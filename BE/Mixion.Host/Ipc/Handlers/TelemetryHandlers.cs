using Mixion.Host.Audio;
using Mixion.Host.Web;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>subscribe</c> / <c>unsubscribe</c> — toggle binary VU-meter telemetry
/// on the calling connection.
/// <c>subscribeSpectrum</c> / <c>unsubscribeSpectrum</c> (BE-081) configure
/// the global spectrum tap so the EQ overlay can render a live magnitude
/// spectrum for one channel at a time.
/// </summary>
public static class TelemetryHandlers
{
    public sealed record SubscribeSpectrumParams(string Bus, int Channel);

    public static void Register(JsonRpcDispatcher dispatcher, EngineHost engine, TelemetryHub hub)
    {
        dispatcher.Register("subscribe", (_, conn, _) =>
        {
            conn.IsTelemetrySubscribed = true;
            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("unsubscribe", (_, conn, _) =>
        {
            conn.IsTelemetrySubscribed = false;
            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("subscribeSpectrum", (paramsEl, conn, _) =>
        {
            var p   = JsonRpcDispatcher.RequireParams<SubscribeSpectrumParams>(paramsEl);
            var bus = ParseBusTag(p.Bus);
            if (p.Channel < 0)
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    $"channel must be non-negative; got {p.Channel}.");

            conn.IsSpectrumSubscribed = true;

            // Last subscriber wins — the broadcaster only ever needs to
            // produce one channel's spectrum at a time anyway.
            engine.Current?.SetSpectrumTap(new SpectrumTapConfig(bus, p.Channel));

            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("unsubscribeSpectrum", (_, conn, _) =>
        {
            conn.IsSpectrumSubscribed = false;

            // Drop the global tap when no client is interested anymore.
            if (!AnySpectrumSubscriber(hub))
                engine.Current?.SetSpectrumTap(null);

            return Task.FromResult<object?>(new { ok = true });
        });
    }

    private static SpectrumFrame.BusTag ParseBusTag(string? raw)
    {
        var v = (raw ?? "input").Trim().ToLowerInvariant();
        return v switch
        {
            "input"  or "in"  => SpectrumFrame.BusTag.Input,
            "output" or "out" => SpectrumFrame.BusTag.Output,
            _ => throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                $"unknown bus '{raw}'; expected 'input' or 'output'."),
        };
    }

    private static bool AnySpectrumSubscriber(TelemetryHub hub)
    {
        foreach (var c in hub.Connections) if (c.IsSpectrumSubscribed) return true;
        return false;
    }
}
