using System.Diagnostics;
using Microsoft.Win32;

namespace VoicemeterAlt.Host.Shell;

/// <summary>
/// Reads / writes the per-user "Run on startup" registry entry under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Backs the
/// "Run on startup" check-item in the system-tray menu.
///
/// Per-user (HKCU) was chosen over per-machine (HKLM) on purpose:
/// HKLM requires admin privileges and applies to every account on the
/// PC, which is rarely what an audio-mixer user wants. HKCU is the same
/// scope Spotify, Discord, etc. use.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "VoicemeterAlt";

    /// <summary>
    /// True iff the Run key currently points at *this* executable. If the
    /// user has copied the .exe to a new location and the registry still
    /// references the old path, we report false — they'll need to re-tick
    /// the menu item to repair it. Re-ticking writes the current path.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string value) return false;

            return string.Equals(
                ExtractExecutablePath(value),
                CurrentExecutablePath(),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Add or remove the auto-start entry. The launch command is just the
    /// .exe path, quoted to survive spaces — no extra args, so behaviour at
    /// boot matches a fresh double-click (single-instance gate, browser
    /// launch, tray, all default).
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException(
                $@"Could not open HKCU\{RunKeyPath} for write.");

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{CurrentExecutablePath()}\"", RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string CurrentExecutablePath()
    {
        // Environment.ProcessPath is null when running as a single-file
        // app extracted to a temp dir on some older runtimes; fall back to
        // MainModule which always resolves to the launching .exe.
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot resolve current executable path.");
    }

    /// <summary>
    /// The Run value can be either a bare path or a quoted path optionally
    /// followed by arguments — peel that back to just the .exe so we can
    /// compare against the current process path.
    /// </summary>
    private static string ExtractExecutablePath(string runValue)
    {
        var trimmed = runValue.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1) return trimmed.Substring(1, end - 1);
        }
        // Unquoted: take the whole thing (the legacy form) — anything after
        // a space would be an argument we don't write but might be present
        // from a hand-edit. Strip it.
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
