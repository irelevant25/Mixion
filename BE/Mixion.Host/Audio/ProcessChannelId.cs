namespace Mixion.Host.Audio;

/// <summary>
/// Channel-id scheme for per-process loopback inputs: <c>process:&lt;name&gt;</c>,
/// where the name is the executable name without extension (what
/// <see cref="System.Diagnostics.Process.ProcessName"/> reports, e.g. <c>chrome</c>).
///
/// Keyed by name, never by PID, so "chrome" is the same channel across Chrome
/// restarts, Mixion restarts and presets. Windows treats process names
/// case-insensitively, so lookups by name should too.
/// </summary>
public static class ProcessChannelId
{
    public const string Prefix = "process:";

    public static string For(string processName) => Prefix + processName;

    public static bool IsProcess(string? channelId)
        => channelId is not null && channelId.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryGetProcessName(string? channelId, out string processName)
    {
        if (IsProcess(channelId) && channelId!.Length > Prefix.Length)
        {
            processName = channelId[Prefix.Length..];
            return true;
        }
        processName = string.Empty;
        return false;
    }

    /// <summary>Label shown in the FE picker and strip header, e.g. <c>chrome (app)</c>.</summary>
    public static string DisplayName(string processName) => $"{processName} (app)";
}
