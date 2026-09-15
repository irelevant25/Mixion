using Mixion.Host.Diagnostics;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// Process-level RPCs that don't fit the device / state / DSP / preset
/// buckets — currently just <c>ping</c>, which the FE uses to detect liveness
/// and measure round-trip latency over the WebSocket without relying on the
/// HTTP <c>/api/health</c> probe.
/// </summary>
public static class SystemHandlers
{
    public static void Register(JsonRpcDispatcher dispatcher)
    {
        dispatcher.Register("ping", (_, _, _) => Task.FromResult<object?>(new
        {
            ok = true,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            version = AppVersion.Current,
        }));
    }
}
