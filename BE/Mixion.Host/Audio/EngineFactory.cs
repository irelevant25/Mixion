using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Mixion.Host.State;

namespace Mixion.Host.Audio;

/// <summary>
/// Builds a fresh <see cref="MixEngine"/> instance from the OS's current view of
/// audio devices and audio-producing processes. Pulled out of <c>Program.cs</c>
/// so the same code path can run both at startup (no previous state) and on a
/// runtime refresh request (previous state preserved across the rebuild).
///
/// Channel-identity discipline:
/// <list type="bullet">
///   <item>Physical device id = WASAPI <c>MMDevice.ID</c> (stable across enumerations).</item>
///   <item>Process loopback id = <c>"process:&lt;name&gt;"</c> (stable across PID changes — Chrome restart keeps the same id).</item>
///   <item>If a previous channel's id is no longer present after enumeration, it stays in the new state with <c>Available = false</c> and a <c>null</c> backing source. The slot strip in the FE renders red but the user doesn't lose their gain/mute/EQ/route settings.</item>
/// </list>
///
/// Matrix preservation: existing channel positions are kept, missing-but-preserved
/// channels stay where they were, brand-new channels are appended. The routing
/// matrix grows accordingly with <c>false</c> in the new positions, so existing
/// routes are untouched.
/// </summary>
public sealed class EngineFactory
{
    /// <summary>4 k mono samples ≈ 85 ms @ 48 kHz. Same value Program.cs used inline.</summary>
    private const int RingSamples = 1 << 12;

    private readonly ILogger             _logger;
    private readonly AudioSettingsStore? _settingsStore;

    public EngineFactory(ILogger logger, AudioSettingsStore? settingsStore = null)
    {
        _logger        = logger;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// Build a fresh engine.
    /// <para>
    /// <paramref name="autoDiscoverNewProcesses"/>: if <c>true</c> (default),
    /// any audio-producing process not already in <paramref name="previousState"/>
    /// is appended as a new channel — that's what lets the user click Refresh and
    /// see a newly-launched VLC. If <c>false</c>, only processes whose ids appear
    /// in <paramref name="previousState"/> are bound; everything else is opened
    /// then immediately discarded. Pass <c>false</c> from <c>removeProcessLoopback</c>
    /// so the channel the user just dropped doesn't immediately re-appear.
    /// </para>
    /// <para>
    /// Physical render/capture devices are ALWAYS auto-discovered — the
    /// flag only gates process loopbacks. Users typically want a freshly
    /// plugged USB mic to show up no matter why we're rebuilding.
    /// </para>
    /// </summary>
    public BuildResult Build(MixerState? previousState = null, bool autoDiscoverNewProcesses = true)
    {
        var enumerator = new MMDeviceEnumerator();
        var targetRate = ResolveTargetRate(enumerator);
        _logger.LogInformation("Engine target sample rate: {Rate} Hz", targetRate);

        var settings = _settingsStore?.Current ?? AudioSettings.Default;
        _logger.LogInformation(
            "Audio settings: capture={CaptureMs} ms, render={RenderMs} ms, preferLowLatency={PreferLow}",
            settings.CaptureBufferMs, settings.RenderLatencyMs, settings.PreferLowLatency);

        // Try low-latency (IAudioClient3) first when the user opts in; fall
        // back to the regular NAudio-backed device on any failure (older OS,
        // driver doesn't implement the min-period contract, virtual cable
        // that can't honour InitializeSharedAudioStream). The fallback log
        // line is INFO-level so a busy box with mixed device support still
        // leaves a clear breadcrumb of who got what.
        var (physicalCaptures, physicalIdToCapture) = OpenAllPhysical<IAudioCaptureSource>(
            enumerator, DataFlow.Capture, targetRate,
            (mm, ring) => settings.PreferLowLatency
                ? OpenLowLatencyOrFallback<IAudioCaptureSource>(
                    () => new LowLatencyCaptureDevice(mm, ring),
                    () => new CaptureDevice(mm, ring, settings.CaptureBufferMs),
                    $"capture '{mm.FriendlyName}'")
                : new CaptureDevice(mm, ring, settings.CaptureBufferMs));

        var (physicalRenders, physicalIdToRender) = OpenAllPhysical<IRenderDevice>(
            enumerator, DataFlow.Render, targetRate,
            (mm, ring) => OpenRender(mm, ring, settings));

        var processCaptures = OpenProcessLoopbacks(targetRate);
        var processIdToCapture = processCaptures.ToDictionary(c => c.Id);

        // Walk previousState first, in order, preserving channel positions and
        // user settings (gain, mute, solo, pan, DSP, available flag flips).
        var inputs       = new List<IAudioCaptureSource?>();
        var inputChannels = new List<Channel>();
        var seenInputIds = new HashSet<string>();

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
                else if (processIdToCapture.TryGetValue(oldCh.Id, out var proc))
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
            if (seenInputIds.Add(p.Id))
            {
                inputs.Add(p);
                inputChannels.Add(new Channel(p.Id, p.FriendlyName, GainDb: 0f, Muted: false, Soloed: false));
            }
        }

        // Then process loopbacks not in previous state. Skipped when the
        // caller explicitly asked for "no auto-discovery" — typically a
        // remove operation that doesn't want the just-removed channel
        // sneaking back in via enumeration.
        if (autoDiscoverNewProcesses)
        {
            foreach (var p in processCaptures)
            {
                if (seenInputIds.Add(p.Id))
                {
                    inputs.Add(p);
                    inputChannels.Add(new Channel(p.Id, p.FriendlyName, GainDb: 0f, Muted: false, Soloed: false));
                }
            }
        }
        else
        {
            // Dispose orphan PLCs so we don't leak WASAPI handles.
            foreach (var p in processCaptures)
            {
                if (!seenInputIds.Contains(p.Id))
                    p.Dispose();
            }
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
            if (seenRenderIds.Add(p.Id))
            {
                renders.Add(p);
                renderChannels.Add(new Channel(p.Id, p.FriendlyName, GainDb: 0f, Muted: false, Soloed: false));
            }
        }

        if (inputs.Count == 0 || inputs.All(x => x is null))
            throw new InvalidOperationException(
                $"No capture sources opened at {targetRate} Hz. " +
                "Check Windows Sound settings and ensure at least one input is available at that rate.");
        if (renders.Count == 0 || renders.All(x => x is null))
            throw new InvalidOperationException(
                $"No render devices opened at {targetRate} Hz. " +
                "Check Windows Sound settings and ensure at least one output is available at that rate.");

        // Matrix: copy old where positions still valid, pad new positions with false.
        var matrix = new RoutingMatrix(inputs.Count, renders.Count);
        if (previousState is not null)
        {
            var copyInRows = Math.Min(previousState.Matrix.Inputs,  inputs.Count);
            var copyOutCols = Math.Min(previousState.Matrix.Outputs, renders.Count);
            for (var i = 0; i < copyInRows; i++)
                for (var o = 0; o < copyOutCols; o++)
                    if (previousState.Matrix[i, o])
                        matrix = matrix.With(i, o, true);
        }

        var initial = new MixerState(
            inputChannels.ToImmutableArray(),
            renderChannels.ToImmutableArray(),
            matrix);

        var loopbacks = OpenLoopbacks(enumerator, renders);

        var engine = new MixEngine(inputs, renders, initial, _logger, loopbacks);
        try
        {
            engine.Start();
        }
        catch
        {
            // Belt-and-braces: MixEngine.Start now rolls back its own
            // started devices, but Dispose also tears down the un-started
            // ones (e.g. the COM RCWs allocated during construction) so
            // nothing leaks regardless of where Start failed.
            engine.Dispose();
            throw;
        }
        return new BuildResult(engine, initial);
    }

    public sealed record BuildResult(MixEngine Engine, MixerState InitialState);

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
    private IRenderDevice OpenRender(MMDevice device, int ringCapacityFrames, AudioSettings settings)
    {
        string? exclusiveFallbackReason = null;

        if (settings.IsExclusiveRender(device.ID))
        {
            var latencyMs = settings.GetExclusiveRenderLatencyMs(device.ID);
            try
            {
                var d = new ExclusiveRenderDevice(device, ringCapacityFrames, latencyMs);
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
                _logger.LogWarning(
                    "Exclusive mode unavailable for render '{Name}' ({Reason}); falling back to shared mode.",
                    device.FriendlyName, ex.Message);
            }
        }

        var fallback = settings.PreferLowLatency
            ? OpenLowLatencyOrFallback<IRenderDevice>(
                () => new LowLatencyRenderDevice(device, ringCapacityFrames),
                () => new RenderDevice(device, ringCapacityFrames, settings.RenderLatencyMs),
                $"render '{device.FriendlyName}'")
            : new RenderDevice(device, ringCapacityFrames, settings.RenderLatencyMs);

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
        string label)
    {
        try
        {
            var d = lowLatency();
            _logger.LogInformation("Opened {Label} via IAudioClient3 (low-latency).", label);
            return d;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "IAudioClient3 path unavailable for {Label} ({Reason}); using regular shared-mode WASAPI.",
                label, ex.Message);
            return legacy();
        }
    }

    private (List<TDevice> ordered, Dictionary<string, TDevice> byId) OpenAllPhysical<TDevice>(
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        int targetRate,
        Func<MMDevice, int, TDevice> open)
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
                var device = open(mm, RingSamples);
                ordered.Add(device);
                byId[mm.ID] = device;
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

    private List<ProcessLoopbackCapture> OpenProcessLoopbacks(int sampleRate)
    {
        var result = new List<ProcessLoopbackCapture>();

        IReadOnlyList<AudioProcess> processes;
        try
        {
            processes = new AudioProcessEnumerator().Enumerate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate audio processes; per-process loopback inputs disabled.");
            return result;
        }

        // Dedupe by stable id ("process:<name>") in case multiple PIDs of the
        // same process are running — we want one loopback per app, not per PID.
        var seenIds = new HashSet<string>();
        foreach (var p in processes)
        {
            try
            {
                var cap = new ProcessLoopbackCapture(p.ProcessId, p.ProcessName, sampleRate, RingSamples);
                if (!seenIds.Add(cap.Id))
                {
                    cap.Dispose();
                    continue;
                }
                result.Add(cap);
                _logger.LogInformation("Opened process loopback for '{Name}' (PID {Pid}).", p.ProcessName, p.ProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open process loopback for '{Name}' (PID {Pid}); skipping.",
                    p.ProcessName, p.ProcessId);
            }
        }

        return result;
    }

    private List<LoopbackCapture?> OpenLoopbacks(MMDeviceEnumerator enumerator, List<IRenderDevice?> renders)
    {
        var result = new List<LoopbackCapture?>(renders.Count);
        foreach (var r in renders)
        {
            if (r is null) { result.Add(null); continue; }

            // WASAPI loopback capture relies on the device's shared-mode
            // mixer to tap. A device opened in exclusive mode bypasses
            // that mixer entirely, so trying to attach a loopback returns
            // AUDCLNT_E_DEVICE_IN_USE — and that exception bubbled up out
            // of MixEngine.Start, killing the entire engine. Skip cleanly:
            // the user just doesn't get a peak meter on this output, which
            // is the documented trade-off for exclusive mode.
            if (r.Mode == RenderMode.Exclusive)
            {
                _logger.LogInformation(
                    "Skipping loopback meter on '{Name}' — device is opened in exclusive mode.",
                    r.FriendlyName);
                result.Add(null);
                continue;
            }

            try
            {
                var mm = enumerator.GetDevice(r.Id);
                result.Add(new LoopbackCapture(mm, RingSamples));
                _logger.LogInformation("Opened loopback meter on '{Name}'.", r.FriendlyName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not open loopback on '{Name}'; output meter will reflect our send only.",
                    r.FriendlyName);
                result.Add(null);
            }
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
