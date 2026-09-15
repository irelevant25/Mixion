using System.Runtime.InteropServices;

namespace Mixion.Host.Audio;

/// <summary>One process as seen by a <see cref="ProcessSnapshot"/>.</summary>
public readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string Name);

/// <summary>
/// Point-in-time table of running processes (PID, parent PID, executable
/// name), taken with a single Toolhelp32 snapshot — one cheap kernel call
/// instead of a <see cref="System.Diagnostics.Process"/> object per PID. The
/// device watcher takes one per poll to answer "is Chrome running, and which
/// process is the root of its tree?".
///
/// Names are executable names without extension, matching
/// <see cref="System.Diagnostics.Process.ProcessName"/>, and compare
/// case-insensitively as Windows does.
/// </summary>
public sealed class ProcessSnapshot
{
    /// <summary>Guards against parent chains that loop through recycled PIDs.</summary>
    private const int MaxAncestorDepth = 64;

    private readonly Dictionary<int, ProcessEntry> _byId;

    private ProcessSnapshot(Dictionary<int, ProcessEntry> byId) => _byId = byId;

    public int Count => _byId.Count;

    public static ProcessSnapshot FromEntries(IEnumerable<ProcessEntry> entries)
    {
        var byId = new Dictionary<int, ProcessEntry>();
        foreach (var e in entries) byId[e.ProcessId] = e;
        return new ProcessSnapshot(byId);
    }

    public static ProcessSnapshot Capture()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
            throw new InvalidOperationException(
                $"CreateToolhelp32Snapshot failed (Win32 error {Marshal.GetLastWin32Error()}).");

        try
        {
            var byId  = new Dictionary<int, ProcessEntry>(512);
            var entry = new NativeMethods.ProcessEntry32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32W>(),
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
                return new ProcessSnapshot(byId);

            do
            {
                var pid = (int)entry.th32ProcessID;
                if (pid == 0) continue; // System Idle Process
                var name = Path.GetFileNameWithoutExtension(entry.szExeFile) ?? string.Empty;
                byId[pid] = new ProcessEntry(pid, (int)entry.th32ParentProcessID, name);
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));

            return new ProcessSnapshot(byId);
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    public bool TryGet(int processId, out ProcessEntry entry) => _byId.TryGetValue(processId, out entry);

    /// <summary>
    /// True when <paramref name="processId"/> is running under
    /// <paramref name="name"/>. A recycled PID now owned by another executable
    /// doesn't count.
    /// </summary>
    public bool IsRunning(int processId, string name)
        => _byId.TryGetValue(processId, out var e) && NameEquals(e.Name, name);

    /// <summary>
    /// Walk up from <paramref name="processId"/> while the parent runs the
    /// same executable and return the top-most one — the app's root process.
    /// For Chrome that's the browser process above the audio-service helper
    /// that actually owns the audio session. Returns <paramref name="processId"/>
    /// itself when it isn't in the snapshot.
    /// </summary>
    public int ResolveAppRoot(int processId)
    {
        if (!_byId.TryGetValue(processId, out var current)) return processId;

        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            if (current.ParentProcessId == current.ProcessId) break;
            if (!_byId.TryGetValue(current.ParentProcessId, out var parent)) break;
            if (!NameEquals(parent.Name, current.Name)) break;
            current = parent;
        }
        return current.ProcessId;
    }

    /// <summary>
    /// Root processes (see <see cref="ResolveAppRoot"/>) of every running
    /// instance of <paramref name="name"/>, lowest PID first.
    /// </summary>
    public IReadOnlyList<int> FindAppRoots(string name)
    {
        var roots = new List<int>();
        foreach (var e in _byId.Values)
        {
            if (!NameEquals(e.Name, name)) continue;
            if (e.ParentProcessId != e.ProcessId
                && _byId.TryGetValue(e.ParentProcessId, out var parent)
                && NameEquals(parent.Name, e.Name))
            {
                continue; // a helper inside another instance's tree
            }
            roots.Add(e.ProcessId);
        }
        roots.Sort();
        return roots;
    }

    /// <summary>
    /// <see cref="FindAppRoots"/> without roots whose process tree contains this
    /// host. An include-tree loopback on such a root — Explorer, when Mixion was
    /// started from it — would capture Mixion's own output and feed it back.
    /// </summary>
    public IReadOnlyList<int> FindCapturableAppRoots(string name)
    {
        var roots = FindAppRoots(name);
        var self  = Environment.ProcessId;
        return roots.Where(root => !IsSelfOrAncestor(root, self)).ToList();
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="processId"/>
    /// itself or one of its ancestors in this snapshot.
    /// </summary>
    public bool IsSelfOrAncestor(int candidate, int processId)
    {
        var current = processId;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            if (current == candidate) return true;
            if (!_byId.TryGetValue(current, out var entry) || entry.ParentProcessId == current) return false;
            current = entry.ParentProcessId;
        }
        return false;
    }

    public static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static class NativeMethods
    {
        public const uint Th32csSnapProcess = 0x00000002;

        public static readonly IntPtr InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32W
        {
            public uint    dwSize;
            public uint    cntUsage;
            public uint    th32ProcessID;
            public IntPtr  th32DefaultHeapID;
            public uint    th32ModuleID;
            public uint    cntThreads;
            public uint    th32ParentProcessID;
            public int     pcPriClassBase;
            public uint    dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string? szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
