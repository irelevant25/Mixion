using Mixion.Host.State;

namespace Mixion.Host.Ipc;

/// <summary>Wire shape for one EQ band; mirrors <see cref="EqBand"/>.</summary>
public sealed record EqBandWireDto(string Id, string Type, float Frequency, float GainDb, float Q);

/// <summary>Wire shape for the whole EQ stage; mirrors <see cref="EqState"/>.</summary>
public sealed record EqStateWireDto(bool Enabled, EqBandWireDto[] Bands);

/// <summary>
/// Wire shape for one channel; mirrors <see cref="Channel"/> plus DSP state
/// (BE-070). DSP fields are nullable on the wire — <c>null</c> means
/// "stage bypassed".
/// </summary>
public sealed record ChannelDto(
    string  Id,
    string  Name,
    float   GainDb,
    bool    Muted,
    bool    Soloed,
    float   Pan,
    GateState?       Gate,
    CompressorState? Compressor,
    EqStateWireDto?  Eq,
    bool             Available);

/// <summary>
/// Wire shape for the whole mixer state. <see cref="Matrix"/> is indexed
/// <c>matrix[input][output]</c>, matching the runtime <see cref="RoutingMatrix"/>.
/// </summary>
public sealed record MixerStateDto(ChannelDto[] Inputs, ChannelDto[] Outputs, bool[][] Matrix);

public static class StateDto
{
    public static readonly MixerStateDto Empty = new(
        Array.Empty<ChannelDto>(),
        Array.Empty<ChannelDto>(),
        Array.Empty<bool[]>());

    public static ChannelDto ToDto(this Channel ch)
        => new(
            ch.Id,
            ch.Name,
            ch.GainDb,
            ch.Muted,
            ch.Soloed,
            ch.Pan,
            ch.Gate,
            ch.Compressor,
            ch.Eq is null ? null : ToWire(ch.Eq),
            ch.Available);

    public static MixerStateDto ToDto(this MixerState s)
    {
        var inputs = new ChannelDto[s.Inputs.Length];
        for (var i = 0; i < s.Inputs.Length; i++) inputs[i] = s.Inputs[i].ToDto();

        var outputs = new ChannelDto[s.Outputs.Length];
        for (var o = 0; o < s.Outputs.Length; o++) outputs[o] = s.Outputs[o].ToDto();

        var rows = s.Matrix.Inputs;
        var cols = s.Matrix.Outputs;
        var matrix = new bool[rows][];
        for (var i = 0; i < rows; i++)
        {
            var row = new bool[cols];
            for (var o = 0; o < cols; o++) row[o] = s.Matrix[i, o];
            matrix[i] = row;
        }

        return new MixerStateDto(inputs, outputs, matrix);
    }

    private static EqStateWireDto ToWire(EqState eq)
    {
        var bands = new EqBandWireDto[eq.Bands.Length];
        for (var i = 0; i < eq.Bands.Length; i++)
        {
            var b = eq.Bands[i];
            bands[i] = new EqBandWireDto(b.Id, BandTypeToWire(b.Type), b.Frequency, b.GainDb, b.Q);
        }
        return new EqStateWireDto(eq.Enabled, bands);
    }

    /// <summary>
    /// Render the enum as a stable camel-case literal so the FE doesn't
    /// need to know about C# casing rules. The reverse mapping lives in
    /// <c>DspHandlers.ParseBandType</c>.
    /// </summary>
    private static string BandTypeToWire(EqBandType t) => t switch
    {
        EqBandType.Peaking   => "peaking",
        EqBandType.LowShelf  => "lowShelf",
        EqBandType.HighShelf => "highShelf",
        EqBandType.LowPass   => "lowPass",
        EqBandType.HighPass  => "highPass",
        EqBandType.Notch     => "notch",
        _                    => "peaking",
    };
}
