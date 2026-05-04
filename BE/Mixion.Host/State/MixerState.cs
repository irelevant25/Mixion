using System.Collections.Immutable;

namespace Mixion.Host.State;

/// <summary>Filter shape applied by a single biquad in the EQ cascade.</summary>
public enum EqBandType
{
    Peaking,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
    Notch,
}

/// <summary>
/// One band of a parametric EQ. Bands carry a stable <see cref="Id"/> so the
/// FE can drag a single point without churning the rest of the chain — the
/// audio engine uses the id to keep biquad delay state across edits.
/// </summary>
public sealed record EqBand(
    string     Id,
    EqBandType Type,
    float      Frequency,
    float      GainDb,
    float      Q);

/// <summary>
/// Whole EQ stage state for one channel. <c>null</c> on <see cref="Channel.Eq"/>
/// = bypass; a state with <see cref="Enabled"/> = false also bypasses the
/// stage but keeps the band list (so the UI can keep editing while muted).
/// </summary>
public sealed record EqState(
    bool      Enabled,
    EqBand[]  Bands)
{
    public static EqState BypassedEmpty { get; } = new(Enabled: false, Bands: Array.Empty<EqBand>());
}

/// <summary>Noise gate stage state; <c>null</c> on the channel = bypass.</summary>
public sealed record GateState(
    bool  Enabled,
    float ThresholdDb,
    float AttackMs,
    float HoldMs,
    float ReleaseMs,
    float RangeDb);

/// <summary>
/// Feed-forward peak compressor stage state; <c>null</c> on the channel = bypass.
/// </summary>
public sealed record CompressorState(
    bool  Enabled,
    float ThresholdDb,
    float Ratio,
    float AttackMs,
    float ReleaseMs,
    float KneeDb,
    float MakeupDb);

/// <summary>
/// One channel of a bus (input or output). Records are reference-immutable;
/// the audio thread reads fields directly. Linear gain is computed in the
/// primary constructor so the mix path never calls <see cref="Math.Pow"/>.
///
/// IMPORTANT: prefer <see cref="WithGainDb(float)"/> over <c>ch with { GainDb = … }</c>
/// when changing gain. The compiler's <c>with</c>-rewriter copies the cached
/// <see cref="GainLinear"/> from the original instance and does not re-run
/// the property initializer, so a plain <c>with</c> would leave the linear
/// gain stale. Mute/solo/pan/gate/eq/compressor toggles can use plain
/// <c>with</c> safely — they don't affect the cached linear gain.
/// </summary>
public sealed record Channel(
    string Id,
    string Name,
    float  GainDb,
    bool   Muted,
    bool   Soloed,
    float  Pan = 0f,
    GateState?       Gate       = null,
    CompressorState? Compressor = null,
    EqState?         Eq         = null,
    bool             Available  = true)
{
    /// <summary>10^(GainDb/20). Cached on the immutable record.</summary>
    public float GainLinear { get; } = (float)Math.Pow(10.0, GainDb / 20.0);

    /// <summary>Return a copy with a new <see cref="GainDb"/> and a freshly-computed <see cref="GainLinear"/>.</summary>
    public Channel WithGainDb(float gainDb) =>
        new(Id, Name, gainDb, Muted, Soloed, Pan, Gate, Compressor, Eq, Available);
}

/// <summary>
/// NxM routing matrix. Stored as a flat <see cref="bool"/> array so we get
/// reference-based equality semantics that work cleanly with record swaps.
/// Indexing convention: <c>this[input, output]</c>.
/// </summary>
public sealed class RoutingMatrix : IEquatable<RoutingMatrix>
{
    private readonly bool[] _cells;

    public int Inputs  { get; }
    public int Outputs { get; }

    public RoutingMatrix(int inputs, int outputs)
    {
        if (inputs  < 0) throw new ArgumentOutOfRangeException(nameof(inputs));
        if (outputs < 0) throw new ArgumentOutOfRangeException(nameof(outputs));
        Inputs  = inputs;
        Outputs = outputs;
        _cells  = new bool[inputs * outputs];
    }

    private RoutingMatrix(int inputs, int outputs, bool[] cells)
    {
        Inputs  = inputs;
        Outputs = outputs;
        _cells  = cells;
    }

    public bool this[int input, int output]
    {
        get => _cells[(input * Outputs) + output];
    }

    public RoutingMatrix With(int input, int output, bool enabled)
    {
        var copy = (bool[])_cells.Clone();
        copy[(input * Outputs) + output] = enabled;
        return new RoutingMatrix(Inputs, Outputs, copy);
    }

    public bool Equals(RoutingMatrix? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Inputs != other.Inputs || Outputs != other.Outputs) return false;
        return _cells.AsSpan().SequenceEqual(other._cells);
    }

    public override bool Equals(object? obj) => Equals(obj as RoutingMatrix);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Inputs);
        hash.Add(Outputs);
        foreach (var c in _cells) hash.Add(c);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Single source of truth for the audio engine. The Kestrel control thread
/// publishes new instances via <see cref="Interlocked.Exchange{T}(ref T, T)"/>;
/// the mix thread reads via <see cref="Volatile.Read{T}(ref T)"/>. There is
/// no locking on the audio path.
/// </summary>
public sealed record MixerState(
    ImmutableArray<Channel> Inputs,
    ImmutableArray<Channel> Outputs,
    RoutingMatrix Matrix)
{
    public static MixerState Empty { get; } = new(
        ImmutableArray<Channel>.Empty,
        ImmutableArray<Channel>.Empty,
        new RoutingMatrix(0, 0));

    /// <summary>
    /// Build an initial state with all inputs routed straight to the matching
    /// output index (1:1 passthrough). Used for the M2 walking-skeleton run.
    /// </summary>
    public static MixerState Passthrough(ImmutableArray<Channel> inputs, ImmutableArray<Channel> outputs)
    {
        var matrix = new RoutingMatrix(inputs.Length, outputs.Length);
        var min    = Math.Min(inputs.Length, outputs.Length);
        for (var i = 0; i < min; i++)
            matrix = matrix.With(i, i, true);

        return new MixerState(inputs, outputs, matrix);
    }
}
