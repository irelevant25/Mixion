using System.Text.Json.Serialization;

namespace VoicemeterAlt.Host.State;

/// <summary>
/// One channel as stored in a preset. Identified by both <see cref="DeviceId"/>
/// (WASAPI endpoint id, may change between machines / driver reinstalls) and
/// <see cref="FriendlyName"/> + <see cref="InterfaceName"/> (BE-062: stable
/// human-readable identity used for re-resolution at load time).
///
/// DSP state (<see cref="Pan"/>, <see cref="Gate"/>, <see cref="Compressor"/>,
/// <see cref="Eq"/>) round-trips alongside gain/mute/solo so loading a preset
/// restores the user's full chain (BE-070).
/// </summary>
public sealed record PresetChannel(
    string  DeviceId,
    string  FriendlyName,
    string  InterfaceName,
    float   GainDb,
    bool    Muted,
    bool    Soloed,
    float   Pan        = 0f,
    GateState?       Gate       = null,
    CompressorState? Compressor = null,
    EqState?         Eq         = null);

/// <summary>
/// One UI slot within a preset. Slots are an FE concept (Voicemeeter-style
/// A/B/C/… positions that can hold devices), but they're persisted with the
/// preset so loading restores the user's layout — without them the load
/// would only patch BE state and the user's row of strips would still be
/// empty.
///
/// A slot with both names null/empty represents an intentionally unassigned
/// placeholder (the user wanted the letter held). Assigned slots carry the
/// device's stable identity (friendly + interface name) and are re-resolved
/// against the live endpoints at load time, the same way <see cref="PresetChannel"/>
/// is.
/// </summary>
public sealed record PresetSlot(string? FriendlyName, string? InterfaceName);

/// <summary>
/// Versioned snapshot of the mixer. Whole shape is round-tripped via
/// <see cref="System.Text.Json"/> source-gen (<see cref="PresetJsonContext"/>).
/// Bumping <see cref="SchemaVersion"/> indicates a breaking change; older
/// presets must be migrated or rejected on load.
/// </summary>
public sealed record Preset(
    int                    SchemaVersion,
    string                 Name,
    DateTimeOffset         SavedAt,
    PresetChannel[]        Inputs,
    PresetChannel[]        Outputs,
    bool[][]               Matrix,
    PresetSlot[]           InputSlots,
    PresetSlot[]           OutputSlots)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// AOT-friendly JSON serializer context for <see cref="Preset"/>. Using
/// source-gen keeps trim/AOT publishing happy and avoids runtime reflection
/// inside the host process.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Preset))]
[JsonSerializable(typeof(PresetChannel))]
[JsonSerializable(typeof(PresetChannel[]))]
[JsonSerializable(typeof(PresetSlot))]
[JsonSerializable(typeof(PresetSlot[]))]
[JsonSerializable(typeof(GateState))]
[JsonSerializable(typeof(CompressorState))]
[JsonSerializable(typeof(EqState))]
[JsonSerializable(typeof(EqBand))]
[JsonSerializable(typeof(EqBand[]))]
[JsonSerializable(typeof(bool[][]))]
public partial class PresetJsonContext : JsonSerializerContext
{
}
