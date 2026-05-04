using System.Collections.Immutable;
using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>getState</c> — full snapshot of the current <see cref="State.MixerState"/>
/// in wire-friendly form. Used by the UI to (re-)hydrate after a reconnect or
/// when it explicitly wants a fresh copy. The same shape is returned in
/// <c>/api/session.mixerStateInit</c> so first-paint avoids a round-trip.
///
/// <c>resetMixerState</c> — wipe operational + DSP state across every channel
/// (gain, mute, solo, pan, gate, compressor, EQ) and clear the routing matrix.
/// Does **not** remove channels — the device/process inventory survives, only
/// the user's tweaks are zeroed. Powers the FE "New" button so a fresh blank
/// preset starts from a clean slate.
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

        dispatcher.Register("resetMixerState", (_, _, _) =>
        {
            var engine = host.Current
                ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");

            var current = engine.SnapshotState();
            var next = new MixerState(
                Inputs:  ResetChannels(current.Inputs),
                Outputs: ResetChannels(current.Outputs),
                Matrix:  new RoutingMatrix(current.Inputs.Length, current.Outputs.Length));

            engine.PublishState(next);
            return Task.FromResult<object?>(next.ToDto());
        });
    }

    /// <summary>
    /// Rebuild each channel with default operational + DSP state. Identity
    /// (id, name, availability) is preserved so the mixer keeps tracking the
    /// same physical device or process loopback. Gain uses <c>0 dB</c> via
    /// <see cref="Channel.WithGainDb"/> so the cached linear gain is correct.
    /// </summary>
    private static ImmutableArray<Channel> ResetChannels(ImmutableArray<Channel> source)
    {
        if (source.IsDefaultOrEmpty) return source;
        var builder = ImmutableArray.CreateBuilder<Channel>(source.Length);
        foreach (var ch in source)
        {
            builder.Add(new Channel(
                Id:         ch.Id,
                Name:       ch.Name,
                GainDb:     0f,
                Muted:      false,
                Soloed:     false,
                Pan:        0f,
                Gate:       null,
                Compressor: null,
                Eq:         null,
                Available:  ch.Available));
        }
        return builder.MoveToImmutable();
    }
}
