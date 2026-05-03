using VoicemeterAlt.Host.Audio;

namespace VoicemeterAlt.Host.Ipc.Handlers;

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

        dispatcher.Register("refreshDevices", (_, _, _) =>
        {
            // Snapshot old engine + state, dispose, rebuild. The rebuild
            // preserves channel ids and matrix positions for surviving
            // channels; ones no longer enumerable end up with Available=false
            // and a null backing source. Brief audio gap (~100-300 ms) while
            // captures/renders re-open — acceptable cost for a user-driven
            // refresh.
            var previous = engineHost.Current?.SnapshotState();

            var oldEngine = engineHost.Current;
            engineHost.Set(null);
            oldEngine?.Dispose();

            var result = engineFactory.Build(previousState: previous);
            engineHost.Set(result.Engine);

            return Task.FromResult<object?>(result.InitialState.ToDto());
        });
    }
}
