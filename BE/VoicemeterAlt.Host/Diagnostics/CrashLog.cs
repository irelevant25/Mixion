using System.Globalization;
using System.Text;

namespace VoicemeterAlt.Host.Diagnostics;

/// <summary>
/// File-based diagnostic log under
/// <c>%LOCALAPPDATA%\VoicemeterAlt\logs\host-yyyyMMdd.log</c> (BE-091). The
/// active file rotates when it grows past <see cref="MaxFileBytes"/> — the
/// current file is moved to <c>host-yyyyMMdd.1.log</c> (a previous .1 is
/// promoted to .2, …, up to <see cref="MaxRotatedFiles"/>) before the next
/// write opens a fresh file.
///
/// Hooks into <see cref="AppDomain.UnhandledException"/> and
/// <see cref="TaskScheduler.UnobservedTaskException"/> so any crash that
/// would otherwise leave nothing behind in a packaged single-file build
/// lands as a stack trace on disk. Writes are guarded by a lock — the log
/// is touched from the control plane (Kestrel + finaliser threads), never
/// from the audio path.
/// </summary>
public static class CrashLog
{
    private const long MaxFileBytes    = 10L * 1024 * 1024;
    private const int  MaxRotatedFiles = 5;

    private static readonly object Gate = new();

    private static string? _logDirectory;
    private static bool    _initialized;

    public static string DefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoicemeterAlt",
            "logs");

    /// <summary>
    /// Wire crash hooks once at process startup. Idempotent — repeated calls
    /// are a no-op so tests that boot the host multiple times don't pile up
    /// duplicate handlers.
    /// </summary>
    public static void Install(string? directory = null)
    {
        lock (Gate)
        {
            if (_initialized) return;
            _logDirectory = directory ?? DefaultDirectory();
            try { Directory.CreateDirectory(_logDirectory); } catch { /* logged below if writes fail */ }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Write("FATAL", "AppDomain.UnhandledException", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("WARN", "TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
            _initialized = true;

            Write("INFO", $"Log opened in {_logDirectory}", null);
        }
    }

    /// <summary>
    /// Append one structured line. <paramref name="ex"/> is dumped with type,
    /// message, and stack trace below the header line. Failures (disk full,
    /// permission denied) are swallowed so the logger can never escalate
    /// itself into the very crash it's trying to record.
    /// </summary>
    public static void Write(string level, string message, Exception? ex)
    {
        lock (Gate)
        {
            if (_logDirectory is null) return;
            try
            {
                var path = ResolveCurrentFile(_logDirectory);
                RotateIfNeeded(path);

                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));

                var ts = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
                writer.Write(ts);
                writer.Write(' ');
                writer.Write(level);
                writer.Write(' ');
                writer.WriteLine(message);
                if (ex is not null)
                {
                    writer.WriteLine(ex);
                }
            }
            catch
            {
                // Best-effort logger; disk errors must never crash the host.
            }
        }
    }

    private static string ResolveCurrentFile(string dir)
        => Path.Combine(dir, $"host-{DateTime.Now:yyyyMMdd}.log");

    private static void RotateIfNeeded(string path)
    {
        FileInfo? info;
        try { info = new FileInfo(path); }
        catch { return; }

        if (!info.Exists || info.Length < MaxFileBytes) return;

        // Promote .N → .N+1 from the top down so we never overwrite a
        // newer rotation by accident; oldest gets dropped.
        for (int i = MaxRotatedFiles; i >= 1; i--)
        {
            var src = $"{path}.{i}";
            var dst = $"{path}.{i + 1}";
            try
            {
                if (File.Exists(src))
                {
                    if (i == MaxRotatedFiles)
                    {
                        File.Delete(src);
                    }
                    else
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }
            }
            catch { /* ignore rotation hiccups; we still try to write below */ }
        }

        try
        {
            var first = $"{path}.1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(path, first);
        }
        catch { /* if we can't move, the next write may exceed the cap by one frame; not catastrophic */ }
    }
}
