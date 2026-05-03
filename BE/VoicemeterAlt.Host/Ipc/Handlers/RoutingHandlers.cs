namespace VoicemeterAlt.Host.Ipc.Handlers;

/// <summary>
/// <c>setRoute</c> — toggle one cell of the NxM routing matrix.
///
/// State mutation rule (BE-036): we read the current <see cref="State.MixerState"/>
/// via <see cref="MixEngine.SnapshotState"/>, build a new immutable record
/// with the patched <see cref="State.RoutingMatrix"/>, and publish it via
/// <see cref="MixEngine.PublishState"/>. The mix loop picks it up on its next
/// tick (sub-millisecond at 256-frame blocks @ 48 kHz).
/// </summary>
public static class RoutingHandlers
{
    public sealed record SetRouteParams(int Input, int Output, bool Enabled);

    public static void Register(JsonRpcDispatcher dispatcher, EngineHost host)
    {
        dispatcher.Register("setRoute", (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<SetRouteParams>(paramsEl);

            var engine = host.Current
                ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");

            var current = engine.SnapshotState();

            if (p.Input < 0 || p.Input >= current.Matrix.Inputs)
                throw new JsonRpcException(
                    JsonRpcErrorCode.InvalidParams,
                    $"input out of range: {p.Input} (have {current.Matrix.Inputs})");
            if (p.Output < 0 || p.Output >= current.Matrix.Outputs)
                throw new JsonRpcException(
                    JsonRpcErrorCode.InvalidParams,
                    $"output out of range: {p.Output} (have {current.Matrix.Outputs})");

            var next = current with { Matrix = current.Matrix.With(p.Input, p.Output, p.Enabled) };
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });
    }
}
