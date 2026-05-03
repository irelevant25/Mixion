using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.State;
using Xunit;

namespace VoicemeterAlt.Host.Tests;

/// <summary>
/// FE-059 / BE-070 acceptance: a preset captures the full DSP chain and
/// applying that preset onto a fresh state restores it exactly.
///
/// We don't go through the FE here — the preset DTO carries the BE state,
/// and the FE's <c>loadPreset</c> path simply calls <c>getState</c> after
/// applying. So if the BE round-trips DSP through Capture → Save → Load
/// → Apply → SnapshotState, the FE flow does too.
/// </summary>
public class PresetDspRoundTripTests
{
    private static readonly TestEndpoints Endpoints = new();

    [Fact]
    public void Capture_ThenApply_RestoresFullDspState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voicemeter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new PresetStore(dir, NullLogger.Instance);

            // Build a state with rich DSP on both buses.
            var input = new Channel(
                Id: "in0", Name: "Mic", GainDb: -3f, Muted: false, Soloed: true,
                Pan: 0f,
                Gate: new GateState(true, -45f, 1.5f, 60f, 130f, -36f),
                Compressor: new CompressorState(true, -16f, 4.5f, 8f, 90f, 6f, 2f),
                Eq: new EqState(true, new[]
                {
                    new EqBand("a", EqBandType.Peaking, 1000f, 4f, 1.2f),
                    new EqBand("b", EqBandType.HighShelf, 8000f, 2f, 0.7f),
                }));
            var output = new Channel(
                Id: "out0", Name: "Spk", GainDb: 0f, Muted: false, Soloed: false,
                Pan: 0.42f,
                Gate: null,
                Compressor: new CompressorState(true, -10f, 2f, 5f, 80f, 0f, 0f),
                Eq: new EqState(true, new[]
                {
                    new EqBand("c", EqBandType.LowShelf, 80f, 1.5f, 0.7f),
                }));

            var matrix = new RoutingMatrix(1, 1).With(0, 0, true);
            var state = new MixerState(
                ImmutableArray.Create(input),
                ImmutableArray.Create(output),
                matrix);

            // Capture + save to disk + reload (so we exercise JSON round-trip too).
            var preset = store.Capture("rich", state, Endpoints, null, null);
            store.Save("rich", preset);
            var loaded = store.Load("rich");

            // Wipe DSP to confirm Apply actually restores it (BE-070 acceptance).
            var wipedInput = new Channel(input.Id, input.Name, 0f, false, false);
            var wipedOutput = new Channel(output.Id, output.Name, 0f, false, false);
            var wiped = new MixerState(
                ImmutableArray.Create(wipedInput),
                ImmutableArray.Create(wipedOutput),
                new RoutingMatrix(1, 1));

            var result = store.Apply(loaded, wiped, Endpoints);
            var next = result.NextState;

            var ni = next.Inputs[0];
            Assert.Equal(input.GainDb, ni.GainDb);
            Assert.Equal(input.Muted, ni.Muted);
            Assert.Equal(input.Soloed, ni.Soloed);
            Assert.Equal(input.Pan, ni.Pan);
            Assert.NotNull(ni.Gate);
            Assert.Equal(input.Gate, ni.Gate);
            Assert.NotNull(ni.Compressor);
            Assert.Equal(input.Compressor, ni.Compressor);
            Assert.NotNull(ni.Eq);
            Assert.Equal(input.Eq!.Enabled, ni.Eq!.Enabled);
            Assert.Equal(input.Eq.Bands.Length, ni.Eq.Bands.Length);
            for (var i = 0; i < input.Eq.Bands.Length; i++)
            {
                Assert.Equal(input.Eq.Bands[i].Id,        ni.Eq.Bands[i].Id);
                Assert.Equal(input.Eq.Bands[i].Type,      ni.Eq.Bands[i].Type);
                Assert.Equal(input.Eq.Bands[i].Frequency, ni.Eq.Bands[i].Frequency);
                Assert.Equal(input.Eq.Bands[i].GainDb,    ni.Eq.Bands[i].GainDb);
                Assert.Equal(input.Eq.Bands[i].Q,         ni.Eq.Bands[i].Q);
            }

            var no = next.Outputs[0];
            Assert.Equal(output.Pan, no.Pan);
            Assert.Null(no.Gate);
            Assert.Equal(output.Compressor, no.Compressor);
            Assert.Equal(output.Eq!.Bands[0].Type, no.Eq!.Bands[0].Type);
            Assert.Equal(output.Eq!.Bands[0].Frequency, no.Eq!.Bands[0].Frequency);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void JsonRoundTrip_PreservesDspFieldsBitForBit()
    {
        // Serialiser used by PresetStore.Save — same source-gen context.
        var preset = new Preset(
            SchemaVersion: Preset.CurrentSchemaVersion,
            Name: "x",
            SavedAt: DateTimeOffset.UtcNow,
            Inputs: new[]
            {
                new PresetChannel("in0", "Mic", "USB",
                    GainDb: -3f, Muted: false, Soloed: false,
                    Pan: -0.4f,
                    Gate: new GateState(true, -50f, 1f, 40f, 120f, -30f),
                    Compressor: null,
                    Eq: new EqState(true, new[] { new EqBand("a", EqBandType.Notch, 250f, 0f, 12f) })),
            },
            Outputs: Array.Empty<PresetChannel>(),
            Matrix: Array.Empty<bool[]>(),
            InputSlots: Array.Empty<PresetSlot>(),
            OutputSlots: Array.Empty<PresetSlot>());

        var json = JsonSerializer.Serialize(preset, PresetJsonContext.Default.Preset);
        var decoded = JsonSerializer.Deserialize(json, PresetJsonContext.Default.Preset);

        Assert.NotNull(decoded);
        var ch = decoded!.Inputs[0];
        Assert.Equal(-0.4f, ch.Pan);
        Assert.Equal(-50f, ch.Gate!.ThresholdDb);
        Assert.Null(ch.Compressor);
        Assert.Equal(EqBandType.Notch, ch.Eq!.Bands[0].Type);
        Assert.Equal(12f, ch.Eq.Bands[0].Q);
    }

    /// <summary>Stub <see cref="IEndpointSource"/> that knows about the test ids above.</summary>
    private sealed class TestEndpoints : IEndpointSource
    {
        public IReadOnlyList<AudioEndpoint> Capture()
            => new[] { new AudioEndpoint("in0", "Mic", "USB", "capture", 48_000, 1, 16) };

        public IReadOnlyList<AudioEndpoint> Render()
            => new[] { new AudioEndpoint("out0", "Spk", "Realtek", "render", 48_000, 2, 16) };
    }
}
