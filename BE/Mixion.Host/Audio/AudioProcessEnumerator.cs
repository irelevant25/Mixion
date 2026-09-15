using System.Runtime.InteropServices;
using static Mixion.Host.Audio.LowLatencyComInterop;

namespace Mixion.Host.Audio;

/// <summary>
/// An app that currently owns at least one WASAPI audio session on a render
/// endpoint. <see cref="SessionProcessId"/> is the process that opened the
/// session; <see cref="RootProcessId"/> is what <see cref="ProcessLoopbackCapture"/>
/// binds to so multi-process apps are captured whole — the top of that app's
/// same-name process tree, or the top-most process below it whose tree doesn't
/// contain Mixion (see <see cref="ProcessSnapshot.ResolveCaptureTarget"/>).
/// </summary>
public sealed record AudioProcess(int SessionProcessId, int RootProcessId, string ProcessName);

/// <summary>
/// Walks every active render endpoint's <c>IAudioSessionManager2</c> and
/// reports the apps behind the sessions — "what is making (or has made)
/// sound on this PC", the candidates for per-process loopback capture.
///
/// <list type="bullet">
///   <item>The system-sounds session and PID 0 are filtered out.</item>
///   <item>A tree loopback must never contain the host — it would capture Mixion's output and feed it back through the mix. When an app's root tree contains the host (Mixion was opened from Chrome's downloads, say), the app is captured through the top-most process below the root that doesn't, normally the one playing audio; when there's none (the host's own process, or Explorer playing a sound when Mixion was started from it), the app is left out.</item>
///   <item>Expired sessions are ignored.</item>
///   <item>Entries are deduplicated by capture target: normally one per running app instance, however many processes or endpoints it plays on, so two instances of the same app yield two entries with the same name. An app that started Mixion can yield one entry per child process that owns a session.</item>
/// </list>
///
/// Drives the session COM interfaces directly rather than through NAudio's
/// <c>AudioSessionManager</c>, which registers a session-created callback each
/// time it is constructed — the device watcher calls this every second and
/// must not pile up registrations. COM work always runs on an MTA thread.
/// </summary>
public sealed class AudioProcessEnumerator
{
    private const uint DeviceStateActive = 0x1;
    private const int  AudioSessionStateExpired = 2;

    private static readonly int  OwnPid = Environment.ProcessId;
    private static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>
    /// Enumerate audio-producing app instances, resolving names and root
    /// processes against <paramref name="snapshot"/> (a fresh one is taken when
    /// null). Sorted by name, then root PID, so the order is stable between calls.
    /// </summary>
    public IReadOnlyList<AudioProcess> Enumerate(ProcessSnapshot? snapshot = null)
    {
        var sessionPids = new HashSet<int>();
        RunOnMta(() => CollectSessionProcessIds(sessionPids));

        snapshot ??= ProcessSnapshot.Capture();

        var result      = new List<AudioProcess>();
        var seenTargets = new HashSet<int>();
        var ordered     = sessionPids.ToArray();
        Array.Sort(ordered);

        foreach (var pid in ordered)
        {
            // Gone since it opened the session — nothing to capture.
            if (!snapshot.TryGet(pid, out var entry) || entry.Name.Length == 0) continue;

            if (snapshot.ResolveCaptureTarget(pid, OwnPid) is not int target) continue;
            if (!seenTargets.Add(target)) continue;

            result.Add(new AudioProcess(pid, target, entry.Name));
        }

        result.Sort((a, b) =>
        {
            var byName = string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : a.RootProcessId.CompareTo(b.RootProcessId);
        });
        return result;
    }

    private static void CollectSessionProcessIds(HashSet<int> pids)
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid   = IID_IMMDeviceEnumerator;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var enumeratorObj);
        if (hr < 0 || enumeratorObj is null)
            throw new InvalidOperationException($"CoCreateInstance(MMDeviceEnumerator) failed. HRESULT 0x{hr:X8}.");

        object? collectionObj = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
            hr = enumerator.EnumAudioEndpoints(EDATAFLOW_RENDER, DeviceStateActive, out collectionObj);
            if (hr < 0 || collectionObj is null) return;

            var collection = (IMMDeviceCollection)collectionObj;
            if (collection.GetCount(out var count) < 0) return;

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0 || device is null) continue;
                try
                {
                    CollectFromDevice(device, pids);
                }
                catch (COMException)
                {
                    // The endpoint can vanish between enumeration and the
                    // session walk. Skip it; the next poll sees the new set.
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (collectionObj is not null) Marshal.ReleaseComObject(collectionObj);
            Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    private static void CollectFromDevice(IMMDevice device, HashSet<int> pids)
    {
        var iid = IidAudioSessionManager2;
        if (device.Activate(ref iid, CLSCTX_INPROC_SERVER, IntPtr.Zero, out var managerObj) < 0 || managerObj is null)
            return;

        object? sessionsObj = null;
        try
        {
            var manager = (IAudioSessionManager2)managerObj;
            if (manager.GetSessionEnumerator(out sessionsObj) < 0 || sessionsObj is null) return;

            var sessions = (IAudioSessionEnumerator)sessionsObj;
            if (sessions.GetCount(out var count) < 0) return;

            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var controlObj) < 0 || controlObj is null) continue;
                try
                {
                    if (controlObj is not IAudioSessionControl2 control) continue;
                    // S_OK (0) means this IS the system-sounds session.
                    if (control.IsSystemSoundsSession() == 0) continue;
                    if (control.GetState(out var state) >= 0 && state == AudioSessionStateExpired) continue;
                    // AUDCLNT_S_NO_SINGLE_PROCESS is a success code; the id is still usable.
                    if (control.GetProcessId(out var pid) < 0) continue;
                    if (pid == 0 || pid == OwnPid) continue;
                    pids.Add((int)pid);
                }
                finally
                {
                    Marshal.ReleaseComObject(controlObj);
                }
            }
        }
        finally
        {
            if (sessionsObj is not null) Marshal.ReleaseComObject(sessionsObj);
            Marshal.ReleaseComObject(managerObj);
        }
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    }

    /// <summary>IAudioSessionManager2 — only the vtable prefix we call is declared.</summary>
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out IntPtr audioVolume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator([MarshalAs(UnmanagedType.IUnknown)] out object sessionEnumerator);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    /// <summary>IAudioSessionControl2 — vtable up to <c>IsSystemSoundsSession</c>. Unused members take raw pointers.</summary>
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr displayName);
        [PreserveSig] int SetDisplayName(IntPtr displayName, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr iconPath);
        [PreserveSig] int SetIconPath(IntPtr iconPath, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr sessionIdentifier);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr sessionInstanceIdentifier);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
    }
}
