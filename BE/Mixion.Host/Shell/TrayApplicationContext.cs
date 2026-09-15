using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Mixion.Host.Diagnostics;

namespace Mixion.Host.Shell;

/// <summary>
/// System-tray UI shell for the host (BE: tray + run-on-startup).
///
/// Owns a <see cref="NotifyIcon"/> and a context menu with four actions:
/// <list type="bullet">
///   <item>Console — show / hide the diagnostic console window.</item>
///   <item>UI — open the host's bound URL in the default browser.</item>
///   <item>Run on startup (check-item) — toggles the HKCU Run entry.</item>
///   <item>Exit — initiates graceful shutdown of the whole host.</item>
/// </list>
///
/// The context derives from <see cref="ApplicationContext"/> rather than a
/// hidden <see cref="Form"/> so we get a real Win32 message loop without a
/// taskbar/Alt-Tab artifact. The loop runs on the STA entry thread; tray
/// events fire on that same thread, so menu handlers can touch the WinForms
/// state directly with no <c>Invoke</c> dance.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon              _notifyIcon;
    private readonly ContextMenuStrip        _menu;
    private readonly ToolStripMenuItem       _consoleItem;
    private readonly ToolStripMenuItem       _uiItem;
    private readonly ToolStripMenuItem       _startupItem;
    private readonly ToolStripMenuItem       _exitItem;
    private readonly string                  _url;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SynchronizationContext  _uiSyncContext;

    /// <summary>
    /// True once <see cref="ExitFromShutdown"/> has run, so a tray Exit
    /// click after the host has already been told to stop becomes a no-op
    /// instead of trying to <see cref="IHostApplicationLifetime.StopApplication"/>
    /// twice.
    /// </summary>
    private bool _shuttingDown;

    public TrayApplicationContext(string url, IHostApplicationLifetime lifetime)
    {
        _url       = url;
        _lifetime  = lifetime;
        _uiSyncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "TrayApplicationContext must be constructed on a thread with a WinForms SynchronizationContext.");

        _consoleItem = new ToolStripMenuItem("Console");
        _consoleItem.Click += (_, _) => ToggleConsole();

        _uiItem = new ToolStripMenuItem("UI");
        _uiItem.Click += (_, _) => OpenUi();

        _startupItem = new ToolStripMenuItem("Run on startup")
        {
            CheckOnClick = true,
            Checked      = StartupRegistration.IsEnabled(),
        };
        _startupItem.CheckedChanged += OnStartupCheckedChanged;

        _exitItem = new ToolStripMenuItem("Exit");
        _exitItem.Click += (_, _) => RequestExit();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_consoleItem);
        _menu.Items.Add(_uiItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _menu.Opening += (_, _) => RefreshDynamicMenuState();

        _notifyIcon = new NotifyIcon
        {
            Icon             = LoadTrayIcon(),
            Text             = $"Mixion v{AppVersion.Current}",
            ContextMenuStrip = _menu,
            Visible          = true,
        };
        // Double-click on the tray icon → open the UI, matching the tray
        // convention used by Spotify/Discord/Slack.
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenUi();
        };

        // Lifetime can be stopped from outside the UI (Ctrl+C in the
        // console, OS shutdown, an unhandled exception triggering the
        // host's stop). When that happens, drain the message loop so
        // Main can return.
        _lifetime.ApplicationStopping.Register(() =>
            _uiSyncContext.Post(_ => ExitFromShutdown(), null));
    }

    private void ToggleConsole()
    {
        if (ConsoleWindow.IsVisible)
            ConsoleWindow.Hide();
        else
            ConsoleWindow.Show();
    }

    private void OpenUi() => BrowserLauncher.Open(_url);

    private void OnStartupCheckedChanged(object? sender, EventArgs e)
    {
        var desired = _startupItem.Checked;
        try
        {
            StartupRegistration.SetEnabled(desired);
        }
        catch (Exception ex)
        {
            CrashLog.Write("WARN", "Could not update Run-on-startup registry entry.", ex);
            // Revert the visual state if we couldn't persist it. Suppress
            // the recursive event so we don't loop on the revert.
            _startupItem.CheckedChanged -= OnStartupCheckedChanged;
            try { _startupItem.Checked = !desired; }
            finally { _startupItem.CheckedChanged += OnStartupCheckedChanged; }

            _notifyIcon.ShowBalloonTip(
                timeout: 5000,
                tipTitle: "Mixion",
                tipText: "Could not update the Run-on-startup setting. See the log for details.",
                ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Refresh state that the user may have changed outside the menu —
    /// e.g. the registry entry being deleted by another tool, or a console
    /// window that was hidden via the tray then unhidden via Win32.
    /// </summary>
    private void RefreshDynamicMenuState()
    {
        // The startup item desync-vs-registry case: a third-party uninstaller
        // could have stripped the value while the host was running.
        var registryEnabled = StartupRegistration.IsEnabled();
        if (_startupItem.Checked != registryEnabled)
        {
            _startupItem.CheckedChanged -= OnStartupCheckedChanged;
            try { _startupItem.Checked = registryEnabled; }
            finally { _startupItem.CheckedChanged += OnStartupCheckedChanged; }
        }

        _consoleItem.Enabled = ConsoleWindow.IsToggleable;
        _consoleItem.Text = ConsoleWindow.IsVisible ? "Hide Console" : "Console";
    }

    /// <summary>
    /// User clicked tray Exit. Tell the host to drain (Kestrel stop, audio
    /// engine teardown) and let <see cref="ExitFromShutdown"/> close the
    /// message loop when that completes.
    /// </summary>
    private void RequestExit()
    {
        if (_shuttingDown) return;
        // Don't disable the tray icon yet — the user wants visual feedback
        // that something's happening, and the host still has work to do.
        _lifetime.StopApplication();
    }

    /// <summary>
    /// React to the host having decided to stop — could be a tray Exit, a
    /// Ctrl+C in the console, or OS log-off. Hide the tray icon and exit
    /// the message loop so <c>Application.Run</c> returns.
    /// </summary>
    private void ExitFromShutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { _notifyIcon.Visible = false; } catch { }
        ExitThread();
    }

    private static Icon LoadTrayIcon()
    {
        // Prefer the multi-resolution .ico embedded as an assembly resource —
        // unlike Icon.ExtractAssociatedIcon (which always returns 32×32), this
        // exposes every size in the .ico, so the tray and any taskbar
        // notifications render crisply on high-DPI displays.
        try
        {
            var asm = typeof(TrayApplicationContext).Assembly;
            using var stream = asm.GetManifestResourceStream("Mixion.Host.Resources.app.ico");
            if (stream is not null)
            {
                return new Icon(stream);
            }
        }
        catch
        {
            // Fall through to the file-based fallback.
        }

        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // Falling through to the default icon below.
        }
        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _notifyIcon.Visible = false; } catch { }
            _notifyIcon.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
