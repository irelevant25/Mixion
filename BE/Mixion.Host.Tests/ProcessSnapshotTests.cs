using System.Diagnostics;
using Mixion.Host.Audio;
using Xunit;

namespace Mixion.Host.Tests;

public class ProcessSnapshotTests
{
    private static ProcessSnapshot Snapshot(params (int Pid, int Parent, string Name)[] entries)
        => ProcessSnapshot.FromEntries(entries.Select(e => new ProcessEntry(e.Pid, e.Parent, e.Name)));

    [Fact]
    public void ResolveAppRoot_WalksUpThroughParentsOfTheSameApp()
    {
        // explorer → chrome (browser) → chrome (utility) → chrome (audio service)
        var snapshot = Snapshot((1, 0, "explorer"), (100, 1, "chrome"), (200, 100, "chrome"), (300, 200, "Chrome"));

        Assert.Equal(100, snapshot.ResolveAppRoot(300));
        Assert.Equal(100, snapshot.ResolveAppRoot(100));
    }

    [Fact]
    public void ResolveAppRoot_ReturnsTheIdItselfWhenUnknown()
    {
        Assert.Equal(999, Snapshot((1, 0, "explorer")).ResolveAppRoot(999));
    }

    [Fact]
    public void ResolveAppRoot_SurvivesParentCyclesFromRecycledPids()
    {
        var snapshot = Snapshot((10, 20, "app"), (20, 10, "app"));

        var root = snapshot.ResolveAppRoot(10);

        Assert.Contains(root, new[] { 10, 20 });
    }

    [Fact]
    public void FindAppRoots_ReturnsOneRootPerRunningInstance()
    {
        var snapshot = Snapshot(
            (1, 0, "explorer"),
            (500, 1, "vlc"),
            (100, 1, "vlc"), (101, 100, "vlc"),
            (7, 1, "chrome"));

        Assert.Equal(new[] { 100, 500 }, snapshot.FindAppRoots("VLC"));
        Assert.Empty(snapshot.FindAppRoots("spotify"));
    }

    [Fact]
    public void IsSelfOrAncestor_FollowsTheParentChain()
    {
        var snapshot = Snapshot((1, 0, "explorer"), (10, 1, "powershell"), (20, 10, "Mixion"));

        Assert.True(snapshot.IsSelfOrAncestor(1, 20));
        Assert.True(snapshot.IsSelfOrAncestor(20, 20));
        Assert.False(snapshot.IsSelfOrAncestor(20, 1));
        Assert.False(snapshot.IsSelfOrAncestor(99, 20));
    }

    [Fact]
    public void FindCapturableAppRoots_SkipsTreesThatContainThisProcess()
    {
        // Pretend this test process was started from an "explorer" instance.
        var self     = Environment.ProcessId;
        var snapshot = Snapshot((1, 0, "explorer"), (2, 0, "explorer"), (self, 1, "testhost"));

        Assert.Equal(new[] { 2 }, snapshot.FindCapturableAppRoots("explorer"));
    }

    [Fact]
    public void IsRunning_RequiresTheSameExecutable()
    {
        var snapshot = Snapshot((42, 1, "vlc"));

        Assert.True(snapshot.IsRunning(42, "VLC"));
        Assert.False(snapshot.IsRunning(42, "chrome"));
        Assert.False(snapshot.IsRunning(7, "vlc"));
    }

    [Fact]
    public void Capture_SeesTheCurrentProcessWithItsParent()
    {
        using var self = Process.GetCurrentProcess();

        var snapshot = ProcessSnapshot.Capture();

        Assert.True(snapshot.TryGet(Environment.ProcessId, out var entry));
        Assert.Equal(self.ProcessName, entry.Name, ignoreCase: true);
        Assert.NotEqual(0, entry.ParentProcessId);
    }
}
