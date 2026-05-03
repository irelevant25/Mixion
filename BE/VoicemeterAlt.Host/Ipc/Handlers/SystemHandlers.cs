using System.Reflection;

namespace VoicemeterAlt.Host.Ipc.Handlers;

/// <summary>
/// Process-level RPCs that don't fit the device / state / DSP / preset
/// buckets — currently just <c>ping</c>, which the FE uses to detect liveness
/// and measure round-trip latency over the WebSocket without relying on the
/// HTTP <c>/api/health</c> probe.
/// </summary>
public static class SystemHandlers
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public static void Register(JsonRpcDispatcher dispatcher)
    {
        dispatcher.Register("ping", (_, _, _) => Task.FromResult<object?>(new
        {
            ok = true,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            version = Version,
        }));
    }
}
