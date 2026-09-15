using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Mixion.Host.Web;

/// <summary>
/// Remembers the port the host last listened on and prefers it on the next
/// launch, so the UI keeps the same origin across restarts: a tab left open
/// (the host crashed, or the browser refused to close it) reconnects to the
/// new host instead of being stranded on a dead port, and bookmarks keep
/// working. Falls back to a random free port when the remembered one is taken.
/// <c>--port</c> always wins and is never remembered.
/// </summary>
public static class PortPreference
{
    /// <summary>Default location: <c>%LOCALAPPDATA%\Mixion\host-port.txt</c>.</summary>
    public static string DefaultFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mixion",
            "host-port.txt");

    /// <summary>The remembered port, or null when there is none or the file is unreadable.</summary>
    public static int? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path).Trim();
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && IsValidPort(port)
                ? port
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persist <paramref name="port"/>. Best-effort — the host runs fine without it.</summary>
    public static void Save(string path, int port)
    {
        if (!IsValidPort(port)) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, port.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // A read-only profile just means a random port next time.
        }
    }

    /// <summary>
    /// Port to listen on: the explicit <c>--port</c>, else the remembered port
    /// when <paramref name="isAvailable"/> says it's free, else 0 (the OS picks).
    /// </summary>
    public static int ResolveListenPort(int? explicitPort, int? rememberedPort, Func<int, bool> isAvailable)
    {
        if (explicitPort is { } fixedPort) return fixedPort;
        if (rememberedPort is { } port && isAvailable(port)) return port;
        return 0;
    }

    /// <summary>True when nothing is listening on <c>127.0.0.1:<paramref name="port"/></c> right now.</summary>
    public static bool IsAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsValidPort(int port) => port is > 1024 and <= 65535;
}
