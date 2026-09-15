using System.Collections.Immutable;
using Mixion.Host.Audio;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Rules the device watcher follows to keep channels bound: apps by name across
/// restarts, devices by endpoint id across plug/unplug, and new ones attached.
/// </summary>
public class TopologyPlannerTests
{
    private const int Rate = 48_000;

    private static readonly SlotBinding Healthy = new(HasSource: true, Healthy: true);
    private static readonly SlotBinding Broken  = new(HasSource: true, Healthy: false);
    private static readonly SlotBinding Unbound = SlotBinding.Empty;

    private static MixerState State(string[] inputIds, string[]? outputIds = null)
    {
        outputIds ??= new[] { "out0" };
        return new MixerState(
            inputIds.Select(id => new Channel(id, id, 0f, false, false)).ToImmutableArray(),
            outputIds.Select(id => new Channel(id, id, 0f, false, false)).ToImmutableArray(),
            new RoutingMatrix(inputIds.Length, outputIds.Length));
    }

    private static ProcessSnapshot Processes(params (int Pid, int Parent, string Name)[] entries)
        => ProcessSnapshot.FromEntries(entries.Select(e => new ProcessEntry(e.Pid, e.Parent, e.Name)));

    private static TopologySnapshot Os(
        ProcessSnapshot processes,
        AudioProcess[]? audio = null,
        EndpointInfo[]? capture = null,
        EndpointInfo[]? render = null)
        => new(capture, render, audio ?? Array.Empty<AudioProcess>(), processes);

    private static TopologyPlan Plan(
        MixerState state,
        SlotBinding[] inputs,
        TopologySnapshot os,
        SlotBinding[]? outputs = null,
        Func<string, bool>? suppressed = null,
        Func<SourceTarget, bool>? blocked = null)
        => TopologyPlanner.Plan(
            state,
            inputs,
            outputs ?? state.Outputs.Select(_ => Healthy).ToArray(),
            os,
            Rate,
            suppressed ?? (_ => false),
            blocked ?? (_ => false));

    // ------------------------------------------------------------------ apps

    [Fact]
    public void RestartedApp_IsReboundToTheNewInstancesRootProcess()
    {
        // Chrome was closed (the bound process exited) and opened again: the
        // audio session belongs to a helper whose root is the browser process.
        var os = Os(
            Processes((1, 0, "explorer"), (100, 1, "chrome"), (200, 100, "chrome")),
            audio: new[] { new AudioProcess(SessionProcessId: 200, RootProcessId: 100, ProcessName: "chrome") });

        var plan = Plan(State(new[] { "process:chrome" }), new[] { Broken }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.App("chrome", 100)) }, plan.InputRebinds);
        Assert.Empty(plan.NewInputs);
    }

    [Fact]
    public void ReopenedApp_ReattachesBeforeItPlaysAnything()
    {
        var os = Os(Processes((1, 0, "explorer"), (300, 1, "chrome"), (301, 300, "chrome")));

        var plan = Plan(State(new[] { "process:chrome" }), new[] { Unbound }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.App("chrome", 300)) }, plan.InputRebinds);
    }

    [Fact]
    public void ClosedApp_IsDetached()
    {
        var plan = Plan(State(new[] { "process:chrome" }), new[] { Broken }, Os(Processes((1, 0, "explorer"))));

        Assert.Equal(new[] { new SlotRebind(0, null) }, plan.InputRebinds);
    }

    [Fact]
    public void AppThatStartedMixion_KeepsItsBindingBetweenAudioProcesses()
    {
        // Mixion was opened from Chrome, so the browser (100) is this process's parent.
        // Chrome recycled its audio service (the bound 300 exited) and hasn't opened a
        // new session yet; the browser itself can't be captured, as it contains Mixion.
        var self = Environment.ProcessId;
        var os   = Os(Processes((1, 0, "explorer"), (100, 1, "chrome"), (self, 100, "testhost")));

        var plan = Plan(State(new[] { "process:chrome" }), new[] { new SlotBinding(true, false, TargetProcessId: 300) }, os);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void AppThatStartedMixion_IsReboundToItsNewAudioProcess()
    {
        var self = Environment.ProcessId;
        var os   = Os(
            Processes((1, 0, "explorer"), (100, 1, "chrome"), (self, 100, "testhost"), (400, 100, "chrome")),
            audio: new[] { new AudioProcess(SessionProcessId: 400, RootProcessId: 400, ProcessName: "chrome") });

        var plan = Plan(State(new[] { "process:chrome" }), new[] { new SlotBinding(true, false, TargetProcessId: 300) }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.App("chrome", 400)) }, plan.InputRebinds);
    }

    [Fact]
    public void AppThatIsStillClosed_NeedsNothing()
    {
        var plan = Plan(State(new[] { "process:chrome" }), new[] { Unbound }, Os(Processes((1, 0, "explorer"))));

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void HealthyApp_IsLeftAlone()
    {
        var os = Os(
            Processes((100, 1, "chrome")),
            audio: new[] { new AudioProcess(100, 100, "chrome") });

        var plan = Plan(State(new[] { "process:chrome" }), new[] { Healthy }, os);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void AppBoundToASilentInstance_FollowsTheInstancePlayingAudio()
    {
        var os = Os(
            Processes((100, 1, "vlc"), (500, 1, "vlc")),
            audio: new[] { new AudioProcess(500, 500, "vlc") });

        var plan = Plan(State(new[] { "process:vlc" }), new[] { new SlotBinding(true, true, TargetProcessId: 100) }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.App("vlc", 500)) }, plan.InputRebinds);
    }

    [Fact]
    public void AppBoundToAnInstancePlayingAudio_StaysPutWhenAnotherPlaysToo()
    {
        var os = Os(
            Processes((100, 1, "vlc"), (500, 1, "vlc")),
            audio: new[] { new AudioProcess(100, 100, "vlc"), new AudioProcess(500, 500, "vlc") });

        var plan = Plan(State(new[] { "process:vlc" }), new[] { new SlotBinding(true, true, TargetProcessId: 500) }, os);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void NewAppWithSeveralInstances_IsAttachedOnce()
    {
        var os = Os(
            Processes((100, 1, "vlc"), (500, 1, "vlc")),
            audio: new[] { new AudioProcess(100, 100, "vlc"), new AudioProcess(500, 500, "vlc") });

        var plan = Plan(State(new[] { "mic" }), new[] { Healthy }, os);

        Assert.Equal(
            new[] { new NewChannel("process:vlc", "vlc (app)", new SourceTarget.App("vlc", 100)) },
            plan.NewInputs);
    }

    [Fact]
    public void AppNames_MatchCaseInsensitively()
    {
        var os = Os(
            Processes((500, 1, "spotify")),
            audio: new[] { new AudioProcess(500, 500, "spotify") });

        var plan = Plan(State(new[] { "process:Spotify" }), new[] { Unbound }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.App("Spotify", 500)) }, plan.InputRebinds);
        Assert.Empty(plan.NewInputs);
    }

    [Fact]
    public void NewAppWithAudio_IsAttached()
    {
        var os = Os(
            Processes((500, 1, "spotify")),
            audio: new[] { new AudioProcess(500, 500, "spotify") });

        var plan = Plan(State(new[] { "mic" }), new[] { Healthy }, os);

        Assert.Equal(
            new[] { new NewChannel("process:spotify", "spotify (app)", new SourceTarget.App("spotify", 500)) },
            plan.NewInputs);
    }

    [Fact]
    public void AppDetachedByTheUser_IsNotAttachedAgain()
    {
        var os = Os(
            Processes((500, 1, "spotify")),
            audio: new[] { new AudioProcess(500, 500, "spotify") });

        var plan = Plan(State(new[] { "mic" }), new[] { Healthy }, os, suppressed: name => name == "spotify");

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void AppThatFailedToOpen_IsSkippedUntilItsCooldownEnds()
    {
        var os = Os(Processes((300, 1, "chrome")));

        var unbound = Plan(State(new[] { "process:chrome" }), new[] { Unbound }, os, blocked: t => t is SourceTarget.App);
        var broken  = Plan(State(new[] { "process:chrome" }), new[] { Broken },  os, blocked: t => t is SourceTarget.App);

        Assert.True(unbound.IsEmpty);
        Assert.Equal(new[] { new SlotRebind(0, null) }, broken.InputRebinds);
    }

    // --------------------------------------------------------------- devices

    [Fact]
    public void Devices_AreLeftAloneWhenTheyWerentScanned()
    {
        var plan = Plan(State(new[] { "mic" }), new[] { Unbound }, Os(Processes()));

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ReturningDevice_IsReattached()
    {
        var os = Os(Processes(), capture: new[] { new EndpointInfo("mic", "Mic", Rate) }, render: new[] { new EndpointInfo("out0", "Speakers", Rate) });

        var plan = Plan(State(new[] { "mic" }), new[] { Unbound }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.Endpoint("mic")) }, plan.InputRebinds);
    }

    [Fact]
    public void FaultedDevice_IsReopened()
    {
        var os = Os(Processes(), capture: new[] { new EndpointInfo("mic", "Mic", Rate) }, render: new[] { new EndpointInfo("out0", "Speakers", Rate) });

        var plan = Plan(State(new[] { "mic" }), new[] { Broken }, os);

        Assert.Equal(new[] { new SlotRebind(0, new SourceTarget.Endpoint("mic")) }, plan.InputRebinds);
    }

    [Fact]
    public void RemovedDevice_IsDetached()
    {
        var os = Os(Processes(), capture: Array.Empty<EndpointInfo>(), render: Array.Empty<EndpointInfo>());

        var plan = Plan(State(new[] { "mic" }), new[] { Healthy }, os);

        Assert.Equal(new[] { new SlotRebind(0, null) }, plan.InputRebinds);
        Assert.Equal(new[] { new SlotRebind(0, null) }, plan.OutputRebinds);
    }

    [Fact]
    public void DeviceAtAnotherSampleRate_IsDetachedAndNeverAdded()
    {
        var os = Os(
            Processes(),
            capture: new[] { new EndpointInfo("mic", "Mic", 44_100), new EndpointInfo("usb", "USB", 44_100) },
            render:  new[] { new EndpointInfo("out0", "Speakers", Rate) });

        var plan = Plan(State(new[] { "mic" }), new[] { Healthy }, os);

        Assert.Equal(new[] { new SlotRebind(0, null) }, plan.InputRebinds);
        Assert.Empty(plan.NewInputs);
    }

    [Fact]
    public void NewDevices_AreAppendedOnBothBuses()
    {
        var os = Os(
            Processes(),
            capture: new[] { new EndpointInfo("mic", "Mic", Rate), new EndpointInfo("headset-mic", "Headset Mic", Rate) },
            render:  new[] { new EndpointInfo("out0", "Speakers", Rate), new EndpointInfo("headset", "Headset", Rate) });

        var plan = Plan(State(new[] { "mic" }), new[] { Healthy }, os);

        Assert.Equal(
            new[] { new NewChannel("headset-mic", "Headset Mic", new SourceTarget.Endpoint("headset-mic")) },
            plan.NewInputs);
        Assert.Equal(
            new[] { new NewChannel("headset", "Headset", new SourceTarget.Endpoint("headset")) },
            plan.NewOutputs);
        Assert.Empty(plan.InputRebinds);
    }
}
