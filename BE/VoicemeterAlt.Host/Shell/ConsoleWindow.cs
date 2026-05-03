using System.Runtime.InteropServices;

namespace VoicemeterAlt.Host.Shell;

/// <summary>
/// Console window control for the WinExe-launched host. Because the project
/// is built as <c>WinExe</c>, double-clicking the .exe attaches no console;
/// this helper allocates a hidden console at startup so all
/// <see cref="Console"/>-based output (Kestrel banner, ILogger, RPC traces)
/// continues to land somewhere, and the tray "Console" item can show / hide
/// that window on demand.
///
/// When the host is launched with stdout already wired up (cmd/PowerShell
/// inheritance, shell redirection, or <c>dotnet run</c>'s pipe) we leave it
/// alone — output flows to the existing destination — and the tray
/// "Console" item is disabled so we don't try to show a window we don't own.
/// </summary>
internal static class ConsoleWindow
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    private const int  STD_OUTPUT_HANDLE = -11;
    private const int  SW_HIDE = 0;
    private const int  SW_SHOW = 5;
    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0x00000000;

    /// <summary>
    /// True when this process owns a console it allocated itself
    /// (vs. having attached to a parent terminal). Only owned consoles
    /// participate in tray show/hide — hiding the user's PowerShell
    /// window would be hostile.
    /// </summary>
    private static bool _ownsConsole;

    /// <summary>
    /// If stdout is unwired (pure GUI launch), allocate a hidden console
    /// so future <c>Console.Write*</c> calls have somewhere to land.
    /// Otherwise leave the existing wiring alone. Best-effort — failures
    /// don't abort startup.
    /// </summary>
    public static void Initialize()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            // Already had a console at process start (rare with WinExe but
            // possible under a debugger). Nothing more to do.
            return;
        }

        // `dotnet run`, shell redirection, or a cmd parent that handed us its
        // console handle all surface here as a non-NULL stdout. Leave that
        // wiring alone — output already goes somewhere visible — and don't
        // expose a tray toggle (we don't own a window we could hide).
        if (GetStdHandle(STD_OUTPUT_HANDLE) != IntPtr.Zero)
            return;

        // Pure GUI launch (double-click, Task Scheduler, "Run on startup"):
        // there's literally no stdout. Make our own console. Hide it
        // immediately and remove the X so a stray click can't kill the
        // whole host process — tray "Console" is the supported control.
        if (AllocConsole())
        {
            _ownsConsole = true;
            RebindStandardStreams();
            DisableCloseButton();
            try { Console.Title = "VoicemeterAlt — Console"; }
            catch { /* Console.Title can throw on rare consoles, ignore. */ }
            ShowWindow(GetConsoleWindow(), SW_HIDE);
        }
    }

    /// <summary>True iff a console window exists and is currently visible.</summary>
    public static bool IsVisible
    {
        get
        {
            var hWnd = GetConsoleWindow();
            return hWnd != IntPtr.Zero && IsWindowVisible(hWnd);
        }
    }

    /// <summary>True iff the console is owned by this process (vs. a borrowed parent terminal).</summary>
    public static bool IsToggleable => _ownsConsole;

    public static void Show()
    {
        var hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;
        ShowWindow(hWnd, SW_SHOW);
        SetForegroundWindow(hWnd);
    }

    public static void Hide()
    {
        // Refuse to hide a parent-owned console; the user would lose their shell.
        if (!_ownsConsole) return;
        var hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;
        ShowWindow(hWnd, SW_HIDE);
    }

    /// <summary>
    /// After AllocConsole the .NET <see cref="Console"/> writers may still
    /// hold cached handles to the original (NULL) std streams. Re-open them
    /// so log lines actually reach the new console.
    /// </summary>
    private static void RebindStandardStreams()
    {
        try
        {
            var stdOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdOut);
            var stdErr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stdErr);
        }
        catch
        {
            // If rebinding fails, .NET's pre-existing writers stay in place.
            // We get a console window but no output — annoying, not fatal.
        }
    }

    /// <summary>
    /// Strip the X button from the console's system menu. The default close
    /// behaviour delivers CTRL_CLOSE_EVENT and Windows then terminates the
    /// process within a few seconds — we'd rather make the button unclickable
    /// than try to fight that race. Tray "Exit" remains the only way out.
    /// </summary>
    private static void DisableCloseButton()
    {
        var hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero) return;
        var menu = GetSystemMenu(hWnd, bRevert: false);
        if (menu != IntPtr.Zero)
            DeleteMenu(menu, SC_CLOSE, MF_BYCOMMAND);
    }
}
