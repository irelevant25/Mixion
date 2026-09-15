namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>setRoute</c> — toggle one cell of the NxM routing matrix.
///
/// State mutation rule (BE-036): the new matrix is derived from the latest
/// <see cref="State.MixerState"/> inside <see cref="Audio.MixEngine.UpdateState"/>
/// and published atomically. The mix loop picks it up on its next tick
/// (~2.7 ms at 128-frame blocks @ 48 kHz).
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

            engine.UpdateState(current =>
            {
                if (p.Input < 0 || p.Input >= current.Matrix.Inputs)
                    throw new JsonRpcException(
                        JsonRpcErrorCode.InvalidParams,
                        $"input out of range: {p.Input} (have {current.Matrix.Inputs})");
                if (p.Output < 0 || p.Output >= current.Matrix.Outputs)
                    throw new JsonRpcException(
                        JsonRpcErrorCode.InvalidParams,
                        $"output out of range: {p.Output} (have {current.Matrix.Outputs})");

                return current with { Matrix = current.Matrix.With(p.Input, p.Output, p.Enabled) };
            });

            return Task.FromResult<object?>(new { ok = true });
        });
    }
}
