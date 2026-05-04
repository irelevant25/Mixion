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

    private readonly ILogger _logger;

    public EngineFactory(ILogger logger) { _logger = logger; }

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

        var (physicalCaptures, physicalIdToCapture) = OpenAllPhysical(
            enumerator, DataFlow.Capture, targetRate,
            (mm, ring) => new CaptureDevice(mm, ring));

        var (physicalRenders, physicalIdToRender) = OpenAllPhysical(
            enumerator, DataFlow.Render, targetRate,
            (mm, ring) => new RenderDevice(mm, ring));

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

        var renders        = new List<RenderDevice?>();
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
        engine.Start();
        return new BuildResult(engine, initial);
    }

    public sealed record BuildResult(MixEngine Engine, MixerState InitialState);

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

    private List<LoopbackCapture?> OpenLoopbacks(MMDeviceEnumerator enumerator, List<RenderDevice?> renders)
    {
        var result = new List<LoopbackCapture?>(renders.Count);
        foreach (var r in renders)
        {
            if (r is null) { result.Add(null); continue; }
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
