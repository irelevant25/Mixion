using Mixion.Host.Audio;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// Device-management RPCs:
/// <list type="bullet">
///   <item><c>listDevices</c> — enumerate active WASAPI capture and render endpoints. Endpoint state on Windows can change between calls (plug/unplug, default-device switch) so we don't cache.</item>
///   <item><c>refreshDevices</c> — the manual last resort. The device watcher normally keeps every channel attached on its own; this re-enumerates everything and rebuilds the <see cref="MixEngine"/> (brief audio gap) preserving channel identity and user state, and brings back apps the user detached this session. Channels that were here before but aren't now are kept with <c>Available = false</c>. Returns the new <see cref="StateDto"/> so the FE can hydrate without a follow-up call.</item>
/// </list>
/// </summary>
public static class DeviceHandlers
{
    public static void Register(
        JsonRpcDispatcher   dispatcher,
        IEndpointSource     source,
        EngineHost          engineHost,
        EngineFactory       engineFactory,
        ProcessSuppressions suppressions)
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

        dispatcher.Register("refreshDevices", async (_, _, ct) =>
        {
            // Routes through EngineHost.RebuildAsync so a concurrent device
            // watcher pass can't race against us. EngineFactory preserves ids,
            // marks gone channels Available=false and appends new devices/apps.
            suppressions.Clear();
            var newState = await engineHost.RebuildAsync(engineFactory, ct: ct);
            return (object?)newState.ToDto();
        });
    }
}
