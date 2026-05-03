using System.Runtime.InteropServices;

namespace VoicemeterAlt.Host.Interop;

/// <summary>
/// P/Invoke wrappers for the Multimedia Class Scheduler Service. Tagging the
/// audio threads (capture, render, mix) as "Pro Audio" tells Windows to bias
/// the scheduler toward them so we don't get preempted mid-buffer.
/// </summary>
public static class Mmcss
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>
    /// Register the calling thread under the given MMCSS task name (e.g.
    /// "Pro Audio"). Returns a handle that must be passed to
    /// <see cref="Revert"/> when the thread exits, or <see cref="IntPtr.Zero"/>
    /// if registration failed (caller can carry on without the priority bump).
    /// </summary>
    public static IntPtr Begin(string taskName = "Pro Audio")
    {
        uint index = 0;
        var handle = AvSetMmThreadCharacteristicsW(taskName, ref index);
        return handle;
    }

    public static void Revert(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            AvRevertMmThreadCharacteristics(handle);
    }
}
