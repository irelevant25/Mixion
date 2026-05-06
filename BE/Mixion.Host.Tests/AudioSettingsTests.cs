using System.Collections.Immutable;
using System.Text.Json;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Covers the per-device exclusive-mode opt-in fields on
/// <see cref="AudioSettings"/> — both the helper methods the engine uses at
/// open time and the JSON round-trip the settings store relies on (old
/// settings files lack the new fields, new ones must survive a save/load
/// cycle).
/// </summary>
public class AudioSettingsTests
{
    [Fact]
    public void Default_HasNoExclusiveDevices()
    {
        var s = AudioSettings.Default;
        Assert.Empty(s.ExclusiveRenderDeviceIds);
        Assert.False(s.IsExclusiveRender("anything"));
    }

    [Fact]
    public void IsExclusiveRender_TracksOptInSet()
    {
        var s = AudioSettings.Default with
        {
            ExclusiveRenderDeviceIds = ImmutableHashSet.Create("device-A", "device-B"),
        };
        Assert.True(s.IsExclusiveRender("device-A"));
        Assert.True(s.IsExclusiveRender("device-B"));
        Assert.False(s.IsExclusiveRender("device-C"));
    }

    [Fact]
    public void GetExclusiveRenderLatencyMs_FallsBackToDefault()
    {
        var s = AudioSettings.Default with
        {
            ExclusiveRenderLatencyMsByDeviceId = ImmutableDictionary<string, int>.Empty
                .Add("device-A", 3),
        };
        Assert.Equal(3, s.GetExclusiveRenderLatencyMs("device-A"));
        Assert.Equal(AudioSettings.DefaultExclusiveRenderLatencyMs,
                     s.GetExclusiveRenderLatencyMs("device-B"));
    }

    [Fact]
    public void Validated_ClampsPerDeviceLatencyOverrides()
    {
        var s = AudioSettings.Default with
        {
            ExclusiveRenderLatencyMsByDeviceId = ImmutableDictionary<string, int>.Empty
                .Add("too-low",  0)                            // < MinBufferMs
                .Add("too-high", AudioSettings.MaxBufferMs + 1)
                .Add("ok",       4),
        };

        var v = s.Validated();
        Assert.Equal(AudioSettings.MinBufferMs, v.ExclusiveRenderLatencyMsByDeviceId["too-low"]);
        Assert.Equal(AudioSettings.MaxBufferMs, v.ExclusiveRenderLatencyMsByDeviceId["too-high"]);
        Assert.Equal(4,                          v.ExclusiveRenderLatencyMsByDeviceId["ok"]);
    }

    [Fact]
    public void Validated_NormalisesNullCollections()
    {
        // Mimics what System.Text.Json may emit for an old settings file
        // that predates the exclusive fields — null collections, not empty.
        var s = new AudioSettings(10, 10, true)
        {
            ExclusiveRenderDeviceIds            = null!,
            ExclusiveRenderLatencyMsByDeviceId  = null!,
        };

        var v = s.Validated();
        Assert.NotNull(v.ExclusiveRenderDeviceIds);
        Assert.NotNull(v.ExclusiveRenderLatencyMsByDeviceId);
        Assert.Empty(v.ExclusiveRenderDeviceIds);
        Assert.Empty(v.ExclusiveRenderLatencyMsByDeviceId);
    }

    [Fact]
    public void JsonRoundTrip_PreservesExclusiveSet()
    {
        var original = AudioSettings.Default with
        {
            ExclusiveRenderDeviceIds = ImmutableHashSet.Create("dev-1", "dev-2"),
            ExclusiveRenderLatencyMsByDeviceId = ImmutableDictionary<string, int>.Empty
                .Add("dev-1", 3)
                .Add("dev-2", 5),
        };

        var json    = JsonSerializer.Serialize(original);
        var decoded = JsonSerializer.Deserialize<AudioSettings>(json);

        Assert.NotNull(decoded);
        var v = decoded!.Validated();
        Assert.Contains("dev-1", v.ExclusiveRenderDeviceIds);
        Assert.Contains("dev-2", v.ExclusiveRenderDeviceIds);
        Assert.Equal(3, v.GetExclusiveRenderLatencyMs("dev-1"));
        Assert.Equal(5, v.GetExclusiveRenderLatencyMs("dev-2"));
    }

    [Fact]
    public void JsonRoundTrip_OldSettingsFileDeserialisesCleanly()
    {
        // Simulates a pre-exclusive-feature settings file: only the three
        // original fields. Validated() must produce an engine-safe instance
        // without throwing.
        const string oldJson = """
            { "CaptureBufferMs": 10, "RenderLatencyMs": 10, "PreferLowLatency": true }
            """;

        var decoded = JsonSerializer.Deserialize<AudioSettings>(oldJson);
        Assert.NotNull(decoded);
        var v = decoded!.Validated();
        Assert.Empty(v.ExclusiveRenderDeviceIds);
        Assert.Empty(v.ExclusiveRenderLatencyMsByDeviceId);
        Assert.False(v.IsExclusiveRender("anything"));
    }
}
