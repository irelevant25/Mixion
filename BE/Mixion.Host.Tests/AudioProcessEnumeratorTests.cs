using Mixion.Host.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// <see cref="AudioProcessEnumerator"/> drives the Core Audio session COM
/// interfaces directly. This compares it with NAudio's session API on the
/// machine running the tests — a wrong vtable layout would read the wrong
/// method and the two views would disagree. It needs the machine's audio
/// stack, so the release workflow (runners without audio devices) skips it.
/// </summary>
[Trait("Requires", "AudioDevices")]
public class AudioProcessEnumeratorTests
{
    [Fact]
    public void Enumerate_SeesTheSameAppsAsNAudio()
    {
        var expected = new HashSet<string>();
        var actual   = new HashSet<string>();

        // Sessions come and go while we look; retry before calling it a mismatch.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = ProcessSnapshot.Capture();
            expected = NAudioSessionApps(snapshot);
            actual   = new AudioProcessEnumerator().Enumerate(snapshot)
                .Select(p => p.ProcessName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (expected.SetEquals(actual)) return;
            Thread.Sleep(250);
        }

        Assert.Fail($"NAudio sees [{string.Join(", ", expected)}] but the enumerator sees [{string.Join(", ", actual)}].");
    }

    private static HashSet<string> NAudioSessionApps(ProcessSnapshot snapshot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                    var pid = (int)session.GetProcessID;
                    if (pid == 0 || !snapshot.TryGet(pid, out var entry)) continue;
                    if (snapshot.IsSelfOrAncestor(snapshot.ResolveAppRoot(pid), Environment.ProcessId)) continue;

                    names.Add(entry.Name);
                }
            }
        }
        return names;
    }
}
