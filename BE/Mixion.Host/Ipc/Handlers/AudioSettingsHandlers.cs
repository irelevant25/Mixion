using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>getAudioSettings</c> / <c>setAudioSettings</c> — runtime tuning for the
/// shared-mode WASAPI buffer values (capture / render in ms) and the
/// "prefer low-latency IAudioClient3" toggle. <c>set</c> persists the new
/// values and rebuilds the engine via the same path as <c>refreshDevices</c>,
/// so the user's mixer state (gain, mute, routing) survives untouched.
/// </summary>
public static class AudioSettingsHandlers
{
    public sealed record AudioSettingsDto(
        int  CaptureBufferMs,
        int  RenderLatencyMs,
        bool PreferLowLatency,
        int  MinBufferMs,
        int  MaxBufferMs);

    public sealed record SetAudioSettingsParams(
        int  CaptureBufferMs,
        int  RenderLatencyMs,
        bool PreferLowLatency);

    public static void Register(
        JsonRpcDispatcher    dispatcher,
        AudioSettingsStore   store,
        EngineHost           engineHost,
        EngineFactory        engineFactory)
    {
        dispatcher.Register("getAudioSettings", (_, _, _) =>
        {
            var s = store.Current;
            return Task.FromResult<object?>(new AudioSettingsDto(
                CaptureBufferMs:  s.CaptureBufferMs,
                RenderLatencyMs:  s.RenderLatencyMs,
                PreferLowLatency: s.PreferLowLatency,
                MinBufferMs:      AudioSettings.MinBufferMs,
                MaxBufferMs:      AudioSettings.MaxBufferMs));
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

            store.Update(new AudioSettings(
                CaptureBufferMs:  p.CaptureBufferMs,
                RenderLatencyMs: p.RenderLatencyMs,
                PreferLowLatency: p.PreferLowLatency));

            // Rebuild the engine so devices pick up the new buffer sizes.
            // RebuildAsync preserves the user's mixer state (channels,
            // gain, routing, DSP) — this is the same path refreshDevices
            // takes, just triggered by a settings change.
            await engineHost.RebuildAsync(engineFactory).ConfigureAwait(false);

            var s = store.Current;
            return new AudioSettingsDto(
                CaptureBufferMs:  s.CaptureBufferMs,
                RenderLatencyMs:  s.RenderLatencyMs,
                PreferLowLatency: s.PreferLowLatency,
                MinBufferMs:      AudioSettings.MinBufferMs,
                MaxBufferMs:      AudioSettings.MaxBufferMs);
        });
    }
}
