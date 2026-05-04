using System.Diagnostics;

namespace Mixion.Host.Shell;

public static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort. The URL is also printed to the console so the user
            // can paste it manually if the shell handler fails for any reason.
        }
    }
}
