using System.Collections.Immutable;
using VoicemeterAlt.Host.State;

namespace VoicemeterAlt.Host.Ipc.Handlers;

/// <summary>
/// <c>setGain</c>, <c>setMute</c>, <c>setSolo</c> — per-channel control RPCs.
///
/// State mutation rule (BE-036): we read the current <see cref="MixerState"/>
/// via <see cref="Audio.MixEngine.SnapshotState"/>, build a new immutable
/// record with the patched <see cref="Channel"/>, and publish it via
/// <see cref="Audio.MixEngine.PublishState"/>. The mix loop picks it up on
/// its next tick. <c>setGain</c> converts dB to linear once on state swap so
/// the audio path never calls <see cref="Math.Pow"/>.
///
/// Channels live in two buses (input and output); since indices overlap, all
/// three RPCs accept an optional <c>bus</c> ("input" | "output"). Defaults to
/// "input" when omitted.
/// </summary>
public static class ChannelHandlers
{
    private const string DefaultBus = "input";

    public sealed record SetGainParams(int Channel, float Db, string? Bus = null);
    public sealed record SetMuteParams(int Channel, bool Muted, string? Bus = null);
    public sealed record SetSoloParams(int Channel, bool Soloed, string? Bus = null);

    public static void Register(JsonRpcDispatcher dispatcher, EngineHost host)
    {
        dispatcher.Register("setGain", (paramsEl, _, _) =>
        {
            var p      = JsonRpcDispatcher.RequireParams<SetGainParams>(paramsEl);
            var engine = RequireEngine(host);
            var state  = engine.SnapshotState();
            var bus    = ParseBus(p.Bus);

            var next = ApplyChannelChange(state, bus, p.Channel,
                ch => ch.WithGainDb(ClampDb(p.Db)));

            engine.PublishState(next);
            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("setMute", (paramsEl, _, _) =>
        {
            var p      = JsonRpcDispatcher.RequireParams<SetMuteParams>(paramsEl);
            var engine = RequireEngine(host);
            var state  = engine.SnapshotState();
            var bus    = ParseBus(p.Bus);

            var next = ApplyChannelChange(state, bus, p.Channel,
                ch => ch with { Muted = p.Muted });

            engine.PublishState(next);
            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("setSolo", (paramsEl, _, _) =>
        {
            var p      = JsonRpcDispatcher.RequireParams<SetSoloParams>(paramsEl);
            var engine = RequireEngine(host);
            var state  = engine.SnapshotState();
            var bus    = ParseBus(p.Bus);

            var next = ApplyChannelChange(state, bus, p.Channel,
                ch => ch with { Soloed = p.Soloed });

            engine.PublishState(next);
            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /// <summary>
    /// Build a new <see cref="MixerState"/> with the channel at <paramref name="index"/>
    /// in the given <paramref name="bus"/> replaced by <paramref name="patch"/>(old).
    /// Throws <see cref="JsonRpcException"/> on invalid index.
    /// </summary>
    public static MixerState ApplyChannelChange(
        MixerState state,
        Bus bus,
        int index,
        Func<Channel, Channel> patch)
    {
        var bank = bus == Bus.Input ? state.Inputs : state.Outputs;
        if (index < 0 || index >= bank.Length)
            throw new JsonRpcException(
                JsonRpcErrorCode.InvalidParams,
                $"channel out of range: {index} (have {bank.Length} {bus.ToString().ToLowerInvariant()}s)");

        var updated = patch(bank[index]);
        var nextBank = bank.SetItem(index, updated);

        return bus == Bus.Input
            ? state with { Inputs  = nextBank }
            : state with { Outputs = nextBank };
    }

    public enum Bus { Input, Output }

    /// <summary>
    /// Parse an optional <c>bus</c> param ("input" / "output"), defaulting
    /// to <c>"input"</c> when omitted. Shared with <see cref="DspHandlers"/>
    /// so the DSP RPCs accept the same bus discriminator as gain/mute/solo.
    /// </summary>
    public static Bus ParseBus(string? raw)
    {
        var value = string.IsNullOrEmpty(raw) ? DefaultBus : raw;
        return value.ToLowerInvariant() switch
        {
            "input"  => Bus.Input,
            "in"     => Bus.Input,
            "output" => Bus.Output,
            "out"    => Bus.Output,
            _ => throw new JsonRpcException(
                JsonRpcErrorCode.InvalidParams,
                $"bus must be 'input' or 'output' (got '{raw}')"),
        };
    }

    /// <summary>
    /// Clamp to ±60 dB. Above +60 dB is mostly fizz; below -60 dB the
    /// linear value rounds to zero in float. The UI exposes [-60, +12].
    /// </summary>
    private static float ClampDb(float db)
    {
        if (float.IsNaN(db)) return 0f;
        if (db >  60f) return  60f;
        if (db < -60f) return -60f;
        return db;
    }

    private static Audio.MixEngine RequireEngine(EngineHost host)
        => host.Current
            ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");
}
