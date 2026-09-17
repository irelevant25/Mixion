using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mixion.Host.Audio;
using Mixion.Host.Diagnostics;
using Mixion.Host.Ipc;
using Mixion.Host.Ipc.Handlers;
using Mixion.Host.Shell;
using Mixion.Host.State;
using Mixion.Host.Web;
// Disambiguate from System.Windows.Forms.MessageBox now that the host links
// against WinForms. The interop helper is the Win32 MessageBoxW we use for
// the pre-server VB-CABLE dialog.
using InteropMessageBox = Mixion.Host.Interop.MessageBox;

namespace Mixion.Host;

internal static class Program
{
    private const int ExitDriverMissing = 2;

    /// <summary>
    /// STA entry — required because the system-tray UI runs on a WinForms
    /// message pump. Async work (web host startup, audio engine, shutdown
    /// drain) is offloaded to <see cref="Task.Run(Func{Task})"/> so this
    /// thread is free to drive <see cref="Application.Run()"/>.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var cli = CliArgs.Parse(args);

        // Crash + diagnostic log first thing so any failure during startup
        // (driver probe, mutex acquisition, Kestrel bind) lands on disk
        // instead of vanishing with the console window. (BE-091)
        CrashLog.Install();
        CrashLog.Write(
            "INFO",
            $"Host starting (pid {Environment.ProcessId}, version {AppVersion.Current}); raw args=[{string.Join(' ', args)}]; "
            + $"parsed port={(cli.Port?.ToString() ?? "<remembered or random>")} noBrowser={cli.NoBrowser} "
            + $"skipDriverCheck={cli.SkipDriverCheck}",
            null);

        // Allocate (or borrow) a console BEFORE anything writes to stdout
        // so the very first `Console.Write*` reaches the user. WinExe builds
        // start with no console attached — without this, log output silently
        // disappears.
        ConsoleWindow.Initialize();

        // Single-instance gate. If we are NOT the first instance the gate
        // opens the existing host's URL in the browser and returns null —
        // we exit cleanly, leaving the running host untouched. (BE-090)
        using var instance = SingleInstanceGate.AcquireOrForward(cli.NoBrowser);
        if (instance is null)
        {
            CrashLog.Write("INFO", "Another instance already owns the mutex; forwarding and exiting.", null);
            return 0;
        }

        // Driver presence check, BEFORE any HTTP/WS/audio work. If
        // VB-CABLE is missing the user gets a native dialog and we
        // exit cleanly — no port bound, no console window left over.
        if (!cli.SkipDriverCheck)
        {
            var probe = DriverProbe.Probe(new DeviceEnumerator());
            if (!probe.Found)
            {
                CrashLog.Write("WARN", "Basic VB-CABLE not detected; aborting startup.", null);
                InteropMessageBox.ShowError(
                    caption: "Mixion — VB-CABLE not found",
                    text:
                        "Basic VB-CABLE driver not detected.\n\n" +
                        "Mixion requires the basic single-cable VB-CABLE — the free download " +
                        "labelled \"VB-CABLE Virtual Audio Device\" on vb-audio.com — which surfaces " +
                        "exactly one \"CABLE Input\" and \"CABLE Output\" endpoint pair.\n\n" +
                        "Higher-tier products (VB-CABLE A+B, VB-CABLE C+D, Voicemeeter) do not satisfy " +
                        "this requirement on their own.\n\n" +
                        "Install the basic VB-CABLE from https://vb-audio.com/Cable/ and relaunch the app.");
                return ExitDriverMissing;
            }
        }

        // Build the web host. Same process serves /api/*, /ws, and the
        // embedded Angular bundle at /. Bound to 127.0.0.1 only.
        var builder = WebApplication.CreateBuilder(args);

        // Reuse the previous run's port when it's free so an open UI tab keeps
        // its origin across restarts; --port always wins.
        var portFile   = PortPreference.DefaultFilePath();
        var listenPort = PortPreference.ResolveListenPort(
            cli.Port, PortPreference.Read(portFile), PortPreference.IsAvailable);
        builder.WebHost.ConfigureKestrel(k =>
            HttpServer.ConfigureKestrel(k, new HostBindingOptions(listenPort == 0 ? null : listenPort)));

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        // Mirror Microsoft.Extensions.Logging output into the rotating
        // crash log file so packaged builds (no console attached) still
        // leave a forensic trail. (BE-091)
        builder.Logging.AddProvider(new FileLoggerProvider());

        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<EngineHost>();
        builder.Services.AddSingleton<TelemetryHub>();
        builder.Services.AddHostedService<TelemetryBroadcaster>();
        builder.Services.AddHostedService<SpectrumBroadcaster>();
        builder.Services.AddSingleton<IEndpointSource, DeviceEnumerator>();
        builder.Services.AddSingleton(provider => new AudioSettingsStore(
            AudioSettingsStore.DefaultFilePath(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("AudioSettingsStore")));
        builder.Services.AddSingleton<ProcessSuppressions>();
        builder.Services.AddSingleton(provider => new EngineFactory(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("MixEngine"),
            provider.GetRequiredService<AudioSettingsStore>(),
            provider.GetRequiredService<ProcessSuppressions>()));
        // Keeps channels bound to whatever Windows offers: apps re-attach by
        // name when they restart, devices follow plug / unplug, new ones are
        // attached — all in place, without rebuilding the engine.
        builder.Services.AddSingleton(provider => new TopologyReconciler(
            provider.GetRequiredService<EngineHost>(),
            provider.GetRequiredService<EngineFactory>(),
            provider.GetRequiredService<ProcessSuppressions>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("DeviceWatcher")));
        builder.Services.AddSingleton<AudioDeviceWatcher>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AudioDeviceWatcher>());
        builder.Services.AddSingleton<CurrentPresetState>();
        builder.Services.AddSingleton(provider => new PresetStore(
            PresetStore.DefaultDirectory(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("PresetStore")));
        builder.Services.AddSingleton(provider =>
        {
            var dispatcher = new JsonRpcDispatcher(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("JsonRpc"));

            DeviceHandlers.Register(
                dispatcher,
                provider.GetRequiredService<IEndpointSource>(),
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<EngineFactory>(),
                provider.GetRequiredService<ProcessSuppressions>());
            ProcessHandlers.Register(
                dispatcher,
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<EngineFactory>(),
                provider.GetRequiredService<ProcessSuppressions>());
            StateHandlers.Register(dispatcher, provider.GetRequiredService<EngineHost>());
            RoutingHandlers.Register(dispatcher, provider.GetRequiredService<EngineHost>());
            ChannelHandlers.Register(dispatcher, provider.GetRequiredService<EngineHost>());
            DspHandlers.Register(dispatcher, provider.GetRequiredService<EngineHost>());
            TelemetryHandlers.Register(
                dispatcher,
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<TelemetryHub>());
            LatencyHandlers.Register(
                dispatcher,
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("LatencyHandlers"));
            AudioSettingsHandlers.Register(
                dispatcher,
                provider.GetRequiredService<AudioSettingsStore>(),
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<EngineFactory>());
            PresetHandlers.Register(
                dispatcher,
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<EngineFactory>(),
                provider.GetRequiredService<PresetStore>(),
                provider.GetRequiredService<CurrentPresetState>(),
                provider.GetRequiredService<IEndpointSource>(),
                provider.GetRequiredService<ProcessSuppressions>(),
                provider.GetRequiredService<AudioDeviceWatcher>());
            SystemHandlers.Register(dispatcher);
            return dispatcher;
        });

        var app = builder.Build();

        var engineHost = app.Services.GetRequiredService<EngineHost>();
        var hub        = app.Services.GetRequiredService<TelemetryHub>();
        // Channel-set and availability changes (device watcher, rebuilds) are
        // pushed to every open UI, so pickers and strips follow devices and
        // apps coming and going without a refresh.
        engineHost.TopologyChanged += state => hub.Broadcast("stateChanged", state.ToDto());

        // Before anything that answers: /api/session and /ws must never serve a
        // foreign page (cross-origin WebSocket, DNS rebinding).
        app.UseLoopbackRequestGuard();
        app.UseWebSockets();

        app.MapHealth();
        app.MapSession();
        app.MapProcessIcon();
        app.MapControlSocket();

        app.UseEmbeddedSpa();

        // Start, then read back the bound URL so we can log + open it.
        // The STA entry thread can't await directly without losing its
        // STA semantics for the upcoming WinForms message pump, so we
        // hand the awaitable to a worker and block here.
        Task.Run(() => app.StartAsync()).GetAwaiter().GetResult();

        var url = HttpServer.ResolveBoundUrl(app.Services.GetRequiredService<IServer>());
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Host");
        logger.LogInformation("Mixion host listening at {Url}", url);
        Console.WriteLine();
        Console.WriteLine($"  Mixion → {url}");
        Console.WriteLine();

        if (cli.Port is null)
            PortPreference.Save(portFile, new Uri(url).Port);

        // Publish the URL so a second-instance launch can forward to it. (BE-090)
        instance.PublishUrl(url);

        // Start the audio engine: every active device plus a loopback for every
        // app producing audio, all routes off. It goes through EngineHost like
        // any rebuild, so a Rescan requested meanwhile can't race it. Failures
        // here don't take the host down — the UI can still talk to /api/*, the
        // user just won't hear audio.
        try
        {
            Task.Run(() => engineHost.RebuildAsync(app.Services.GetRequiredService<EngineFactory>()))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audio engine did not start; HTTP/WS surface remains available.");
            // Make the failure visible in a plain dotnet run too — the
            // structured logger is easy to miss in a busy console.
            Console.WriteLine();
            Console.WriteLine($"  ⚠ Audio engine did not start: {ex.Message}");
            Console.WriteLine($"    UI will load, but the matrix will be empty until this is resolved.");
            Console.WriteLine();
        }

        // Auto-load the last preset the user worked with (BE-side persistence
        // of "current setup").
        bool LoadLastPreset() => TryLoadLastPreset(
            engineHost,
            app.Services.GetRequiredService<EngineFactory>(),
            app.Services.GetRequiredService<PresetStore>(),
            app.Services.GetRequiredService<CurrentPresetState>(),
            app.Services.GetRequiredService<IEndpointSource>(),
            app.Services.GetRequiredService<ProcessSuppressions>(),
            logger);

        if (engineHost.Current is not null)
        {
            LoadLastPreset();
        }
        else
        {
            // Audio didn't come up (e.g. Windows Audio not ready yet at logon).
            // The device watcher keeps retrying; apply the preset to the first
            // engine that does start, exactly once — then have open pages
            // re-hydrate, since they loaded without it.
            var presetPending = 1;
            void OnFirstEngine(MixerState started)
            {
                if (Interlocked.Exchange(ref presetPending, 0) == 0) return;
                engineHost.TopologyChanged -= OnFirstEngine;
                _ = Task.Run(() =>
                {
                    if (LoadLastPreset()) hub.Broadcast("sessionChanged", null);
                });
            }
            engineHost.TopologyChanged += OnFirstEngine;

            // A recovery may have finished between the check above and subscribing.
            if (engineHost.Current is { } running) OnFirstEngine(running.SnapshotState());
        }

        // Pages that loaded while the engine was starting have been waiting on this.
        engineHost.CompleteStartup();

        if (!cli.NoBrowser)
            BrowserLauncher.Open(url);

        // Graceful shutdown (BE-092). The .NET host already wires Ctrl+C →
        // IHostApplicationLifetime; we hook ProcessExit too so an abnormal
        // termination still gets a log entry and an explicit Console.CancelKeyPress
        // handler so the trace shows the source of the shutdown signal.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        Console.CancelKeyPress += (_, e) =>
        {
            // Setting Cancel = true tells the runtime "I'll handle it" so
            // the process doesn't get killed mid-flush. WaitForShutdownAsync
            // returns once the lifetime is done draining. The tray context
            // observes the same lifetime cancellation and drains the
            // WinForms message loop, so this single signal stops both.
            e.Cancel = true;
            CrashLog.Write("INFO", "Ctrl+C received; initiating graceful shutdown.", null);
            lifetime.StopApplication();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            CrashLog.Write("INFO", "Process exiting.", null);

        // Install the WinForms SynchronizationContext now, BEFORE constructing
        // the tray context. Application.Run installs one automatically — but
        // only once it actually starts pumping, which is too late: the tray
        // context's constructor needs SynchronizationContext.Current to capture
        // a UI marshaler for the lifetime-stopping callback. Without this, the
        // constructor throws, the catch in Main prints FATAL ERROR and blocks
        // on Console.ReadKey, and the host stays alive in Task Manager (Kestrel
        // + audio still running) but no tray icon ever appears.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // Tray UI runs on this STA thread. Application.Run blocks until the
        // user picks tray Exit OR ApplicationStopping fires from elsewhere
        // (Ctrl+C, OS log-off). The browser/audio/Kestrel are all live by
        // the time we get here.
        using (var trayContext = new TrayApplicationContext(url, lifetime))
        {
            Application.Run(trayContext);
        }

        // The message loop has drained. If shutdown was driven externally
        // (Ctrl+C), lifetime is already stopping; otherwise the user clicked
        // tray Exit and we kick the lifetime now to start the host drain.
        if (!lifetime.ApplicationStopping.IsCancellationRequested)
            lifetime.StopApplication();

        // Stopping the lifetime also tells every open UI the host is exiting
        // (WebSocketEndpoint), so their tabs close instead of reconnecting.
        Task.Run(() => app.WaitForShutdownAsync()).GetAwaiter().GetResult();

        // Order matters per BE-092: Kestrel has stopped accepting new
        // connections and the device watcher has stopped by the time
        // WaitForShutdownAsync returns; now tear down the engine in render →
        // mix → capture order so the last ~100 ms of audio drains cleanly.
        // ShutdownAsync waits for a rebuild still in flight and refuses new
        // ones, so no engine is left running behind our back.
        logger.LogInformation("Shutdown: stopping audio engine.");
        Task.Run(() => engineHost.ShutdownAsync(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
        CrashLog.Write("INFO", "Host stopped cleanly.", null);
        return 0;
    }

    /// <summary>
    /// Apply the user's most recently used preset (if any) on top of the
    /// freshly-started engine. Failures are logged but non-fatal — the user
    /// gets the empty default state, exactly the v1 startup behaviour.
    /// Returns true when a preset was applied.
    /// </summary>
    private static bool TryLoadLastPreset(
        EngineHost          host,
        EngineFactory       factory,
        PresetStore         store,
        CurrentPresetState  currentPreset,
        IEndpointSource     endpoints,
        ProcessSuppressions suppressions,
        ILogger             logger)
    {
        var name = store.GetLastPresetName();
        if (string.IsNullOrEmpty(name)) return false;

        try
        {
            var preset = store.Load(name);
            var result = Task.Run(() => PresetHandlers.ApplyPresetAsync(host, factory, store, suppressions, preset, endpoints))
                .GetAwaiter().GetResult();
            currentPreset.Set(name, result.InputSlots, result.OutputSlots);

            logger.LogInformation("Auto-loaded last preset '{Name}'", name);
            if (result.MissingDevices.Count > 0)
            {
                logger.LogWarning(
                    "Preset '{Name}' references {Count} device(s) not currently present.",
                    name, result.MissingDevices.Count);
            }
            return true;
        }
        catch (FileNotFoundException)
        {
            logger.LogInformation("Last preset '{Name}' no longer exists; clearing pointer.", name);
            store.ClearLastPresetName();
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not auto-load last preset '{Name}'", name);
            return false;
        }
    }

}

internal sealed record CliArgs(int? Port, bool NoBrowser, bool SkipDriverCheck)
{
    public static CliArgs Parse(string[] args)
    {
        int? port = null;
        bool noBrowser = false;
        bool skipDriverCheck = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
                    port = p;
                    i++;
                    break;
                case "--no-browser":
                    noBrowser = true;
                    break;
                case "--no-driver-check":
                    skipDriverCheck = true;
                    break;
            }
        }

        return new CliArgs(port, noBrowser, skipDriverCheck);
    }
}
