using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.Interop;
using VoicemeterAlt.Host.Ipc;
using VoicemeterAlt.Host.Ipc.Handlers;
using VoicemeterAlt.Host.Shell;
using VoicemeterAlt.Host.State;
using VoicemeterAlt.Host.Web;

namespace VoicemeterAlt.Host;

internal static class Program
{
    private const int ExitDriverMissing = 2;

    public static async Task<int> Main(string[] args)
    {
        var cli = CliArgs.Parse(args);

        // 1. Driver presence check, BEFORE any HTTP/WS/audio work. If
        //    VB-CABLE is missing the user gets a native dialog and we
        //    exit cleanly — no port bound, no console window left over.
        if (!cli.SkipDriverCheck)
        {
            var probe = DriverProbe.Probe(new DeviceEnumerator());
            if (!probe.Found)
            {
                MessageBox.ShowError(
                    caption: "VoicemeterAlt — VB-CABLE not found",
                    text:
                        "VB-CABLE driver not detected.\n\n" +
                        "VoicemeterAlt requires VB-CABLE to provide virtual audio endpoints.\n" +
                        "Install it from https://vb-audio.com/Cable/ and relaunch the app.");
                return ExitDriverMissing;
            }
        }

        // 2. Build the web host. Same process serves /api/*, /ws, and the
        //    embedded Angular bundle at /. Bound to 127.0.0.1 only.
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(k =>
            HttpServer.ConfigureKestrel(k, new HostBindingOptions(cli.Port)));

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<EngineHost>();
        builder.Services.AddSingleton<TelemetryHub>();
        builder.Services.AddHostedService<TelemetryBroadcaster>();
        builder.Services.AddHostedService<SpectrumBroadcaster>();
        builder.Services.AddSingleton<IEndpointSource, DeviceEnumerator>();
        builder.Services.AddSingleton(provider => new EngineFactory(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("MixEngine")));
        builder.Services.AddSingleton<CurrentPresetState>();
        builder.Services.AddSingleton(provider => new PresetStore(
            PresetStore.DefaultDirectory(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("PresetStore")));
        builder.Services.AddSingleton(provider =>
        {
            var dispatcher = new JsonRpcDispatcher(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("JsonRpc"));

            DeviceHandlers   .Register(
                dispatcher,
                provider.GetRequiredService<IEndpointSource>(),
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<EngineFactory>());
            StateHandlers    .Register(dispatcher, provider.GetRequiredService<EngineHost>());
            RoutingHandlers  .Register(dispatcher, provider.GetRequiredService<EngineHost>());
            ChannelHandlers  .Register(dispatcher, provider.GetRequiredService<EngineHost>());
            DspHandlers      .Register(dispatcher, provider.GetRequiredService<EngineHost>());
            TelemetryHandlers.Register(
                dispatcher,
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<TelemetryHub>());
            PresetHandlers   .Register(
                dispatcher,
                provider.GetRequiredService<EngineHost>(),
                provider.GetRequiredService<PresetStore>(),
                provider.GetRequiredService<CurrentPresetState>(),
                provider.GetRequiredService<IEndpointSource>());
            return dispatcher;
        });

        var app = builder.Build();

        app.UseWebSockets();

        app.MapHealth();
        app.MapSession();
        app.MapControlSocket();

        app.UseEmbeddedSpa();

        // 3. Start, then read back the bound URL so we can log + open it.
        await app.StartAsync();

        var url = HttpServer.ResolveBoundUrl(app.Services.GetRequiredService<IServer>());
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Host");
        logger.LogInformation("VoicemeterAlt host listening at {Url}", url);
        Console.WriteLine();
        Console.WriteLine($"  VoicemeterAlt → {url}");
        Console.WriteLine();

        // 4. Start the audio engine. Default mic → default output as a
        //    walking-skeleton passthrough (BE-011). Failures here don't take
        //    the host down — the UI can still talk to /api/*, the user just
        //    won't hear audio. Useful when running on a CI box without an
        //    audio device.
        MixEngine? engine = null;
        var engineHost = app.Services.GetRequiredService<EngineHost>();
        try
        {
            engine = StartFullDeviceEngine(
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MixEngine"));
            engineHost.Set(engine);
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
        // of "current setup"). If audio failed to come up there is no engine
        // to apply state to — the FE will see no current preset and start
        // from defaults.
        if (engine is not null)
        {
            TryLoadLastPreset(
                engine,
                app.Services.GetRequiredService<PresetStore>(),
                app.Services.GetRequiredService<CurrentPresetState>(),
                app.Services.GetRequiredService<IEndpointSource>(),
                logger);
        }

        if (!cli.NoBrowser)
            BrowserLauncher.Open(url);

        await app.WaitForShutdownAsync();

        engineHost.Set(null);
        engine?.Dispose();
        return 0;
    }

    /// <summary>
    /// Apply the user's most recently used preset (if any) on top of the
    /// freshly-started engine. Failures are logged but non-fatal — the user
    /// gets the empty default state, exactly the v1 startup behaviour.
    /// </summary>
    private static void TryLoadLastPreset(
        MixEngine engine,
        PresetStore store,
        CurrentPresetState currentPreset,
        IEndpointSource endpoints,
        ILogger logger)
    {
        var name = store.GetLastPresetName();
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            var preset = store.Load(name);
            var result = store.Apply(preset, engine.SnapshotState(), endpoints);
            engine.PublishState(result.NextState);
            currentPreset.Set(name, result.InputSlots, result.OutputSlots);

            logger.LogInformation("Auto-loaded last preset '{Name}'", name);
            if (result.MissingDevices.Count > 0)
            {
                logger.LogWarning(
                    "Preset '{Name}' references {Count} device(s) not currently present.",
                    name, result.MissingDevices.Count);
            }
        }
        catch (FileNotFoundException)
        {
            logger.LogInformation("Last preset '{Name}' no longer exists; clearing pointer.", name);
            store.ClearLastPresetName();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not auto-load last preset '{Name}'", name);
        }
    }

    /// <summary>
    /// Open every active capture and render endpoint Windows reports — one
    /// strip per device, just like Sound settings — plus a per-process
    /// loopback for every audio-producing process, and start the mix engine
    /// with all routes off. Delegates to <see cref="EngineFactory"/> so the
    /// runtime <c>refreshDevices</c> RPC can rebuild the engine via the same
    /// path with previous state preserved.
    /// </summary>
    private static MixEngine StartFullDeviceEngine(ILogger logger)
    {
        return new EngineFactory(logger).Build(previousState: null).Engine;
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
