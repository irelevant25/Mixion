using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VoicemeterAlt.Host.Audio;

/// <summary>
/// One running process that currently has at least one active WASAPI audio
/// session on a render endpoint. The PID is what
/// <see cref="ProcessLoopbackCapture"/> binds against; the friendly name is
/// what the FE shows in the input picker.
/// </summary>
public sealed record AudioProcess(int ProcessId, string ProcessName, string? ExecutablePath);

/// <summary>
/// Walks every active render endpoint, asks each for its
/// <c>IAudioSessionManager2</c>, and dedupes the resulting sessions by PID. The
/// idea is "what's currently making sound on this PC" — those are the
/// candidates for per-process loopback capture.
///
/// <list type="bullet">
///   <item>System sessions (PID 0, the audio engine itself) are filtered out.</item>
///   <item>The host's own PID is filtered out — looping back our own render endpoints to ourselves would feedback through the mix.</item>
///   <item>A process holding sessions on multiple render endpoints appears once; per-process loopback is endpoint-agnostic.</item>
/// </list>
///
/// Enumeration is cheap (a handful of COM calls) but allocates, so it lives
/// off the audio path entirely. Callers should re-enumerate on demand rather
/// than caching — a process can start producing audio mid-session.
/// </summary>
public sealed class AudioProcessEnumerator
{
    private static readonly int OwnPid = Environment.ProcessId;

    public IReadOnlyList<AudioProcess> Enumerate()
    {
        var result = new List<AudioProcess>();
        var seen   = new HashSet<int>();

        var enumerator = new MMDeviceEnumerator();
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                AudioSessionManager? manager = null;
                try
                {
                    manager = device.AudioSessionManager;
                    var sessions = manager.Sessions;
                    if (sessions is null) continue;

                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        var pid = (int)session.GetProcessID;
                        if (pid <= 0)         continue; // 0 = system
                        if (pid == OwnPid)    continue; // never capture ourselves
                        if (!seen.Add(pid))   continue; // already noted from another endpoint

                        if (TryDescribeProcess(pid, out var name, out var path))
                            result.Add(new AudioProcess(pid, name, path));
                    }
                }
                catch
                {
                    // A device can vanish between EnumerateAudioEndPoints and
                    // the AudioSessionManager walk. Skip and move on.
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        finally
        {
            // MMDeviceEnumerator implements no IDisposable in NAudio's binding —
            // GC handles the underlying COM ref.
        }

        // Stable order by friendly name — keeps the FE picker from reshuffling
        // every time the user opens it.
        result.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Resolve a PID to a name + executable path. <c>Process.GetProcessById</c>
    /// throws if the PID has just exited; we treat that as "not eligible" and
    /// drop it from the result.
    /// </summary>
    private static bool TryDescribeProcess(int pid, out string name, out string? path)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try   { path = p.MainModule?.FileName; }
            catch { path = null; } // protected processes refuse MainModule access
            return true;
        }
        catch
        {
            name = string.Empty;
            path = null;
            return false;
        }
    }
}
