using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.State;

namespace VoicemeterAlt.Host.Ipc.Handlers;

/// <summary>
/// <c>listPresets</c>, <c>savePreset</c>, <c>loadPreset</c>, <c>deletePreset</c>,
/// <c>getCurrentPreset</c> — full preset lifecycle over JSON-RPC. State swaps
/// happen via the same <see cref="MixEngine.PublishState"/> path the
/// channel/route handlers use, so a preset load is observed by the audio
/// thread on the next mix tick.
///
/// Slot layout (an FE concept) round-trips through the preset: the FE sends
/// its current layout on save, and the load response carries the resolved
/// slot device ids back so the FE can rebuild its strip rows. The host also
/// caches the resolved layout in <see cref="CurrentPresetState"/> so the
/// <c>/api/session</c> endpoint can hand it to a fresh page load without an
/// extra RPC.
/// </summary>
public static class PresetHandlers
{
    public sealed record NameParam(string Name);

    /// <summary>
    /// FE-shaped slot in the savePreset request: just the live device id (or
    /// null for an unassigned placeholder). The BE enriches each into a
    /// <see cref="PresetSlot"/> by looking up the friendly + interface name
    /// from the endpoint enumerator — that's the stable identity used for
    /// re-resolution when the preset is loaded later.
    /// </summary>
    public sealed record SlotDeviceDto(string? DeviceId);

    public sealed record SavePresetParams(
        string Name,
        SlotDeviceDto[]? InputSlots,
        SlotDeviceDto[]? OutputSlots);

    public static void Register(
        JsonRpcDispatcher dispatcher,
        EngineHost host,
        PresetStore store,
        CurrentPresetState currentPreset,
        IEndpointSource endpoints)
    {
        dispatcher.Register("listPresets", (_, _, _) =>
        {
            var names = store.List();
            return Task.FromResult<object?>(new { presets = names });
        });

        dispatcher.Register("getCurrentPreset", (_, _, _) =>
        {
            return Task.FromResult<object?>(new
            {
                name        = currentPreset.Name,
                inputSlots  = currentPreset.InputSlots,
                outputSlots = currentPreset.OutputSlots,
            });
        });

        dispatcher.Register("savePreset", (paramsEl, _, _) =>
        {
            var p      = JsonRpcDispatcher.RequireParams<SavePresetParams>(paramsEl);
            var name   = NormalizeName(p.Name);
            var engine = RequireEngine(host);
            var state  = engine.SnapshotState();

            var captureLookup = endpoints.Capture().ToDictionary(e => e.Id, StringComparer.Ordinal);
            var renderLookup  = endpoints.Render() .ToDictionary(e => e.Id, StringComparer.Ordinal);

            var inputSlots  = ToPresetSlots(p.InputSlots,  captureLookup);
            var outputSlots = ToPresetSlots(p.OutputSlots, renderLookup);

            var preset = store.Capture(name, state, endpoints, inputSlots, outputSlots);
            try
            {
                store.Save(name, preset);
                store.SetLastPresetName(name);
            }
            catch (ArgumentException ex)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, ex.Message);
            }

            // Saving a preset is also "this is now the active preset" — the
            // resolved slots are exactly the device ids the FE just sent us,
            // passed back so getCurrentPreset reflects the same layout.
            var resolvedInputs  = ToResolvedSlots(p.InputSlots);
            var resolvedOutputs = ToResolvedSlots(p.OutputSlots);
            currentPreset.Set(name, resolvedInputs, resolvedOutputs);

            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("loadPreset", (paramsEl, _, _) =>
        {
            var p      = JsonRpcDispatcher.RequireParams<NameParam>(paramsEl);
            var name   = NormalizeName(p.Name);
            var engine = RequireEngine(host);

            Preset preset;
            try
            {
                preset = store.Load(name);
            }
            catch (FileNotFoundException)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, $"Preset '{name}' not found.");
            }
            catch (InvalidDataException ex)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InternalError, ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, ex.Message);
            }

            var current = engine.SnapshotState();
            var result  = store.Apply(preset, current, endpoints);
            engine.PublishState(result.NextState);

            store.SetLastPresetName(name);
            currentPreset.Set(name, result.InputSlots, result.OutputSlots);

            return Task.FromResult<object?>(new
            {
                ok = true,
                missingDevices = result.MissingDevices,
                inputSlots     = result.InputSlots,
                outputSlots    = result.OutputSlots,
            });
        });

        dispatcher.Register("deletePreset", (paramsEl, _, _) =>
        {
            var p    = JsonRpcDispatcher.RequireParams<NameParam>(paramsEl);
            var name = NormalizeName(p.Name);
            try
            {
                store.Delete(name);
            }
            catch (ArgumentException ex)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, ex.Message);
            }

            // Deleting the active preset detaches the host from it — the FE
            // reflects this via the cleared name on next getCurrentPreset.
            if (string.Equals(currentPreset.Name, name, StringComparison.Ordinal))
                currentPreset.Clear();

            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /// <summary>
    /// Enrich each FE-supplied slot device id into a <see cref="PresetSlot"/>
    /// holding friendly + interface name (the stable identity used for
    /// re-resolution at load time). A slot whose device id is null or no
    /// longer in the live endpoint set becomes an unassigned placeholder
    /// (both names null).
    /// </summary>
    private static PresetSlot[] ToPresetSlots(
        SlotDeviceDto[]? input,
        IReadOnlyDictionary<string, AudioEndpoint> endpointLookup)
    {
        if (input is null || input.Length == 0) return Array.Empty<PresetSlot>();
        var result = new PresetSlot[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var deviceId = input[i].DeviceId;
            if (string.IsNullOrEmpty(deviceId) || !endpointLookup.TryGetValue(deviceId, out var ep))
            {
                result[i] = new PresetSlot(null, null);
            }
            else
            {
                result[i] = new PresetSlot(ep.FriendlyName, ep.InterfaceName);
            }
        }
        return result;
    }

    /// <summary>
    /// Echo the FE-supplied device ids back as <see cref="ResolvedSlot"/>s
    /// for the in-memory "current preset" cache. Used after a save so a
    /// subsequent <c>getCurrentPreset</c> sees the exact layout the user
    /// just stored.
    /// </summary>
    private static ResolvedSlot[] ToResolvedSlots(SlotDeviceDto[]? input)
    {
        if (input is null || input.Length == 0) return Array.Empty<ResolvedSlot>();
        var result = new ResolvedSlot[input.Length];
        for (var i = 0; i < input.Length; i++)
            result[i] = new ResolvedSlot(string.IsNullOrEmpty(input[i].DeviceId) ? null : input[i].DeviceId);
        return result;
    }

    private static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "Preset name must be non-empty.");
        return raw.Trim();
    }

    private static MixEngine RequireEngine(EngineHost host)
        => host.Current
            ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");
}
