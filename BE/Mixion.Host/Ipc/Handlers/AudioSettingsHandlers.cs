using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>getAudioSettings</c> / <c>setAudioSettings</c> / <c>setDeviceExclusive</c> —
/// runtime tuning for the shared-mode WASAPI buffer values, the
/// "prefer low-latency IAudioClient3" toggle, and per-device WASAPI
/// exclusive-mode opt-in. <c>set*</c> handlers persist the new values and
/// rebuild the engine via the same path as <c>refreshDevices</c>, so the
/// user's mixer state (gain, mute, routing) survives untouched.
/// </summary>
public static class AudioSettingsHandlers
{
    public sealed record AudioSettingsDto(
        int      CaptureBufferMs,
        int      RenderLatencyMs,
        bool     PreferLowLatency,
        int      MinBufferMs,
        int      MaxBufferMs,
        int      DefaultExclusiveRenderLatencyMs,
        string[] ExclusiveRenderDeviceIds);

    public sealed record SetAudioSettingsParams(
        int  CaptureBufferMs,
        int  RenderLatencyMs,
        bool PreferLowLatency);

    /// <summary>
    /// Toggle one render device's WASAPI exclusive-mode opt-in. <c>LatencyMs</c>
    /// is honoured only when <c>Exclusive</c> is true; clearing the flag
    /// also drops any per-device latency override the user had set.
    /// </summary>
    public sealed record SetDeviceExclusiveParams(
        string DeviceId,
        bool   Exclusive,
        int?   LatencyMs);

    public static void Register(
        JsonRpcDispatcher    dispatcher,
        AudioSettingsStore   store,
        EngineHost           engineHost,
        EngineFactory        engineFactory)
    {
        dispatcher.Register("getAudioSettings", (_, _, _) =>
        {
            return Task.FromResult<object?>(BuildDto(store.Current));
        });

        dispatcher.Register("setAudioSettings", async (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<SetAudioSettingsParams>(paramsEl);

            if (p.CaptureBufferMs < AudioSettings.MinBufferMs || p.CaptureBufferMs > AudioSettings.MaxBufferMs)
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    $"captureBufferMs must be between {AudioSettings.MinBufferMs} and {AudioSettings.MaxBufferMs} ms.");
            if (p.RenderLatencyMs < AudioSettings.MinBufferMs || p.RenderLatencyMs > AudioSettings.MaxBufferMs)
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    $"renderLatencyMs must be between {AudioSettings.MinBufferMs} and {AudioSettings.MaxBufferMs} ms.");

            // Preserve any per-device exclusive state when buffer-size
            // settings change — those are independent toggles and the user
            // shouldn't have to re-flip exclusive every time they tune ms.
            var prev = store.Current;
            store.Update(new AudioSettings(
                CaptureBufferMs:  p.CaptureBufferMs,
                RenderLatencyMs:  p.RenderLatencyMs,
                PreferLowLatency: p.PreferLowLatency)
            {
                ExclusiveRenderDeviceIds            = prev.ExclusiveRenderDeviceIds,
                ExclusiveRenderLatencyMsByDeviceId  = prev.ExclusiveRenderLatencyMsByDeviceId,
            });

            // Rebuild the engine so devices pick up the new buffer sizes.
            // RebuildAsync preserves the user's mixer state (channels,
            // gain, routing, DSP) — this is the same path refreshDevices
            // takes, just triggered by a settings change.
            await engineHost.RebuildAsync(engineFactory).ConfigureAwait(false);

            return BuildDto(store.Current);
        });

        dispatcher.Register("setDeviceExclusive", async (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<SetDeviceExclusiveParams>(paramsEl);
            if (string.IsNullOrWhiteSpace(p.DeviceId))
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    "deviceId is required.");
            if (p.Exclusive && p.LatencyMs is { } ms
                && (ms < AudioSettings.MinBufferMs || ms > AudioSettings.MaxBufferMs))
            {
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    $"latencyMs must be between {AudioSettings.MinBufferMs} and {AudioSettings.MaxBufferMs} ms.");
            }

            var prev = store.Current;
            var ids  = prev.ExclusiveRenderDeviceIds;
            var lat  = prev.ExclusiveRenderLatencyMsByDeviceId;

            if (p.Exclusive)
            {
                ids = ids.Add(p.DeviceId);
                if (p.LatencyMs is { } latMs)
                    lat = lat.SetItem(p.DeviceId, latMs);
            }
            else
            {
                ids = ids.Remove(p.DeviceId);
                // Clearing exclusive on a device also drops its latency
                // override so the next opt-in starts from the default.
                lat = lat.Remove(p.DeviceId);
            }

            store.Update(prev with
            {
                ExclusiveRenderDeviceIds            = ids,
                ExclusiveRenderLatencyMsByDeviceId  = lat,
            });

            await engineHost.RebuildAsync(engineFactory).ConfigureAwait(false);
            return BuildDto(store.Current);
        });
    }

    private static AudioSettingsDto BuildDto(AudioSettings s) => new(
        CaptureBufferMs:                  s.CaptureBufferMs,
        RenderLatencyMs:                  s.RenderLatencyMs,
        PreferLowLatency:                 s.PreferLowLatency,
        MinBufferMs:                      AudioSettings.MinBufferMs,
        MaxBufferMs:                      AudioSettings.MaxBufferMs,
        DefaultExclusiveRenderLatencyMs:  AudioSettings.DefaultExclusiveRenderLatencyMs,
        ExclusiveRenderDeviceIds:         s.ExclusiveRenderDeviceIds.ToArray());
}
