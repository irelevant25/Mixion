using VoicemeterAlt.Host.Audio;
using Xunit;

namespace VoicemeterAlt.Host.Tests;

/// <summary>
/// The driver probe is intentionally narrow: only the **basic** VB-CABLE
/// (single render + single capture endpoint named <c>CABLE Input</c> /
/// <c>CABLE Output</c>) is treated as installed. Higher-tier products
/// (VB-CABLE A+B, VB-CABLE C+D, Voicemeeter VAIOs, HiFi-Cable, etc.) must
/// NOT trip the probe — the app's "one virtual cable" mental model breaks
/// when the user is given multiple cables to choose between.
/// </summary>
public class DriverProbeTests
{
    [Fact]
    public void Probe_FindsBasicVbCableRenderEndpoint()
    {
        var source = new FakeSource(
            render: new[] { Ep("CABLE Input (VB-Audio Virtual Cable)", "render") },
            capture: Array.Empty<AudioEndpoint>());

        var result = DriverProbe.Probe(source);

        Assert.True(result.Found);
        Assert.NotNull(result.MatchedDevice);
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", result.MatchedDevice!.FriendlyName);
    }

    [Fact]
    public void Probe_FindsBasicVbCableCaptureEndpoint()
    {
        var source = new FakeSource(
            render:  Array.Empty<AudioEndpoint>(),
            capture: new[] { Ep("CABLE Output (VB-Audio Virtual Cable)", "capture") });

        var result = DriverProbe.Probe(source);

        Assert.True(result.Found);
    }

    [Fact]
    public void Probe_RejectsCableABVariants()
    {
        // VB-CABLE A+B installs surface "CABLE-A Input" / "CABLE-B Output" —
        // those are NOT the basic product and must not satisfy the probe.
        var source = new FakeSource(
            render: new[]
            {
                Ep("CABLE-A Input (VB-Audio Cable A)",  "render"),
                Ep("CABLE-B Input (VB-Audio Cable B)",  "render"),
            },
            capture: new[]
            {
                Ep("CABLE-A Output (VB-Audio Cable A)", "capture"),
                Ep("CABLE-B Output (VB-Audio Cable B)", "capture"),
            });

        var result = DriverProbe.Probe(source);

        Assert.False(result.Found);
        Assert.Null(result.MatchedDevice);
    }

    [Fact]
    public void Probe_RejectsCableCDVariants()
    {
        var source = new FakeSource(
            render: new[]
            {
                Ep("CABLE-C Input (VB-Audio Cable C)", "render"),
                Ep("CABLE-D Input (VB-Audio Cable D)", "render"),
            },
            capture: Array.Empty<AudioEndpoint>());

        Assert.False(DriverProbe.Probe(source).Found);
    }

    [Fact]
    public void Probe_RejectsVoicemeeterVaioEndpoints()
    {
        // Voicemeeter Banana / Potato install several "VAIO" inputs/outputs.
        // They're VB-Audio products but NOT the basic VB-CABLE.
        var source = new FakeSource(
            render: new[]
            {
                Ep("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",     "render"),
                Ep("VoiceMeeter Aux Input (VB-Audio VoiceMeeter AUX VAIO)", "render"),
            },
            capture: new[]
            {
                Ep("VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)",    "capture"),
            });

        Assert.False(DriverProbe.Probe(source).Found);
    }

    [Fact]
    public void Probe_FindsBasicEvenWhenHigherTierVariantsCoexist()
    {
        // A user might have both basic VB-CABLE and VB-CABLE A+B installed —
        // the basic one still satisfies us.
        var source = new FakeSource(
            render: new[]
            {
                Ep("CABLE-A Input (VB-Audio Cable A)",        "render"),
                Ep("CABLE Input (VB-Audio Virtual Cable)",    "render"),
            },
            capture: Array.Empty<AudioEndpoint>());

        var result = DriverProbe.Probe(source);

        Assert.True(result.Found);
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", result.MatchedDevice!.FriendlyName);
    }

    [Fact]
    public void Probe_ReturnsNotFoundOnEmptyEndpoints()
    {
        var source = new FakeSource(Array.Empty<AudioEndpoint>(), Array.Empty<AudioEndpoint>());
        Assert.False(DriverProbe.Probe(source).Found);
    }

    [Fact]
    public void Probe_RejectsRandomNonVbDevices()
    {
        var source = new FakeSource(
            render:  new[] { Ep("Speakers (Realtek HD Audio)",          "render") },
            capture: new[] { Ep("Microphone (USB Audio Device)",        "capture") });

        Assert.False(DriverProbe.Probe(source).Found);
    }

    [Fact]
    public void Probe_MatchesIsCaseInsensitive()
    {
        var source = new FakeSource(
            render: new[] { Ep("cable input (vb-audio virtual cable)", "render") },
            capture: Array.Empty<AudioEndpoint>());

        Assert.True(DriverProbe.Probe(source).Found);
    }

    private static AudioEndpoint Ep(string friendlyName, string direction)
        => new("id-" + friendlyName, friendlyName, friendlyName, direction, 48_000, 2, 16);

    private sealed class FakeSource : IEndpointSource
    {
        private readonly IReadOnlyList<AudioEndpoint> _capture;
        private readonly IReadOnlyList<AudioEndpoint> _render;

        public FakeSource(IReadOnlyList<AudioEndpoint> render, IReadOnlyList<AudioEndpoint> capture)
        {
            _render  = render;
            _capture = capture;
        }

        public IReadOnlyList<AudioEndpoint> Capture() => _capture;
        public IReadOnlyList<AudioEndpoint> Render()  => _render;
    }
}
