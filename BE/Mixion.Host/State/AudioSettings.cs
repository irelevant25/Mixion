using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mixion.Host.State;

/// <summary>
/// User-tunable audio engine configuration. Persisted as a single JSON file
/// in <c>%LOCALAPPDATA%\Mixion\audio-settings.json</c> so values survive a
/// host restart without bouncing through preset files.
///
/// <see cref="CaptureBufferMs"/> and <see cref="RenderLatencyMs"/> govern the
/// regular shared-mode WASAPI path (<c>CaptureDevice</c> / <c>RenderDevice</c>);
/// the low-latency IAudioClient3 path uses the OS-reported minimum engine
/// period regardless of these values, so users who want a deterministic
/// buffer also flip <see cref="PreferLowLatency"/> off.
/// </summary>
public sealed record AudioSettings(
    int  CaptureBufferMs,
    int  RenderLatencyMs,
    bool PreferLowLatency)
{
    public const int  MinBufferMs        = 1;
    public const int  MaxBufferMs        = 200;
    public const int  DefaultCaptureBufferMs  = 10;
    public const int  DefaultRenderLatencyMs  = 10;
    public const bool DefaultPreferLowLatency = true;

    /// <summary>
    /// Default render-side latency in ms when a device is opened in WASAPI
    /// exclusive mode. Exclusive mode bypasses the system mixer so it can
    /// honour periods well below the 10 ms shared-mode floor — 5 ms is a
    /// safe starting point for most consumer hardware.
    /// </summary>
    public const int  DefaultExclusiveRenderLatencyMs = 5;

    /// <summary>
    /// WASAPI <c>MMDevice.ID</c>s the user has opted into exclusive-mode
    /// render. Exclusive mode locks the device to our process — no other
    /// Windows app can play through it while the engine holds it — but
    /// drops a few ms of shared-mode mixer latency in exchange. Empty by
    /// default; toggled per-device via the <c>setDeviceExclusive</c> RPC.
    /// </summary>
    public ImmutableHashSet<string> ExclusiveRenderDeviceIds { get; init; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Per-device override of <see cref="DefaultExclusiveRenderLatencyMs"/>.
    /// Keyed by <c>MMDevice.ID</c>; missing entries fall back to the
    /// default. Exposed so a user with picky hardware can dial up the
    /// exclusive-mode buffer without giving up exclusive mode entirely.
    /// </summary>
    public ImmutableDictionary<string, int> ExclusiveRenderLatencyMsByDeviceId { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    public static readonly AudioSettings Default = new(
        CaptureBufferMs:  DefaultCaptureBufferMs,
        RenderLatencyMs:  DefaultRenderLatencyMs,
        PreferLowLatency: DefaultPreferLowLatency);

    /// <summary>
    /// Clamp buffer values to the allowed range so a corrupt JSON or a
    /// malformed RPC payload can't put us into "0 ms = blow up the audio
    /// engine" territory. Also normalises any null collections so the
    /// engine never has to null-check at lookup time.
    /// </summary>
    public AudioSettings Validated()
    {
        var ids = ExclusiveRenderDeviceIds ?? ImmutableHashSet<string>.Empty;
        var ms  = ExclusiveRenderLatencyMsByDeviceId ?? ImmutableDictionary<string, int>.Empty;

        // Clamp per-device latency overrides too. A user-supplied 0 here
        // would crash WASAPI Initialize the same way 0 ms shared-mode
        // would, so the same bounds apply.
        var clampedMs = ms;
        foreach (var kvp in ms)
        {
            var clamped = Math.Clamp(kvp.Value, MinBufferMs, MaxBufferMs);
            if (clamped != kvp.Value)
                clampedMs = clampedMs.SetItem(kvp.Key, clamped);
        }

        return new AudioSettings(
            CaptureBufferMs:  Math.Clamp(CaptureBufferMs, MinBufferMs, MaxBufferMs),
            RenderLatencyMs:  Math.Clamp(RenderLatencyMs, MinBufferMs, MaxBufferMs),
            PreferLowLatency: PreferLowLatency)
        {
            ExclusiveRenderDeviceIds            = ids,
            ExclusiveRenderLatencyMsByDeviceId  = clampedMs,
        };
    }

    /// <summary>
    /// Latency in ms to request when opening <paramref name="deviceId"/> in
    /// exclusive mode — per-device override if present, otherwise the
    /// default. Caller should still gate on <see cref="IsExclusiveRender"/>.
    /// </summary>
    public int GetExclusiveRenderLatencyMs(string deviceId)
        => ExclusiveRenderLatencyMsByDeviceId.TryGetValue(deviceId, out var ms)
            ? ms
            : DefaultExclusiveRenderLatencyMs;

    /// <summary>True when <paramref name="deviceId"/> is opted into exclusive-mode render.</summary>
    public bool IsExclusiveRender(string deviceId)
        => ExclusiveRenderDeviceIds.Contains(deviceId);
}

/// <summary>
/// Singleton store for the live <see cref="AudioSettings"/>. Loads from disk
/// on construction; writes are atomic (temp file + rename) so a crash mid-
/// save can't leave a half-written settings file. Engine factories read
/// <see cref="Current"/> when building/rebuilding.
/// </summary>
public sealed class AudioSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented           = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string  _filePath;
    private readonly ILogger _logger;
    private AudioSettings    _current;

    public AudioSettings Current => Volatile.Read(ref _current);

    public AudioSettingsStore(string filePath, ILogger logger)
    {
        _filePath = filePath;
        _logger   = logger;
        _current  = Load();
    }

    /// <summary>Default file location: <c>%LOCALAPPDATA%\Mixion\audio-settings.json</c>.</summary>
    public static string DefaultFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mixion",
            "audio-settings.json");

    /// <summary>
    /// Replace the current settings and write them to disk. Caller is
    /// responsible for kicking the engine rebuild — the store doesn't know
    /// about <see cref="Audio.EngineFactory"/> on purpose, to keep the
    /// dependency direction one-way.
    /// </summary>
    public void Update(AudioSettings next)
    {
        var validated = next.Validated();
        Interlocked.Exchange(ref _current, validated);
        try
        {
            Save(validated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist audio settings to {Path}", _filePath);
        }
    }

    private AudioSettings Load()
    {
        if (!File.Exists(_filePath)) return AudioSettings.Default;

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<AudioSettings>(json, JsonOptions);
            return loaded?.Validated() ?? AudioSettings.Default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read audio settings at {Path}; using defaults.", _filePath);
            return AudioSettings.Default;
        }
    }

    private void Save(AudioSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic write: serialize to a temp file in the same directory, then
        // File.Replace to swap. Avoids a half-written file if the host is
        // killed mid-save.
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(_filePath))
            File.Replace(tmp, _filePath, destinationBackupFileName: null);
        else
            File.Move(tmp, _filePath);
    }
}
