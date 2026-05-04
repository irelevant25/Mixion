using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mixion.Host.Audio;

namespace Mixion.Host.State;

/// <summary>
/// One channel that the loaded preset referenced but the host could not find
/// among the active WASAPI endpoints. Surfaced to the UI as a non-fatal
/// warning so the user can plug the device back in or pick a replacement.
/// </summary>
public sealed record MissingDevice(string Direction, string FriendlyName, string InterfaceName);

/// <summary>
/// Wire shape for one resolved slot returned by <see cref="PresetStore.Apply"/>:
/// the live device id the saved slot mapped to, or null when the slot was
/// intentionally unassigned, when no live device matched the saved
/// friendly+interface name, or when the preset omitted slots (older presets
/// with no layout default to "all unassigned").
/// </summary>
public sealed record ResolvedSlot(string? DeviceId);

/// <summary>
/// One row in <see cref="PresetStore.ListWithMetadata"/>. <see cref="EditedAt"/>
/// always carries a value (= the file's <c>SavedAt</c>); <see cref="CreatedAt"/>
/// falls back to <see cref="EditedAt"/> for legacy presets that predate the
/// dedicated created-at field.
/// </summary>
public sealed record PresetMetadata(string Name, DateTimeOffset CreatedAt, DateTimeOffset EditedAt);

/// <summary>
/// Outcome of a <see cref="PresetStore.Apply"/> call: the (possibly partial)
/// next <see cref="MixerState"/>, slot layout for the FE to apply, and any
/// preset entries that did not match a live device. The caller publishes
/// <see cref="NextState"/> via <see cref="Audio.MixEngine.PublishState"/> and
/// forwards <see cref="MissingDevices"/> + slots to the client.
/// </summary>
public sealed record PresetApplyResult(
    MixerState NextState,
    IReadOnlyList<MissingDevice> MissingDevices,
    ResolvedSlot[] InputSlots,
    ResolvedSlot[] OutputSlots);

/// <summary>
/// Reads and writes <see cref="Preset"/> files under
/// <c>%LOCALAPPDATA%\Mixion\presets\*.json</c>. The store is also
/// responsible for capturing a preset from the live <see cref="MixerState"/>,
/// re-resolving devices when applying a preset back onto a state (BE-062),
/// and remembering the last used preset name so the host can auto-load it
/// on next startup.
/// </summary>
public sealed class PresetStore
{
    private const string LastPresetFileName = ".last";

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private readonly string  _directory;
    private readonly ILogger _logger;

    public PresetStore(string directory, ILogger logger)
    {
        _directory = directory;
        _logger    = logger;
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%\Mixion\presets</c>.</summary>
    public static string DefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mixion",
            "presets");

    public string Directory => _directory;

    /// <summary>
    /// Names of every preset on disk, sorted alphabetically. Files that fail
    /// to parse are skipped with a warning rather than aborting the listing —
    /// one corrupt preset shouldn't hide the rest.
    /// </summary>
    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(_directory)) return Array.Empty<string>();

        var files = System.IO.Directory.GetFiles(_directory, "*.json");
        var names = new List<string>(files.Length);
        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Same set as <see cref="List"/>, enriched with creation and edit
    /// timestamps so the FE can render and sort the preset list. Presets that
    /// fail to parse are skipped (a warning is logged); presets that lack a
    /// <c>createdAt</c> on disk (legacy v1 files) report <c>createdAt = savedAt</c>.
    /// Sorted alphabetically — the FE re-sorts as needed.
    /// </summary>
    public IReadOnlyList<PresetMetadata> ListWithMetadata()
    {
        if (!System.IO.Directory.Exists(_directory)) return Array.Empty<PresetMetadata>();

        var files = System.IO.Directory.GetFiles(_directory, "*.json");
        var rows  = new List<PresetMetadata>(files.Length);
        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) continue;

            try
            {
                var preset = JsonSerializer.Deserialize(File.ReadAllText(path), PresetJsonContext.Default.Preset);
                if (preset is null) continue;
                var edited  = preset.SavedAt;
                var created = preset.CreatedAt ?? preset.SavedAt;
                rows.Add(new PresetMetadata(name, created, edited));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable preset file {Path}", path);
            }
        }
        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>
    /// Capture the current <see cref="MixerState"/> + the FE-supplied slot
    /// layout as a <see cref="Preset"/>. Endpoint <see cref="AudioEndpoint.InterfaceName"/>
    /// is looked up from <paramref name="endpoints"/> by the channel id (the
    /// WASAPI endpoint id stored in <see cref="Channel.Id"/>); we fall back
    /// to an empty interface name when the endpoint is no longer reachable.
    /// </summary>
    public Preset Capture(
        string name,
        MixerState state,
        IEndpointSource endpoints,
        PresetSlot[]? inputSlots,
        PresetSlot[]? outputSlots)
    {
        var captureMap = endpoints.Capture().ToDictionary(e => e.Id, StringComparer.Ordinal);
        var renderMap  = endpoints.Render() .ToDictionary(e => e.Id, StringComparer.Ordinal);

        var inputs  = state.Inputs .Select(c => ToPresetChannel(c, captureMap)).ToArray();
        var outputs = state.Outputs.Select(c => ToPresetChannel(c, renderMap )).ToArray();

        var matrix = new bool[state.Matrix.Inputs][];
        for (var i = 0; i < state.Matrix.Inputs; i++)
        {
            var row = new bool[state.Matrix.Outputs];
            for (var o = 0; o < state.Matrix.Outputs; o++) row[o] = state.Matrix[i, o];
            matrix[i] = row;
        }

        var now = DateTimeOffset.UtcNow;
        return new Preset(
            SchemaVersion: Preset.CurrentSchemaVersion,
            Name:          name,
            SavedAt:       now,
            Inputs:        inputs,
            Outputs:       outputs,
            Matrix:        matrix,
            InputSlots:    inputSlots  ?? Array.Empty<PresetSlot>(),
            OutputSlots:   outputSlots ?? Array.Empty<PresetSlot>(),
            CreatedAt:     now);
    }

    /// <summary>
    /// Save (or overwrite) <paramref name="preset"/> under <paramref name="name"/>.
    /// On overwrite the existing file's <see cref="Preset.CreatedAt"/> is preserved
    /// so "created" reflects the original save, not the latest edit. A corrupt
    /// existing file is treated as missing.
    /// </summary>
    public void Save(string name, Preset preset)
    {
        EnsureValidName(name);
        System.IO.Directory.CreateDirectory(_directory);
        var path = ResolvePath(name);

        var toSave = preset with { CreatedAt = ResolveCreatedAt(path, preset) };

        var json = JsonSerializer.Serialize(toSave, PresetJsonContext.Default.Preset);
        // Atomic write so a crash mid-save can't leave a half-written file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);

        _logger.LogInformation("Saved preset '{Name}' to {Path}", name, path);
    }

    /// <summary>
    /// Rename a preset on disk. Updates the file name, the <c>Name</c> field
    /// inside the JSON, and the "last preset" pointer if it referenced the old
    /// name. <see cref="Preset.CreatedAt"/> and <see cref="Preset.SavedAt"/>
    /// are preserved — renaming isn't an edit. Throws when the source is
    /// missing or the target already exists.
    /// </summary>
    public void Rename(string oldName, string newName)
    {
        EnsureValidName(oldName);
        EnsureValidName(newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;

        var oldPath = ResolvePath(oldName);
        var newPath = ResolvePath(newName);
        if (!File.Exists(oldPath))
            throw new FileNotFoundException($"Preset '{oldName}' not found.", oldPath);
        if (File.Exists(newPath))
            throw new IOException($"A preset named '{newName}' already exists.");

        var preset = Load(oldName);
        var renamed = preset with { Name = newName };

        var json = JsonSerializer.Serialize(renamed, PresetJsonContext.Default.Preset);
        var tmp  = newPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, newPath, overwrite: false);
        File.Delete(oldPath);

        if (string.Equals(GetLastPresetName(), oldName, StringComparison.Ordinal))
            SetLastPresetName(newName);

        _logger.LogInformation("Renamed preset '{Old}' → '{New}'", oldName, newName);
    }

    /// <summary>
    /// Resolve the <see cref="Preset.CreatedAt"/> to write: the existing
    /// file's value if any (so overwrites preserve creation), the legacy
    /// <see cref="Preset.SavedAt"/> for v1 files, or the in-memory preset's
    /// own value (typically "now" from <see cref="Capture"/>).
    /// </summary>
    private DateTimeOffset ResolveCreatedAt(string path, Preset preset)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize(File.ReadAllText(path), PresetJsonContext.Default.Preset);
                if (existing is not null)
                    return existing.CreatedAt ?? existing.SavedAt;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Existing preset at {Path} unreadable; treating overwrite as new", path);
            }
        }
        return preset.CreatedAt ?? preset.SavedAt;
    }

    /// <summary>
    /// Read a preset by name. Throws <see cref="FileNotFoundException"/> if
    /// no such preset exists, or <see cref="InvalidDataException"/> if the
    /// file is unreadable / has the wrong schema version.
    /// </summary>
    public Preset Load(string name)
    {
        EnsureValidName(name);
        var path = ResolvePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Preset '{name}' not found.", path);

        var json = File.ReadAllText(path);
        Preset? preset;
        try
        {
            preset = JsonSerializer.Deserialize(json, PresetJsonContext.Default.Preset);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Preset '{name}' is not valid JSON: {ex.Message}", ex);
        }

        if (preset is null)
            throw new InvalidDataException($"Preset '{name}' is empty.");
        if (preset.SchemaVersion != Preset.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Preset '{name}' uses schema version {preset.SchemaVersion}; expected {Preset.CurrentSchemaVersion}.");

        return preset;
    }

    /// <summary>Delete a preset. No-op if it doesn't exist.</summary>
    public void Delete(string name)
    {
        EnsureValidName(name);
        var path = ResolvePath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted preset '{Name}' at {Path}", name, path);
        }

        // Drop the "last" pointer if it pointed at this preset — a stale
        // pointer would surface a confusing "preset missing" warning on
        // next startup.
        if (string.Equals(GetLastPresetName(), name, StringComparison.Ordinal))
            ClearLastPresetName();
    }

    /// <summary>
    /// BE-062: project a preset onto the current <see cref="MixerState"/>.
    ///
    /// For each preset channel, find the best matching live channel (by id,
    /// then friendly + interface name, then friendly name alone). Apply
    /// gain/mute/solo to that live channel and remap the matrix into the
    /// current channel ordering. Preset channels with no live match are
    /// reported via <see cref="PresetApplyResult.MissingDevices"/> — the
    /// state still applies for the channels that did match (warn, don't
    /// throw).
    /// </summary>
    public PresetApplyResult Apply(Preset preset, MixerState current, IEndpointSource endpoints)
    {
        var captureEndpoints = endpoints.Capture();
        var renderEndpoints  = endpoints.Render();

        var (inputMap,  inputMissing)  = ResolveBus(preset.Inputs,  current.Inputs,  captureEndpoints);
        var (outputMap, outputMissing) = ResolveBus(preset.Outputs, current.Outputs, renderEndpoints);

        var nextInputs  = ApplyChannelPatches(current.Inputs,  preset.Inputs,  inputMap);
        var nextOutputs = ApplyChannelPatches(current.Outputs, preset.Outputs, outputMap);

        var nextMatrix = RemapMatrix(
            preset.Matrix,
            current.Matrix.Inputs,
            current.Matrix.Outputs,
            inputMap,
            outputMap);

        var inputSlots  = ResolveSlots(preset.InputSlots,  current.Inputs,  captureEndpoints);
        var outputSlots = ResolveSlots(preset.OutputSlots, current.Outputs, renderEndpoints);

        var missing = new List<MissingDevice>(inputMissing.Count + outputMissing.Count);
        foreach (var c in inputMissing)
            missing.Add(new MissingDevice("input", c.FriendlyName, c.InterfaceName));
        foreach (var c in outputMissing)
            missing.Add(new MissingDevice("output", c.FriendlyName, c.InterfaceName));

        var nextState = new MixerState(nextInputs, nextOutputs, nextMatrix);
        return new PresetApplyResult(nextState, missing, inputSlots, outputSlots);
    }

    /// <summary>
    /// Read the persisted "last preset" pointer. Returns null if the pointer
    /// file is absent or unreadable. The pointer is just the preset name on a
    /// single line — no JSON envelope.
    /// </summary>
    public string? GetLastPresetName()
    {
        try
        {
            var path = LastPresetPath();
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read last-preset pointer");
            return null;
        }
    }

    /// <summary>
    /// Persist the "last preset" pointer to disk. Called on every successful
    /// save and load so the host auto-loads the latest preset next start-up.
    /// </summary>
    public void SetLastPresetName(string name)
    {
        try
        {
            EnsureValidName(name);
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(LastPresetPath(), name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write last-preset pointer for '{Name}'", name);
        }
    }

    /// <summary>Remove the "last preset" pointer (e.g. after deleting that preset).</summary>
    public void ClearLastPresetName()
    {
        try
        {
            var path = LastPresetPath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear last-preset pointer");
        }
    }

    // ----- helpers -----

    /// <summary>
    /// For each preset channel, return the index of the matching live channel
    /// (or -1 when no match). Also returns the preset channels that had no
    /// match so the caller can warn the user. Matching prefers exact id, then
    /// friendly + interface name, then friendly name alone.
    /// </summary>
    private static (int[] map, IReadOnlyList<PresetChannel> missing) ResolveBus(
        PresetChannel[] presetChannels,
        ImmutableArray<Channel> current,
        IReadOnlyList<AudioEndpoint> currentEndpoints)
    {
        var map     = new int[presetChannels.Length];
        var missing = new List<PresetChannel>();
        var taken   = new bool[current.Length];

        // Same length and order as `current` — used for friendly + interface
        // name matching when the id has changed.
        var endpointById = currentEndpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);

        for (var p = 0; p < presetChannels.Length; p++)
        {
            var pc = presetChannels[p];
            var match = -1;

            // 1) exact id
            for (var i = 0; i < current.Length; i++)
            {
                if (taken[i]) continue;
                if (string.Equals(current[i].Id, pc.DeviceId, StringComparison.Ordinal))
                {
                    match = i;
                    break;
                }
            }

            // 2) friendly + interface name
            if (match < 0 && !string.IsNullOrEmpty(pc.InterfaceName))
            {
                for (var i = 0; i < current.Length; i++)
                {
                    if (taken[i]) continue;
                    if (!endpointById.TryGetValue(current[i].Id, out var ep)) continue;
                    if (string.Equals(ep.FriendlyName,  pc.FriendlyName,  StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ep.InterfaceName, pc.InterfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = i;
                        break;
                    }
                }
            }

            // 3) friendly name only
            if (match < 0)
            {
                for (var i = 0; i < current.Length; i++)
                {
                    if (taken[i]) continue;
                    if (string.Equals(current[i].Name, pc.FriendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = i;
                        break;
                    }
                }
            }

            map[p] = match;
            if (match >= 0)
            {
                taken[match] = true;
            }
            else
            {
                missing.Add(pc);
            }
        }

        return (map, missing);
    }

    /// <summary>
    /// Resolve a row of <see cref="PresetSlot"/> entries against live
    /// channels. The slot layout itself doesn't contribute to
    /// <c>MissingDevices</c>: any device a slot referenced was already
    /// reported as a missing channel above (slots can't reference devices
    /// the channel set doesn't). We simply hand the FE the resolved id (or
    /// null) so it can rebuild its strip row in the saved order.
    /// </summary>
    private static ResolvedSlot[] ResolveSlots(
        PresetSlot[] presetSlots,
        ImmutableArray<Channel> current,
        IReadOnlyList<AudioEndpoint> currentEndpoints)
    {
        if (presetSlots.Length == 0) return Array.Empty<ResolvedSlot>();

        var endpointById = currentEndpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var result = new ResolvedSlot[presetSlots.Length];

        for (var s = 0; s < presetSlots.Length; s++)
        {
            var slot = presetSlots[s];
            // Empty slot (placeholder) — keep the position but no device.
            if (string.IsNullOrEmpty(slot.FriendlyName))
            {
                result[s] = new ResolvedSlot(null);
                continue;
            }

            string? matchedId = null;

            // friendly + interface
            if (!string.IsNullOrEmpty(slot.InterfaceName))
            {
                for (var i = 0; i < current.Length; i++)
                {
                    if (!endpointById.TryGetValue(current[i].Id, out var ep)) continue;
                    if (string.Equals(ep.FriendlyName,  slot.FriendlyName,  StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ep.InterfaceName, slot.InterfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedId = current[i].Id;
                        break;
                    }
                }
            }

            // friendly only
            if (matchedId is null)
            {
                for (var i = 0; i < current.Length; i++)
                {
                    if (string.Equals(current[i].Name, slot.FriendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedId = current[i].Id;
                        break;
                    }
                }
            }

            result[s] = new ResolvedSlot(matchedId);
        }

        return result;
    }

    private static ImmutableArray<Channel> ApplyChannelPatches(
        ImmutableArray<Channel> current,
        PresetChannel[] presetChannels,
        int[] presetToCurrent)
    {
        if (current.IsEmpty) return current;

        var builder = current.ToBuilder();
        for (var p = 0; p < presetChannels.Length; p++)
        {
            var ci = presetToCurrent[p];
            if (ci < 0) continue;

            var pc = presetChannels[p];
            var ch = builder[ci];
            // Preserve the live device id + name; only copy operational +
            // DSP state. Pan / Gate / Compressor / Eq round-trip from BE-070
            // so a loaded preset restores the user's full chain.
            builder[ci] = new Channel(
                ch.Id, ch.Name,
                pc.GainDb, pc.Muted, pc.Soloed,
                pc.Pan, pc.Gate, pc.Compressor, pc.Eq);
        }

        return builder.ToImmutable();
    }

    private static RoutingMatrix RemapMatrix(
        bool[][] presetMatrix,
        int currentInputs,
        int currentOutputs,
        int[] presetInputToCurrent,
        int[] presetOutputToCurrent)
    {
        // Start from a clean matrix sized to the live state. Cells corresponding
        // to live channels not mentioned in the preset stay false — better than
        // inheriting whatever was routed before the load (the preset is the
        // user's stated intent).
        var matrix = new RoutingMatrix(currentInputs, currentOutputs);

        var presetInputCount  = Math.Min(presetMatrix.Length, presetInputToCurrent.Length);
        for (var pi = 0; pi < presetInputCount; pi++)
        {
            var ci = presetInputToCurrent[pi];
            if (ci < 0 || ci >= currentInputs) continue;

            var row = presetMatrix[pi];
            if (row is null) continue;

            var presetOutputCount = Math.Min(row.Length, presetOutputToCurrent.Length);
            for (var po = 0; po < presetOutputCount; po++)
            {
                if (!row[po]) continue;
                var co = presetOutputToCurrent[po];
                if (co < 0 || co >= currentOutputs) continue;
                matrix = matrix.With(ci, co, true);
            }
        }

        return matrix;
    }

    private static PresetChannel ToPresetChannel(Channel ch, IReadOnlyDictionary<string, AudioEndpoint> endpoints)
    {
        var iface = endpoints.TryGetValue(ch.Id, out var ep) ? ep.InterfaceName : string.Empty;
        return new PresetChannel(
            ch.Id, ch.Name, iface,
            ch.GainDb, ch.Muted, ch.Soloed,
            ch.Pan, ch.Gate, ch.Compressor, ch.Eq);
    }

    private string ResolvePath(string name)     => Path.Combine(_directory, name + ".json");
    private string LastPresetPath()              => Path.Combine(_directory, LastPresetFileName);

    /// <summary>
    /// Reject names that would escape the presets folder, contain path
    /// separators, or include filesystem-invalid characters. Cheap defence
    /// against an accidentally-bad name from the UI.
    /// </summary>
    private static void EnsureValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Preset name must be non-empty.", nameof(name));
        if (name.Length > 64)
            throw new ArgumentException("Preset name too long (max 64).", nameof(name));
        if (name.IndexOfAny(InvalidNameChars) >= 0
            || name.Contains('/') || name.Contains('\\')
            || name == "." || name == "..")
        {
            throw new ArgumentException($"Preset name contains invalid characters: '{name}'.", nameof(name));
        }
    }
}
