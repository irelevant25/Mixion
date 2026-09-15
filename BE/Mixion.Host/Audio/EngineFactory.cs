using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Mixion.Host.State;

namespace Mixion.Host.Audio;

/// <summary>An active WASAPI endpoint as seen by a device scan.</summary>
public sealed record EndpointInfo(string Id, string FriendlyName, int SampleRate);

/// <summary>
/// Builds a fresh <see cref="MixEngine"/> instance from the OS's current view of
/// audio devices and audio-producing processes, and opens individual sources
/// for the device watcher to attach to a running engine. Pulled out of
/// <c>Program.cs</c> so the same code path runs at startup (no previous state),
/// on a rebuild (previous state preserved) and for in-place re-attachment.
///
/// Channel-identity discipline:
/// <list type="bullet">
///   <item>Physical device id = WASAPI <c>MMDevice.ID</c> (stable across enumerations).</item>
///   <item>Process loopback id = <c>"process:&lt;name&gt;"</c> (see <see cref="ProcessChannelId"/>) — "chrome" stays the same channel whatever its PID.</item>
///   <item>If a previous channel's id is no longer present after enumeration, it stays in the new state with <c>Available = false</c> and a <c>null</c> backing source. The slot strip in the FE renders red but the user doesn't lose their gain/mute/EQ/route settings, and the device watcher re-attaches it when it comes back.</item>
/// </list>
///
/// Matrix preservation: existing channel positions are kept, missing-but-preserved
/// channels stay where they were, brand-new channels are appended. The routing
/// matrix grows accordingly with <c>false</c> in the new positions, so existing
/// routes are untouched.
/// </summary>
public sealed class EngineFactory
{
    /// <summary>
    /// Capture ring capacity in frames at <paramref name="sampleRate"/>: at least
    /// 160 ms — more than the longest the mix waits for a late source (twice
    /// <see cref="MixEngine.MaxDeliveryGapMs"/>) plus a block or two, so the other
    /// captures keep buffering meanwhile instead of dropping audio. 8192 frames
    /// at 44.1 / 48 kHz.
    /// </summary>
    public static int CaptureRingFrames(int sampleRate) => RingFramesFor(sampleRate, milliseconds: 160);

    /// <summary>Render ring capacity in frames at <paramref name="sampleRate"/>: at least 85 ms (4096 frames at 44.1 / 48 kHz).</summary>
    public static int RenderRingFrames(int sampleRate) => RingFramesFor(sampleRate, milliseconds: 85);

    private static int RingFramesFor(int sampleRate, int milliseconds)
    {
        var frames = (long)Math.Max(sampleRate, 8_000) * milliseconds / 1000;
        var ring   = 1024;
        while (ring < frames) ring <<= 1;
        return ring;
    }

    private readonly ILogger             _logger;
    private readonly AudioSettingsStore? _settingsStore;
    private readonly ProcessSuppressions _suppressions;

    public EngineFactory(
        ILogger              logger,
        AudioSettingsStore?  settingsStore = null,
        ProcessSuppressions? suppressions  = null)
    {
        _logger        = logger;
        _settingsStore = settingsStore;
        _suppressions  = suppressions ?? new ProcessSuppressions();
    }

    private AudioSettings Settings => _settingsStore?.Current ?? AudioSettings.Default;

    /// <summary>
    /// Build a fresh engine.
    /// <para>
    /// <paramref name="autoDiscoverNewProcesses"/>: if <c>true</c> (default),
    /// every audio-producing app not already in <paramref name="previousState"/>
    /// (and not detached by the user this session) is appended as a new channel.
    /// If <c>false</c>, only apps whose channels appear in
    /// <paramref name="previousState"/> are bound — what <c>removeProcessLoopback</c>
    /// wants so the rebuild stays minimal.
    /// </para>
    /// <para>
    /// Physical render/capture devices are ALWAYS auto-discovered — the
    /// flag only gates process loopbacks. Users typically want a freshly
    /// plugged USB mic to show up no matter why we're rebuilding.
    /// </para>
    /// </summary>
    public BuildResult Build(MixerState? previousState = null, bool autoDiscoverNewProcesses = true)
    {
        using var enumerator = new MMDeviceEnumerator();
        var targetRate = ResolveTargetRate(enumerator);
        _logger.LogInformation("Engine target sample rate: {Rate} Hz", targetRate);

        var settings = Settings;
        _logger.LogInformation(
            "Audio settings: capture={CaptureMs} ms, render={RenderMs} ms, preferLowLatency={PreferLow}",
            settings.CaptureBufferMs, settings.RenderLatencyMs, settings.PreferLowLatency);

        var (physicalCaptures, physicalIdToCapture) = OpenAllPhysical<IAudioCaptureSource>(
            enumerator, DataFlow.Capture, targetRate, mm => OpenCapture(mm, targetRate, settings));

        var (physicalRenders, physicalIdToRender) = OpenAllPhysical<IRenderDevice>(
            enumerator, DataFlow.Render, targetRate, mm => OpenRender(mm, targetRate, settings));

        var processCaptures = OpenProcessLoopbacks(targetRate, previousState, autoDiscoverNewProcesses);

        // Walk previousState first, in order, preserving channel positions and
        // user settings (gain, mute, solo, pan, DSP, available flag flips).
        var inputs        = new List<IAudioCaptureSource?>();
        var inputChannels = new List<Channel>();
        var seenInputIds  = new HashSet<string>();
        var boundApps     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (previousState is not null)
        {
            foreach (var oldCh in previousState.Inputs)
            {
                seenInputIds.Add(oldCh.Id);
                if (physicalIdToCapture.TryGetValue(oldCh.Id, out var phys))
                {
                    inputs.Add(phys);
                    inputChannels.Add(oldCh with { Name = phys.FriendlyName, Available = true });
                }
                else if (ProcessChannelId.TryGetProcessName(oldCh.Id, out var app)
                         && processCaptures.TryGetValue(app, out var proc)
                         && boundApps.Add(app))
                {
                    inputs.Add(proc);
                    inputChannels.Add(oldCh with { Name = proc.FriendlyName, Available = true });
                }
                else
                {
                    // Channel was here before, isn't now. Keep the slot
                    // (preserves slot bindings + gain/mute/etc.) but mark
                    // unavailable so the FE renders it red.
                    inputs.Add(null);
                    inputChannels.Add(oldCh with { Available = false });
                }
            }
        }

        // Append physical captures that weren't in previous state.
        foreach (var p in physicalCaptures)
        {
            if (!seenInputIds.Add(p.Id)) continue;
            inputs.Add(p);
            inputChannels.Add(NewChannel(p.Id, p.FriendlyName));
        }

        // Then the apps that weren't bound to an existing channel.
        foreach (var (app, proc) in processCaptures.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (boundApps.Contains(app)) continue;
            if (!seenInputIds.Add(proc.Id))
            {
                MixEngine.DisposeQuietly(proc);
                continue;
            }
            boundApps.Add(app);
            inputs.Add(proc);
            inputChannels.Add(NewChannel(proc.Id, proc.FriendlyName));
        }

        var renders        = new List<IRenderDevice?>();
        var renderChannels = new List<Channel>();
        var seenRenderIds  = new HashSet<string>();

        if (previousState is not null)
        {
            foreach (var oldCh in previousState.Outputs)
            {
                seenRenderIds.Add(oldCh.Id);
                if (physicalIdToRender.TryGetValue(oldCh.Id, out var phys))
                {
                    renders.Add(phys);
                    renderChannels.Add(oldCh with { Name = phys.FriendlyName, Available = true });
                }
                else
                {
                    renders.Add(null);
                    renderChannels.Add(oldCh with { Available = false });
                }
            }
        }

        foreach (var p in physicalRenders)
        {
            if (!seenRenderIds.Add(p.Id)) continue;
            renders.Add(p);
            renderChannels.Add(NewChannel(p.Id, p.FriendlyName));
        }

        if (inputs.All(x => x is null) || renders.All(x => x is null))
        {
            foreach (var c in inputs)  MixEngine.DisposeQuietly(c);
            foreach (var r in renders) MixEngine.DisposeQuietly(r);
            throw new InvalidOperationException(inputs.All(x => x is null)
                ? $"No capture sources opened at {targetRate} Hz. " +
                  "Check Windows Sound settings and ensure at least one input is available at that rate."
                : $"No render devices opened at {targetRate} Hz. " +
                  "Check Windows Sound settings and ensure at least one output is available at that rate.");
        }

        // Matrix: old positions keep their routes, new positions start unrouted.
        var matrix = previousState?.Matrix.Resize(inputs.Count, renders.Count)
                     ?? new RoutingMatrix(inputs.Count, renders.Count);

        var initial = new MixerState(
            inputChannels.ToImmutableArray(),
            renderChannels.ToImmutableArray(),
            matrix);

        var engine = new MixEngine(inputs, renders, initial, _logger);
        try
        {
            engine.Start();
        }
        catch
        {
            // Start stops whatever it managed to start; Dispose releases every
            // device (started or not) so nothing leaks regardless of where
            // Start failed.
            engine.Dispose();
            throw;
        }
        return new BuildResult(engine, initial);
    }

    public sealed record BuildResult(MixEngine Engine, MixerState InitialState);

    // ------------------------------------------------- in-place attachment API

    /// <summary>
    /// Active endpoints in one direction with their shared-mode mix rates.
    /// Endpoints whose format can't be read are skipped.
    /// </summary>
    public IReadOnlyList<EndpointInfo> EnumerateEndpoints(DataFlow flow)
    {
        var result = new List<EndpointInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var mm in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                result.Add(new EndpointInfo(mm.ID, mm.FriendlyName, mm.AudioClient.MixFormat.SampleRate));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping {Flow} endpoint while scanning: format unreadable.", flow);
            }
            finally
            {
                mm.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// Open capture endpoint <paramref name="deviceId"/> with the user's latency
    /// preferences. Returns null (and logs why) when the endpoint isn't active,
    /// runs at another rate, or fails to open. <paramref name="quiet"/> logs
    /// failures at debug level — for retries of a target that already failed.
    /// </summary>
    public IAudioCaptureSource? TryOpenCapture(string deviceId, int engineRate, bool quiet = false)
        => TryOpenEndpoint<IAudioCaptureSource>(deviceId, engineRate, "capture", quiet, mm => OpenCapture(mm, engineRate, Settings, quiet));

    /// <summary>Render counterpart of <see cref="TryOpenCapture"/>, honouring exclusive-mode opt-ins.</summary>
    public IRenderDevice? TryOpenRender(string deviceId, int engineRate, bool quiet = false)
        => TryOpenEndpoint<IRenderDevice>(deviceId, engineRate, "render", quiet, mm => OpenRender(mm, engineRate, Settings, quiet));

    /// <summary>
    /// Open a loopback on the app rooted at <paramref name="rootProcessId"/>.
    /// Returns null (and logs why) when the OS refuses.
    /// </summary>
    public ProcessLoopbackCapture? TryOpenProcessLoopback(string processName, int rootProcessId, int engineRate, bool quiet = false)
    {
        try
        {
            var capture = new ProcessLoopbackCapture(rootProcessId, processName, engineRate, CaptureRingFrames(engineRate));
            _logger.LogInformation("Opened process loopback for '{Name}' (PID {Pid}).", processName, rootProcessId);
            return capture;
        }
        catch (Exception ex)
        {
            _logger.Log(quiet ? LogLevel.Debug : LogLevel.Warning, ex,
                "Could not open process loopback for '{Name}' (PID {Pid}).", processName, rootProcessId);
            return null;
        }
    }

    private TDevice? TryOpenEndpoint<TDevice>(
        string deviceId, int engineRate, string kind, bool quiet, Func<MMDevice, TDevice> open)
        where TDevice : class
    {
        MMDevice? mm = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            mm = enumerator.GetDevice(deviceId);
            if (mm.State != DeviceState.Active)
            {
                mm.Dispose();
                return null;
            }

            var name = mm.FriendlyName;
            var rate = mm.AudioClient.MixFormat.SampleRate;
            if (rate != engineRate)
            {
                _logger.Log(quiet ? LogLevel.Debug : LogLevel.Information,
                    "Not attaching {Kind} '{Name}': sample rate {Rate} Hz ≠ engine rate {Target} Hz.",
                    kind, name, rate, engineRate);
                mm.Dispose();
                return null;
            }

            var device = open(mm);
            ReleaseIfNotOwned(mm, device);
            _logger.LogInformation("Opened {Kind} '{Name}'.", kind, name);
            return device;
        }
        catch (Exception ex)
        {
            _logger.Log(quiet ? LogLevel.Debug : LogLevel.Warning, ex, "Could not open {Kind} endpoint {Id}.", kind, deviceId);
            mm?.Dispose();
            return null;
        }
    }

    // ------------------------------------------------------------ open helpers

    private static Channel NewChannel(string id, string name)
        => new(id, name, GainDb: 0f, Muted: false, Soloed: false);

    /// <summary>
    /// The NAudio-backed devices take ownership of the <see cref="MMDevice"/>
    /// they're given; the IAudioClient3 devices only read its id, name and
    /// format and activate their own COM objects, so the wrapper is released here.
    /// </summary>
    private static void ReleaseIfNotOwned(MMDevice mm, object device)
    {
        if (device is LowLatencyCaptureDevice or LowLatencyRenderDevice) mm.Dispose();
    }

    /// <summary>
    /// Try low-latency (IAudioClient3) first when the user opts in; fall back
    /// to the regular NAudio-backed device on any failure (older OS, driver
    /// doesn't implement the min-period contract, virtual cable that can't
    /// honour InitializeSharedAudioStream).
    /// </summary>
    private IAudioCaptureSource OpenCapture(MMDevice device, int sampleRate, AudioSettings settings, bool quiet = false)
    {
        var ringFrames = CaptureRingFrames(sampleRate);
        return settings.PreferLowLatency
            ? OpenLowLatencyOrFallback<IAudioCaptureSource>(
                () => new LowLatencyCaptureDevice(device, ringFrames),
                () => new CaptureDevice(device, ringFrames, settings.CaptureBufferMs),
                $"capture '{device.FriendlyName}'",
                quiet)
            : new CaptureDevice(device, ringFrames, settings.CaptureBufferMs);
    }

    /// <summary>
    /// Pick the right <see cref="IRenderDevice"/> implementation for
    /// <paramref name="device"/> based on the user's per-device
    /// preferences. Order:
    /// <list type="number">
    ///   <item>If the user has opted this device into WASAPI exclusive
    ///         mode, try <see cref="ExclusiveRenderDevice"/> first. On
    ///         failure (virtual cables, picky drivers, format rejection)
    ///         fall through to the shared-mode paths so the user still
    ///         hears audio.</item>
    ///   <item>Otherwise, if <see cref="AudioSettings.PreferLowLatency"/>
    ///         is set, try <see cref="LowLatencyRenderDevice"/> via
    ///         <c>IAudioClient3</c>; fall back to <see cref="RenderDevice"/>
    ///         on any exception.</item>
    ///   <item>Otherwise, use the regular <see cref="RenderDevice"/>.</item>
    /// </list>
    /// </summary>
    private IRenderDevice OpenRender(MMDevice device, int sampleRate, AudioSettings settings, bool quiet = false)
    {
        var ringFrames = RenderRingFrames(sampleRate);
        string? exclusiveFallbackReason = null;

        if (settings.IsExclusiveRender(device.ID))
        {
            var latencyMs = settings.GetExclusiveRenderLatencyMs(device.ID);
            try
            {
                var d = new ExclusiveRenderDevice(device, ringFrames, latencyMs);
                _logger.LogInformation(
                    "Opened render '{Name}' in WASAPI exclusive mode at {Ms} ms.",
                    device.FriendlyName, latencyMs);
                return d;
            }
            catch (Exception ex)
            {
                // Capture the underlying message so the FE can show *why*
                // exclusive failed (format mismatch, device busy, alignment,
                // etc.) instead of a generic "fell back" warning. The
                // fallback shared-mode device gets the reason stamped on
                // it below before we return.
                exclusiveFallbackReason = SummariseExclusiveError(ex);
                _logger.Log(quiet ? LogLevel.Debug : LogLevel.Warning,
                    "Exclusive mode unavailable for render '{Name}' ({Reason}); falling back to shared mode.",
                    device.FriendlyName, ex.Message);
            }
        }

        var fallback = settings.PreferLowLatency
            ? OpenLowLatencyOrFallback<IRenderDevice>(
                () => new LowLatencyRenderDevice(device, ringFrames),
                () => new RenderDevice(device, ringFrames, settings.RenderLatencyMs),
                $"render '{device.FriendlyName}'",
                quiet)
            : new RenderDevice(device, ringFrames, settings.RenderLatencyMs);

        if (exclusiveFallbackReason is not null)
            fallback.ExclusiveFallbackReason = exclusiveFallbackReason;

        return fallback;
    }

    /// <summary>
    /// Trim the noisy WASAPI exception text to something meaningful for
    /// the FE. NAudio wraps HRESULTs in <see cref="COMException"/>; the
    /// raw message is usually a hex code that nobody reads. This helper
    /// keeps known-meaningful prefixes verbatim and falls back to the
    /// outer message otherwise.
    /// </summary>
    private static string SummariseExclusiveError(Exception ex)
    {
        // Walk the inner-exception chain — the most useful message is
        // typically on the deepest wrapped exception, where NAudio left
        // the COM HRESULT detail.
        var cur = ex;
        while (cur is not null)
        {
            if (!string.IsNullOrWhiteSpace(cur.Message)) return cur.Message;
            cur = cur.InnerException;
        }
        return "exclusive-mode open failed";
    }

    /// <summary>
    /// Open a device through <paramref name="lowLatency"/> first; on any
    /// exception fall back to <paramref name="legacy"/>. Lets the engine
    /// pick up the IAudioClient3 path on machines that support it (Win10
    /// 1803+, modern audio drivers) without breaking on virtual cables or
    /// older drivers that reject <c>InitializeSharedAudioStream</c>.
    /// </summary>
    private TDevice OpenLowLatencyOrFallback<TDevice>(
        Func<TDevice> lowLatency,
        Func<TDevice> legacy,
        string label,
        bool quiet = false)
    {
        var level = quiet ? LogLevel.Debug : LogLevel.Information;
        try
        {
            var d = lowLatency();
            _logger.Log(level, "Opened {Label} via IAudioClient3 (low-latency).", label);
            return d;
        }
        catch (Exception ex)
        {
            _logger.Log(level,
                "IAudioClient3 path unavailable for {Label} ({Reason}); using regular shared-mode WASAPI.",
                label, ex.Message);
            return legacy();
        }
    }

    private (List<TDevice> ordered, Dictionary<string, TDevice> byId) OpenAllPhysical<TDevice>(
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        int targetRate,
        Func<MMDevice, TDevice> open)
        where TDevice : class, IDisposable
    {
        var ordered = new List<TDevice>();
        var byId    = new Dictionary<string, TDevice>();

        foreach (var mm in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            var name = mm.FriendlyName;
            int rate;
            try
            {
                rate = mm.AudioClient.MixFormat.SampleRate;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read mix format for {Flow} '{Name}'; skipping.", flow, name);
                mm.Dispose();
                continue;
            }

            if (rate != targetRate)
            {
                _logger.LogInformation(
                    "Skipping {Flow} '{Name}': sample rate {Rate} Hz ≠ engine rate {Target} Hz.",
                    flow, name, rate, targetRate);
                mm.Dispose();
                continue;
            }

            try
            {
                var id     = mm.ID;
                var device = open(mm);
                ReleaseIfNotOwned(mm, device);
                ordered.Add(device);
                byId[id] = device;
                _logger.LogInformation("Opened {Flow} '{Name}'.", flow, name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open {Flow} '{Name}'; skipping.", flow, name);
                mm.Dispose();
            }
        }

        return (ordered, byId);
    }

    /// <summary>
    /// Open one loopback per app that belongs in the engine, keyed by process
    /// name: apps already in <paramref name="previousState"/> — found through
    /// their audio session or, when they haven't opened one yet, as a running
    /// process — plus, when <paramref name="autoDiscover"/> is set, every other
    /// app with an audio session that the user hasn't detached.
    /// </summary>
    private Dictionary<string, ProcessLoopbackCapture> OpenProcessLoopbacks(
        int sampleRate,
        MixerState? previousState,
        bool autoDiscover)
    {
        var result = new Dictionary<string, ProcessLoopbackCapture>(StringComparer.OrdinalIgnoreCase);

        ProcessSnapshot snapshot;
        IReadOnlyList<AudioProcess> audio;
        try
        {
            snapshot = ProcessSnapshot.Capture();
            audio    = new AudioProcessEnumerator().Enumerate(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate audio processes; per-process loopback inputs disabled.");
            return result;
        }

        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (previousState is not null)
        {
            foreach (var ch in previousState.Inputs)
            {
                if (!ProcessChannelId.TryGetProcessName(ch.Id, out var app) || targets.ContainsKey(app)) continue;

                var playing = audio.FirstOrDefault(p => ProcessSnapshot.NameEquals(p.ProcessName, app));
                if (playing is not null)
                {
                    targets[app] = playing.RootProcessId;
                }
                else if (snapshot.FindCapturableAppRoots(app) is { Count: > 0 } roots)
                {
                    targets[app] = roots[0];
                }
            }
        }

        if (autoDiscover)
        {
            foreach (var p in audio)
            {
                if (targets.ContainsKey(p.ProcessName) || _suppressions.Contains(p.ProcessName)) continue;
                targets[p.ProcessName] = p.RootProcessId;
            }
        }

        foreach (var (app, rootPid) in targets)
        {
            var capture = TryOpenProcessLoopback(app, rootPid, sampleRate);
            if (capture is not null) result[app] = capture;
        }

        return result;
    }

    private int ResolveTargetRate(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return defaultRender.AudioClient.MixFormat.SampleRate;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query default render rate; assuming 48000 Hz.");
            return 48_000;
        }
    }
}
