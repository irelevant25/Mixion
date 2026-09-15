using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Mixion.Host.Audio;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Slot bindings survive a preset save / load even when their device or app
/// isn't there at load time — the setup mustn't need fixing by hand after a
/// restart.
/// </summary>
public class PresetSlotPersistenceTests : IDisposable
{
    private static readonly TestEndpoints Endpoints = new();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mixion-test-" + Guid.NewGuid().ToString("N"));
    private readonly PresetStore _store;

    public PresetSlotPersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new PresetStore(_dir, NullLogger.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly Channel Mic      = new("mic", "Mic", GainDb: 0f, Muted: false, Soloed: false);
    private static readonly Channel Speakers = new("out0", "Speakers", GainDb: 0f, Muted: false, Soloed: false);

    [Fact]
    public void AppSlot_SurvivesALoadWhileTheAppIsClosed()
    {
        var chrome = new Channel("process:chrome", "chrome (app)", GainDb: -6f, Muted: false, Soloed: false, Pan: 0.25f);
        var saved  = new MixerState(
            ImmutableArray.Create(Mic, chrome),
            ImmutableArray.Create(Speakers),
            new RoutingMatrix(2, 1).With(1, 0, true));

        var slots = PresetStore.CaptureSlots(new[] { "process:chrome", null }, saved.Inputs, Endpoints.Capture());
        _store.Save("p", _store.Capture("p", saved, Endpoints, slots, Array.Empty<PresetSlot>()));
        var preset = _store.Load("p");

        Assert.Equal("process:chrome", preset.InputSlots[0].DeviceId);
        Assert.Equal("chrome (app)", preset.InputSlots[0].FriendlyName);
        Assert.Null(preset.InputSlots[1].DeviceId);

        // After a restart with Chrome closed only the mic and speakers exist.
        var live   = new MixerState(ImmutableArray.Create(Mic), ImmutableArray.Create(Speakers), new RoutingMatrix(1, 1));
        var result = _store.Apply(preset, live, Endpoints);

        var placeholder = Assert.Single(result.NextState.Inputs, c => c.Id == "process:chrome");
        Assert.False(placeholder.Available);
        Assert.Equal("chrome (app)", placeholder.Name);
        Assert.Equal(-6f, placeholder.GainDb);
        Assert.Equal(0.25f, placeholder.Pan);
        Assert.Equal("process:chrome", result.InputSlots[0].DeviceId);
        Assert.Null(result.InputSlots[1].DeviceId);
        Assert.True(result.NextState.Matrix[1, 0]);
        Assert.Empty(result.MissingDevices);
    }

    [Fact]
    public void UnresolvableDeviceSlot_KeepsANamedPlaceholder()
    {
        var headset = new Channel("{gone}", "USB Headset", GainDb: -3f, Muted: true, Soloed: false);
        var saved   = new MixerState(ImmutableArray.Create(Mic, headset), ImmutableArray.Create(Speakers), new RoutingMatrix(2, 1));
        var slots   = PresetStore.CaptureSlots(new[] { "{gone}" }, saved.Inputs, Endpoints.Capture());
        var preset  = _store.Capture("p", saved, Endpoints, slots, Array.Empty<PresetSlot>());

        var live   = new MixerState(ImmutableArray.Create(Mic), ImmutableArray.Create(Speakers), new RoutingMatrix(1, 1));
        var result = _store.Apply(preset, live, Endpoints);

        var placeholder = Assert.Single(result.NextState.Inputs, c => c.Id == "{gone}");
        Assert.False(placeholder.Available);
        Assert.Equal("USB Headset", placeholder.Name);
        Assert.True(placeholder.Muted);
        Assert.Equal("{gone}", result.InputSlots[0].DeviceId);
    }

    [Fact]
    public void Apply_KeepsAnUnavailableChannelUnavailable()
    {
        var unplugged = Mic with { Available = false };
        var saved     = new MixerState(ImmutableArray.Create(Mic.WithGainDb(-3f)), ImmutableArray.Create(Speakers), new RoutingMatrix(1, 1));
        var preset    = _store.Capture("p", saved, Endpoints, null, null);

        var live   = new MixerState(ImmutableArray.Create(unplugged), ImmutableArray.Create(Speakers), new RoutingMatrix(1, 1));
        var result = _store.Apply(preset, live, Endpoints);

        Assert.False(result.NextState.Inputs[0].Available);
        Assert.Equal(-3f, result.NextState.Inputs[0].GainDb);
    }

    [Fact]
    public void ClosedApps_AreNotReportedAsMissingDevices()
    {
        var discord = new Channel("process:discord", "discord (app)", 0f, false, false);
        var saved   = new MixerState(ImmutableArray.Create(Mic, discord), ImmutableArray.Create(Speakers), new RoutingMatrix(2, 1));
        var preset  = _store.Capture("p", saved, Endpoints, null, null);

        var live   = new MixerState(ImmutableArray.Create(Mic), ImmutableArray.Create(Speakers), new RoutingMatrix(1, 1));
        var result = _store.Apply(preset, live, Endpoints);

        Assert.Empty(result.MissingDevices);
        Assert.Single(result.NextState.Inputs); // not bound to a slot, so no placeholder
    }

    [Fact]
    public void LegacySlotWithoutAnId_StillResolvesByName()
    {
        var preset = new Preset(
            SchemaVersion: Preset.CurrentSchemaVersion,
            Name:          "legacy",
            SavedAt:       DateTimeOffset.UtcNow,
            Inputs:        new[] { new PresetChannel("old-id", "Mic", "USB", GainDb: -2f, Muted: false, Soloed: false) },
            Outputs:       Array.Empty<PresetChannel>(),
            Matrix:        new[] { Array.Empty<bool>() },
            InputSlots:    new[] { new PresetSlot("Mic", "USB") },
            OutputSlots:   Array.Empty<PresetSlot>());

        var live   = new MixerState(ImmutableArray.Create(Mic), ImmutableArray.Create(Speakers), new RoutingMatrix(1, 1));
        var result = _store.Apply(preset, live, Endpoints);

        Assert.Equal("mic", result.InputSlots[0].DeviceId);
        Assert.Single(result.NextState.Inputs);
        Assert.Equal(-2f, result.NextState.Inputs[0].GainDb);
    }

    private sealed class TestEndpoints : IEndpointSource
    {
        public IReadOnlyList<AudioEndpoint> Capture()
            => new[] { new AudioEndpoint("mic", "Mic", "USB", "capture", 48_000, 1, 16) };

        public IReadOnlyList<AudioEndpoint> Render()
            => new[] { new AudioEndpoint("out0", "Speakers", "Realtek", "render", 48_000, 2, 16) };
    }
}
