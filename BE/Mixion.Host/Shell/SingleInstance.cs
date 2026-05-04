using System.Diagnostics;
using Mixion.Host.Shell;

namespace Mixion.Host.Shell;

/// <summary>
/// Named-mutex single-instance gate (BE-090). The first host process owns the
/// mutex and writes the URL it bound to into a small "rendezvous" file under
/// <c>%LOCALAPPDATA%\Mixion\runtime\url.txt</c>. Subsequent launches
/// fail to acquire the mutex, read that file, open the URL in the default
/// browser, and exit cleanly — so double-clicking the .exe a second time pops
/// a fresh tab pointing at the already-running host instead of crashing on a
/// duplicate Kestrel bind.
///
/// The mutex name is prefixed <c>Local\</c> so the gate is per-user-session
/// (one per Remote Desktop / fast-user-switch session).
/// </summary>
public sealed class SingleInstanceGate : IDisposable
{
    private const string MutexName = @"Local\Mixion";

    private static readonly string RuntimeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mixion",
        "runtime");

    private static readonly string UrlFile = Path.Combine(RuntimeDir, "url.txt");

    private readonly Mutex _mutex;
    private readonly bool  _ownsMutex;

    private SingleInstanceGate(Mutex mutex, bool ownsMutex)
    {
        _mutex     = mutex;
        _ownsMutex = ownsMutex;
    }

    /// <summary>
    /// Try to claim the single-instance mutex. Returns the gate (caller must
    /// dispose at shutdown) when this is the first instance; returns
    /// <c>null</c> when another instance is already running — in that case
    /// <see cref="ForwardToRunningInstance"/> has already opened the browser
    /// at the existing host URL and the caller should exit.
    /// </summary>
    public static SingleInstanceGate? AcquireOrForward(bool noBrowser)
    {
        // createdNew = true → we are the first instance.
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceGate(mutex, ownsMutex: true);
        }

        // We did not create the mutex; another process owns it. Don't hold a
        // reference — release the handle and forward.
        mutex.Dispose();
        ForwardToRunningInstance(noBrowser);
        return null;
    }

    /// <summary>
    /// Persist the URL the first instance bound to so a follow-up launch can
    /// open it in the browser. Best-effort — failure to write does not stop
    /// the host from running.
    /// </summary>
    public void PublishUrl(string url)
    {
        try
        {
            Directory.CreateDirectory(RuntimeDir);
            File.WriteAllText(UrlFile, url);
        }
        catch
        {
            // The rendezvous is a convenience for the second-launch case; if
            // we can't write it, the host still runs fine.
        }
    }

    public void Dispose()
    {
        try
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { /* already released / abandoned */ }
                // Wipe the rendezvous file so a stale URL doesn't survive a
                // crash. A surviving file is harmless (the next launch falls
                // back to spawning a fresh host) but tidy is better.
                try { if (File.Exists(UrlFile)) File.Delete(UrlFile); } catch { }
            }
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    private static void ForwardToRunningInstance(bool noBrowser)
    {
        string? url = null;
        try
        {
            if (File.Exists(UrlFile))
                url = File.ReadAllText(UrlFile).Trim();
        }
        catch
        {
            url = null;
        }

        if (!string.IsNullOrEmpty(url))
        {
            // Surface the URL so it's visible even in --no-browser scenarios.
            Console.WriteLine();
            Console.WriteLine($"  Mixion is already running at {url}");
            Console.WriteLine();
            if (!noBrowser) BrowserLauncher.Open(url);
        }
        else
        {
            // Mutex was held but no URL file — the running instance crashed
            // mid-startup or is still binding. Tell the user; they can retry.
            Console.WriteLine();
            Console.WriteLine("  Mixion is already running but its URL is unknown.");
            Console.WriteLine("  Wait a few seconds and retry, or check the existing console window.");
            Console.WriteLine();
        }
    }
}
