using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>listPresets</c>, <c>savePreset</c>, <c>loadPreset</c>, <c>deletePreset</c>,
/// <c>getCurrentPreset</c> — full preset lifecycle over JSON-RPC. State swaps
/// go through <see cref="MixEngine.UpdateState"/> like every other control RPC,
/// so a preset load is observed by the audio thread on the next mix tick.
///
/// Slot layout (an FE concept) round-trips through the preset: the FE sends
/// its current layout on save, and the load response carries the resolved
/// slot device ids back so the FE can rebuild its strip rows. Slots persist
/// their channel id, so a slot bound to an app keeps pointing at that app
/// even when it isn't running at load time — the channel comes back as an
/// unavailable placeholder and the device watcher attaches it once the app
/// starts. The host also caches the resolved layout in
/// <see cref="CurrentPresetState"/> so the <c>/api/session</c> endpoint can
/// hand it to a fresh page load without an extra RPC.
/// </summary>
public static class PresetHandlers
{
    public sealed record NameParam(string Name);

    public sealed record RenamePresetParams(string OldName, string NewName);

    /// <summary>
    /// FE-shaped slot in the savePreset request: just the live channel id (or
    /// null for an unassigned placeholder). The BE enriches each into a
    /// <see cref="PresetSlot"/> with friendly + interface name for matching on
    /// machines where the id differs.
    /// </summary>
    public sealed record SlotDeviceDto(string? DeviceId);

    public sealed record SavePresetParams(
        string Name,
        SlotDeviceDto[]? InputSlots,
        SlotDeviceDto[]? OutputSlots);

    public static void Register(
        JsonRpcDispatcher   dispatcher,
        EngineHost          host,
        EngineFactory       factory,
        PresetStore         store,
        CurrentPresetState  currentPreset,
        IEndpointSource     endpoints,
        ProcessSuppressions suppressions,
        AudioDeviceWatcher? watcher = null)
    {
        dispatcher.Register("listPresets", (_, _, _) =>
        {
            var rows = store.ListWithMetadata();
            // Camel-case wire shape; one entry per preset on disk. The FE
            // sorts as needed (the spec is "newest edited on top").
            var presets = rows.Select(r => new
            {
                name      = r.Name,
                createdAt = r.CreatedAt,
                editedAt  = r.EditedAt,
            }).ToArray();
            return Task.FromResult<object?>(new { presets });
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

            var inputSlots  = PresetStore.CaptureSlots(SlotIds(p.InputSlots),  state.Inputs,  endpoints.Capture());
            var outputSlots = PresetStore.CaptureSlots(SlotIds(p.OutputSlots), state.Outputs, endpoints.Render());

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

        dispatcher.Register("loadPreset", async (paramsEl, _, ct) =>
        {
            var p    = JsonRpcDispatcher.RequireParams<NameParam>(paramsEl);
            var name = NormalizeName(p.Name);

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

            var result = await ApplyPresetAsync(host, factory, store, suppressions, preset, endpoints, ct);

            store.SetLastPresetName(name);
            currentPreset.Set(name, result.InputSlots, result.OutputSlots);

            // Placeholder channels for apps / devices the preset uses may be
            // attachable right away — don't wait for the next watcher tick.
            watcher?.RequestReconcile();

            return new
            {
                ok = true,
                missingDevices = result.MissingDevices,
                inputSlots     = result.InputSlots,
                outputSlots    = result.OutputSlots,
            };
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

        dispatcher.Register("renamePreset", (paramsEl, _, _) =>
        {
            var p   = JsonRpcDispatcher.RequireParams<RenamePresetParams>(paramsEl);
            var old = NormalizeName(p.OldName);
            var nw  = NormalizeName(p.NewName);
            try
            {
                store.Rename(old, nw);
            }
            catch (FileNotFoundException)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, $"Preset '{old}' not found.");
            }
            catch (IOException ex)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, ex.Message);
            }

            // Keep the in-memory "current preset" pointer aligned with disk so
            // the FE doesn't see a stale name after rename.
            if (string.Equals(currentPreset.Name, old, StringComparison.Ordinal))
                currentPreset.Set(nw, currentPreset.InputSlots, currentPreset.OutputSlots);

            return Task.FromResult<object?>(new { ok = true });
        });

        // Detach the host from whichever preset it last loaded/saved without
        // touching mixer state. FE "New" calls this so a subsequent reload
        // doesn't auto-bind back to the previous preset.
        dispatcher.Register("clearCurrentPreset", (_, _, _) =>
        {
            currentPreset.Clear();
            store.ClearLastPresetName();
            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /// <summary>
    /// Project <paramref name="preset"/> onto the running engine. Placeholder
    /// channels the preset needs are attached as empty slots (or, when the
    /// engine has no spare slots, the engine is rebuilt around the new state)
    /// before the state is published. Used by <c>loadPreset</c> and by the
    /// startup auto-load.
    /// </summary>
    public static async Task<PresetApplyResult> ApplyPresetAsync(
        EngineHost          host,
        EngineFactory       factory,
        PresetStore         store,
        ProcessSuppressions suppressions,
        Preset              preset,
        IEndpointSource     endpoints,
        CancellationToken   ct = default)
    {
        // Enumerate endpoints once, up front, so applying the preset inside the
        // state lock below is pure computation.
        var cachedEndpoints = new CachedEndpoints(endpoints.Capture(), endpoints.Render());
        PresetApplyResult? applied = null;

        await host.ChangeTopologyAsync(factory, engine =>
        {
            // Channel ids only change under the topology lock we hold, so this
            // preview settles which placeholder slots the preset needs.
            var preview = store.Apply(preset, engine.SnapshotState(), cachedEndpoints);
            var next    = preview.NextState;

            // Apps the preset uses are wanted, even if the user detached them
            // earlier in this session.
            foreach (var ch in next.Inputs)
                if (ProcessChannelId.TryGetProcessName(ch.Id, out var app)) suppressions.Remove(app);

            var addedInputs  = next.Inputs.Length  - engine.InputCount;
            var addedOutputs = next.Outputs.Length - engine.OutputCount;
            if (addedInputs  > engine.InputCapacity  - engine.InputCount ||
                addedOutputs > engine.OutputCapacity - engine.OutputCount)
            {
                applied = preview;
                return TopologyOutcome.Rebuild(next);
            }

            for (var i = engine.InputCount;  i < next.Inputs.Length;  i++) engine.TryAppendCapture(null, next.Inputs[i]);
            for (var o = engine.OutputCount; o < next.Outputs.Length; o++) engine.TryAppendRender(null, next.Outputs[o]);

            // Apply again on the latest state, inside the state lock, so a gain or
            // route change that landed after the preview survives on channels the
            // preset doesn't cover.
            engine.UpdateState(s =>
            {
                applied = store.Apply(preset, s, cachedEndpoints);
                return applied.NextState;
            });

            return addedInputs > 0 || addedOutputs > 0 ? TopologyOutcome.ChangedState : TopologyOutcome.Unchanged;
        }, ct);

        return applied
            ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");
    }

    /// <summary>Endpoint lists enumerated once, so applying a preset under a lock doesn't touch COM.</summary>
    private sealed class CachedEndpoints : IEndpointSource
    {
        private readonly IReadOnlyList<AudioEndpoint> _capture;
        private readonly IReadOnlyList<AudioEndpoint> _render;

        public CachedEndpoints(IReadOnlyList<AudioEndpoint> capture, IReadOnlyList<AudioEndpoint> render)
        {
            _capture = capture;
            _render  = render;
        }

        public IReadOnlyList<AudioEndpoint> Capture() => _capture;
        public IReadOnlyList<AudioEndpoint> Render()  => _render;
    }

    private static string?[] SlotIds(SlotDeviceDto[]? slots)
        => slots is null ? Array.Empty<string?>() : slots.Select(s => s.DeviceId).ToArray();

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
