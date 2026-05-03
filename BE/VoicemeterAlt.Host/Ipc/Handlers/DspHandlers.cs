using VoicemeterAlt.Host.State;

namespace VoicemeterAlt.Host.Ipc.Handlers;

/// <summary>
/// <c>setPan</c>, <c>setGate</c>, <c>setCompressor</c>, <c>addEqBand</c>,
/// <c>setEqBand</c>, <c>removeEqBand</c> — per-channel DSP control
/// (BE-077). State mutation follows the same pattern as
/// <see cref="ChannelHandlers"/>: snapshot the current
/// <see cref="MixerState"/>, build a new immutable record with the patched
/// <see cref="Channel"/>, publish it. The mix loop picks it up on its next
/// tick and the relevant DSP processors recompute coefficients via their
/// <c>Apply</c> methods.
///
/// <c>addEqBand</c> assigns a fresh server-side id; the response carries it
/// back so subsequent <c>setEqBand</c>/<c>removeEqBand</c> can target the
/// same band stably across edits and preset round-trips.
/// </summary>
public static class DspHandlers
{
    public sealed record SetPanParams(int Channel, float Pan, string? Bus = null);
    public sealed record SetGateParams(int Channel, GateState? Gate, string? Bus = null);
    public sealed record SetCompressorParams(
        int Channel,
        CompressorState? Compressor,
        string? Bus = null);

    /// <summary>
    /// Wire shape for one EQ band coming over the RPC. <see cref="Type"/>
    /// is a string for ease of use in the FE; we map it to the enum on the
    /// way in. <see cref="Id"/> is optional on requests — <c>addEqBand</c>
    /// always assigns one server-side, <c>setEqBand</c> uses the band id
    /// from its own <see cref="SetEqBandParams.BandId"/>.
    /// </summary>
    public sealed record EqBandDto(
        string Type,
        float  Frequency,
        float  GainDb,
        float  Q,
        string? Id = null);

    public sealed record AddEqBandParams(int Channel, EqBandDto Band, string? Bus = null);
    public sealed record SetEqBandParams(int Channel, string BandId, EqBandDto Band, string? Bus = null);
    public sealed record RemoveEqBandParams(int Channel, string BandId, string? Bus = null);
    public sealed record SetEqEnabledParams(int Channel, bool Enabled, string? Bus = null);

    public static void Register(JsonRpcDispatcher dispatcher, EngineHost host)
    {
        dispatcher.Register("setPan", (paramsEl, _, _) =>
        {
            var p   = JsonRpcDispatcher.RequireParams<SetPanParams>(paramsEl);
            var pan = ClampPan(p.Pan);
            var bus = ChannelHandlers.ParseBus(p.Bus);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Pan = pan });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("setGate", (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<SetGateParams>(paramsEl);
            var bus = ChannelHandlers.ParseBus(p.Bus);
            var gate = NormaliseGate(p.Gate);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Gate = gate });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("setCompressor", (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<SetCompressorParams>(paramsEl);
            var bus = ChannelHandlers.ParseBus(p.Bus);
            var comp = NormaliseCompressor(p.Compressor);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Compressor = comp });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("addEqBand", (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<AddEqBandParams>(paramsEl);
            var bus = ChannelHandlers.ParseBus(p.Bus);
            var bandId = NewBandId();
            var band = ToEqBand(p.Band, bandId);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Eq = AppendBand(ch.Eq, band) });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true, bandId });
        });

        dispatcher.Register("setEqBand", (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<SetEqBandParams>(paramsEl);
            if (string.IsNullOrEmpty(p.BandId))
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "bandId required.");
            var bus = ChannelHandlers.ParseBus(p.Bus);
            var band = ToEqBand(p.Band, p.BandId);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Eq = ReplaceBand(ch.Eq, p.BandId, band) });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });

        dispatcher.Register("removeEqBand", (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<RemoveEqBandParams>(paramsEl);
            if (string.IsNullOrEmpty(p.BandId))
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams, "bandId required.");
            var bus = ChannelHandlers.ParseBus(p.Bus);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Eq = RemoveBand(ch.Eq, p.BandId) });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });

        // Toggle the EQ stage's bypass without disturbing the band list. The
        // FE used to fake this by re-broadcasting every band on toggle, which
        // didn't actually flip Enabled because ReplaceBand preserves it.
        dispatcher.Register("setEqEnabled", (paramsEl, _, _) =>
        {
            var p   = JsonRpcDispatcher.RequireParams<SetEqEnabledParams>(paramsEl);
            var bus = ChannelHandlers.ParseBus(p.Bus);

            var engine = RequireEngine(host);
            var next = ChannelHandlers.ApplyChannelChange(
                engine.SnapshotState(), bus, p.Channel,
                ch => ch with { Eq = SetEnabled(ch.Eq, p.Enabled) });
            engine.PublishState(next);

            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /// <summary>Pan is in <c>[-1, +1]</c>; clamp to keep bad input out of the audio path.</summary>
    private static float ClampPan(float p)
    {
        if (float.IsNaN(p)) return 0f;
        if (p >  1f) return  1f;
        if (p < -1f) return -1f;
        return p;
    }

    /// <summary>
    /// Coerce an incoming <see cref="GateState"/> into safe ranges. NaN /
    /// nonsense values would otherwise produce poisoned coefficients that
    /// the audio thread can't recover from without another state swap.
    /// </summary>
    private static GateState? NormaliseGate(GateState? g)
    {
        if (g is null) return null;
        return new GateState(
            Enabled:     g.Enabled,
            ThresholdDb: ClampFloat(g.ThresholdDb, -120f, 0f, defaultValue: -50f),
            AttackMs:    ClampFloat(g.AttackMs,    0.1f, 1000f, defaultValue: 1f),
            HoldMs:      ClampFloat(g.HoldMs,      0f,   2000f, defaultValue: 50f),
            ReleaseMs:   ClampFloat(g.ReleaseMs,   1f,   5000f, defaultValue: 120f),
            RangeDb:     ClampFloat(g.RangeDb,    -120f, 0f,    defaultValue: -40f));
    }

    private static CompressorState? NormaliseCompressor(CompressorState? c)
    {
        if (c is null) return null;
        return new CompressorState(
            Enabled:     c.Enabled,
            ThresholdDb: ClampFloat(c.ThresholdDb, -60f, 0f,    defaultValue: -18f),
            Ratio:       ClampFloat(c.Ratio,       1f,  20f,   defaultValue: 4f),
            AttackMs:    ClampFloat(c.AttackMs,    0.1f,500f,  defaultValue: 10f),
            ReleaseMs:   ClampFloat(c.ReleaseMs,   1f,  5000f, defaultValue: 100f),
            KneeDb:      ClampFloat(c.KneeDb,      0f,  24f,   defaultValue: 6f),
            MakeupDb:    ClampFloat(c.MakeupDb,    -24f,24f,   defaultValue: 0f));
    }

    private static float ClampFloat(float v, float min, float max, float defaultValue)
    {
        if (float.IsNaN(v)) return defaultValue;
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    /// <summary>Map a wire-level type string ("peaking" / "lowShelf" / …) onto the enum.</summary>
    private static EqBandType ParseBandType(string? raw)
    {
        var s = (raw ?? "peaking").Trim().ToLowerInvariant();
        return s switch
        {
            "peaking"   => EqBandType.Peaking,
            "lowshelf"  => EqBandType.LowShelf,
            "low_shelf" => EqBandType.LowShelf,
            "highshelf" => EqBandType.HighShelf,
            "high_shelf"=> EqBandType.HighShelf,
            "lowpass"   => EqBandType.LowPass,
            "low_pass"  => EqBandType.LowPass,
            "highpass"  => EqBandType.HighPass,
            "high_pass" => EqBandType.HighPass,
            "notch"     => EqBandType.Notch,
            _ => throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                $"Unknown EQ band type '{raw}'."),
        };
    }

    private static EqBand ToEqBand(EqBandDto dto, string id)
    {
        return new EqBand(
            Id:        id,
            Type:      ParseBandType(dto.Type),
            Frequency: ClampFloat(dto.Frequency, 10f,  24_000f, defaultValue: 1000f),
            GainDb:    ClampFloat(dto.GainDb,   -24f,  24f,     defaultValue: 0f),
            Q:         ClampFloat(dto.Q,         0.1f, 18f,     defaultValue: 1f));
    }

    private static EqState AppendBand(EqState? eq, EqBand band)
    {
        var existing = eq?.Bands ?? Array.Empty<EqBand>();
        var next = new EqBand[existing.Length + 1];
        Array.Copy(existing, next, existing.Length);
        next[^1] = band;
        return new EqState(eq?.Enabled ?? true, next);
    }

    private static EqState? ReplaceBand(EqState? eq, string bandId, EqBand band)
    {
        if (eq is null) return new EqState(true, new[] { band });
        var bands = eq.Bands;
        var idx = -1;
        for (var i = 0; i < bands.Length; i++)
        {
            if (string.Equals(bands[i].Id, bandId, StringComparison.Ordinal)) { idx = i; break; }
        }
        if (idx < 0)
        {
            // Treat update-to-missing as append; the FE may have lost
            // track of the id mid-edit (rare race) but the user clearly
            // wants the new band reflected.
            return AppendBand(eq, band);
        }
        var next = new EqBand[bands.Length];
        Array.Copy(bands, next, bands.Length);
        next[idx] = band;
        return new EqState(eq.Enabled, next);
    }

    private static EqState SetEnabled(EqState? eq, bool enabled)
    {
        if (eq is null) return new EqState(enabled, Array.Empty<EqBand>());
        if (eq.Enabled == enabled) return eq;
        return eq with { Enabled = enabled };
    }

    private static EqState? RemoveBand(EqState? eq, string bandId)
    {
        if (eq is null) return null;
        var bands = eq.Bands;
        var idx = -1;
        for (var i = 0; i < bands.Length; i++)
        {
            if (string.Equals(bands[i].Id, bandId, StringComparison.Ordinal)) { idx = i; break; }
        }
        if (idx < 0) return eq;

        if (bands.Length == 1) return new EqState(eq.Enabled, Array.Empty<EqBand>());

        var next = new EqBand[bands.Length - 1];
        Array.Copy(bands, 0, next, 0, idx);
        Array.Copy(bands, idx + 1, next, idx, bands.Length - idx - 1);
        return new EqState(eq.Enabled, next);
    }

    /// <summary>
    /// Compact, URL-safe band id. Random base-36-ish so it stays terse on
    /// the wire and in JSON presets — guids would be 36 characters per
    /// band, multiplied by the band count and the number of channels.
    /// </summary>
    private static string NewBandId()
        => "b" + Convert.ToString(Random.Shared.NextInt64(0x10_000_000_000_000), 16);

    private static Audio.MixEngine RequireEngine(EngineHost host)
        => host.Current
            ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");
}
