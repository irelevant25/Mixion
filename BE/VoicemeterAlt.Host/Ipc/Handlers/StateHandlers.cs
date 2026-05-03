namespace VoicemeterAlt.Host.Ipc.Handlers;

/// <summary>
/// <c>getState</c> — full snapshot of the current <see cref="State.MixerState"/>
/// in wire-friendly form. Used by the UI to (re-)hydrate after a reconnect or
/// when it explicitly wants a fresh copy. The same shape is returned in
/// <c>/api/session.mixerStateInit</c> so first-paint avoids a round-trip.
/// </summary>
public static class StateHandlers
{
    public static void Register(JsonRpcDispatcher dispatcher, EngineHost host)
    {
        dispatcher.Register("getState", (_, _, _) =>
        {
            var engine = host.Current;
            var dto    = engine?.SnapshotState().ToDto() ?? StateDto.Empty;
            return Task.FromResult<object?>(dto);
        });
    }
}
