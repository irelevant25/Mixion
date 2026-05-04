using Mixion.Host.Audio;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// Device-management RPCs:
/// <list type="bullet">
///   <item><c>listDevices</c> — enumerate active WASAPI capture and render endpoints. Endpoint state on Windows can change between calls (plug/unplug, default-device switch) so we don't cache.</item>
///   <item><c>refreshDevices</c> — re-enumerate physical devices + audio-producing processes, then rebuild the <see cref="MixEngine"/> preserving channel identity and user state. Channels that were here before but aren't now are kept with <c>Available = false</c> so the FE can render them red without losing the slot binding. Returns the new <see cref="StateDto"/> so the FE can hydrate without a follow-up call.</item>
/// </list>
/// </summary>
public static class DeviceHandlers
{
    public static void Register(
        JsonRpcDispatcher dispatcher,
        IEndpointSource source,
        EngineHost engineHost,
        EngineFactory engineFactory)
    {
        dispatcher.Register("listDevices", (_, _, _) =>
        {
            var capture = source.Capture();
            var render  = source.Render();
            return Task.FromResult<object?>(new
            {
                capture,
                render,
            });
        });

        dispatcher.Register("refreshDevices", async (_, _, _) =>
        {
            // Routes through EngineHost.RebuildAsync so a concurrent
            // ProcessHealthMonitor auto-rebind can't race against us. The
            // identity transform (no state edit) means we simply re-enumerate;
            // EngineFactory preserves ids, marks gone channels Available=false,
            // and appends new audio-producing apps.
            var newState = await engineHost.RebuildAsync(engineFactory);
            return (object?)newState.ToDto();
        });
    }
}
