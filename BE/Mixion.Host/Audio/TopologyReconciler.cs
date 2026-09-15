using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Mixion.Host.Ipc;
using Mixion.Host.State;

namespace Mixion.Host.Audio;

/// <summary>
/// Keeps the running engine's channels bound to whatever Windows currently
/// offers — "Chrome is always chrome". Each pass takes an OS snapshot (running
/// processes, apps with audio sessions and, when devices changed, the active
/// endpoints), asks <see cref="TopologyPlanner"/> what should change and applies
/// it in place through <see cref="EngineHost"/>: a dead or faulted source is
/// swapped for a fresh one, a missing one is detached, new devices and apps are
/// attached. Nothing else is re-opened, so unaffected channels never drop out.
/// Only when the engine runs out of spare slots does a pass fall back to a
/// full rebuild. If a rebuild ever fails and leaves no engine, passes retry it
/// from the state it had — right after a device change, otherwise backing off.
/// </summary>
public sealed class TopologyReconciler
{
    /// <summary>Wait before retrying a target that failed to open; doubles with every further failure.</summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait between attempts, so a device another app held exclusively is still picked up eventually.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A source that stays healthy this long after being (re)opened ends its
    /// failure streak; one that faults sooner counts as another failed open.
    /// </summary>
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

    private readonly EngineHost             _host;
    private readonly EngineFactory          _factory;
    private readonly ProcessSuppressions    _suppressions;
    private readonly AudioProcessEnumerator _audioProcesses = new();
    private readonly ILogger                _logger;

    /// <summary>Targets that failed to open: when last, and how many times in a row.</summary>
    private readonly Dictionary<SourceTarget, OpenFailure> _failedOpens = new();

    /// <summary>When a pass last opened each target. Guarded by the <see cref="_failedOpens"/> lock.</summary>
    private readonly Dictionary<SourceTarget, long> _openedAt = new();

    /// <summary>Failed attempts to rebuild a missing engine. Only touched by passes, which never overlap.</summary>
    private OpenFailure? _recoveryFailure;

    public TopologyReconciler(
        EngineHost          host,
        EngineFactory       factory,
        ProcessSuppressions suppressions,
        ILogger             logger)
    {
        _host         = host;
        _factory      = factory;
        _suppressions = suppressions;
        _logger       = logger;
    }

    /// <summary>
    /// Run one pass. <paramref name="scanDevices"/> includes the endpoint scan
    /// (a notification arrived or the periodic safety scan is due);
    /// <paramref name="devicesChanged"/> additionally forgets earlier device
    /// open failures, since the device may be usable now.
    /// </summary>
    public async Task ReconcileAsync(bool scanDevices, bool devicesChanged, CancellationToken ct)
    {
        if (_host.Current is null)
        {
            // A failed rebuild left no engine: retry it from the state it had, so
            // settings, routes and placeholder channels survive. A device change
            // may be what fixes it; otherwise back off like a device that won't open.
            if (devicesChanged || _recoveryFailure is not { } failure || failure.RetryDue(Environment.TickCount64))
                await TryRecoverAsync(ct).ConfigureAwait(false);
            return;
        }

        await _host.ChangeTopologyAsync(
            _factory,
            engine => Reconcile(engine, scanDevices, devicesChanged),
            ct).ConfigureAwait(false);
    }

    private async Task TryRecoverAsync(CancellationToken ct)
    {
        try
        {
            if (await _host.TryRecoverAsync(_factory, ct).ConfigureAwait(false))
                _logger.LogInformation("Audio engine rebuilt after an earlier failure.");
            _recoveryFailure = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var count = (_recoveryFailure?.Count ?? 0) + 1;
            _recoveryFailure = new OpenFailure(Environment.TickCount64, count);
            _logger.Log(count == 1 ? LogLevel.Warning : LogLevel.Debug, ex,
                "Audio engine still can't be rebuilt; retrying later.");
        }
    }

    private TopologyOutcome Reconcile(MixEngine engine, bool scanDevices, bool devicesChanged)
    {
        var processes = ProcessSnapshot.Capture();
        var captures  = engine.Captures;
        var renders   = engine.Renders;

        var anyFaultedDevice = false;
        var healthyTargets   = new List<SourceTarget>();
        var faultedTargets   = new List<SourceTarget>();

        var inputBindings = new SlotBinding[captures.Count];
        for (var i = 0; i < captures.Count; i++)
        {
            var source = captures[i];
            inputBindings[i] = DescribeCapture(source, processes);
            if (source is null) continue;

            if (source.IsFaulted)
            {
                faultedTargets.Add(TargetOf(source));
                if (source is not ProcessLoopbackCapture) anyFaultedDevice = true;
            }
            else if (inputBindings[i].Healthy)
            {
                healthyTargets.Add(TargetOf(source));
            }
        }

        var outputBindings = new SlotBinding[renders.Count];
        for (var o = 0; o < renders.Count; o++)
        {
            var device = renders[o];
            outputBindings[o] = new SlotBinding(device is not null, device is { IsFaulted: false });
            if (device is null) continue;

            if (device.IsFaulted)
            {
                faultedTargets.Add(new SourceTarget.Endpoint(device.Id));
                anyFaultedDevice = true;
            }
            else
            {
                healthyTargets.Add(new SourceTarget.Endpoint(device.Id));
            }
        }

        IReadOnlyList<AudioProcess> audio;
        try
        {
            audio = _audioProcesses.Enumerate(processes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio session scan failed; treating as no active apps this pass.");
            audio = Array.Empty<AudioProcess>();
        }

        // A faulted device needs the endpoint list to decide between re-open
        // and detach, whatever the caller asked for.
        var scan = scanDevices || anyFaultedDevice;
        var os = new TopologySnapshot(
            scan ? _factory.EnumerateEndpoints(DataFlow.Capture) : null,
            scan ? _factory.EnumerateEndpoints(DataFlow.Render)  : null,
            audio,
            processes);

        UpdateFailureRecords(devicesChanged, healthyTargets, faultedTargets);

        var state = engine.SnapshotState();
        var plan  = TopologyPlanner.Plan(
            state, inputBindings, outputBindings, os, engine.SampleRate,
            _suppressions.Contains, IsBlocked);

        return plan.IsEmpty ? TopologyOutcome.Unchanged : Apply(engine, state, plan);
    }

    private void UpdateFailureRecords(
        bool                        devicesChanged,
        IReadOnlyList<SourceTarget> healthyTargets,
        IReadOnlyList<SourceTarget> faultedTargets)
    {
        lock (_failedOpens)
        {
            var now      = Environment.TickCount64;
            var stableMs = (long)StableAfter.TotalMilliseconds;

            // Healthy and up long enough since a pass opened it (or opened by a
            // rebuild, so nothing to wait for): its failure streak is over.
            foreach (var target in healthyTargets)
            {
                if (!_openedAt.TryGetValue(target, out var openedAt))
                {
                    _failedOpens.Remove(target);
                }
                else if (now - openedAt >= stableMs)
                {
                    _openedAt.Remove(target);
                    _failedOpens.Remove(target);
                }
            }

            // Faulted again soon after a pass opened it: count that as a failed
            // open, so a source that keeps failing backs off instead of being
            // re-opened (and re-announced to the UI) every second.
            foreach (var target in faultedTargets)
            {
                if (_openedAt.Remove(target, out var openedAt) && now - openedAt < stableMs)
                    RecordFailure(target, now);
            }

            // A device notification may mean a busy or broken endpoint works now,
            // and explains a device that just faulted (format change, re-plug) —
            // so after counting faults, give every device a fresh try.
            if (devicesChanged) ForgetFailures(t => t is SourceTarget.Endpoint);

            // Entries far past the longest retry delay describe targets that are gone.
            var staleMs = 3 * (long)MaxRetryDelay.TotalMilliseconds;
            ForgetFailures(t => now - _failedOpens[t].AtMs > staleMs);
            foreach (var target in _openedAt.Where(kv => now - kv.Value > staleMs).Select(kv => kv.Key).ToList())
                _openedAt.Remove(target);
        }
    }

    private static SlotBinding DescribeCapture(IAudioCaptureSource? source, ProcessSnapshot processes)
    {
        switch (source)
        {
            case null:
                return SlotBinding.Empty;

            case ProcessLoopbackCapture app:
                var exited = app.HasTargetExited() ?? !processes.IsRunning(app.TargetProcessId, app.ProcessName);
                return new SlotBinding(true, !exited && !app.IsFaulted, app.TargetProcessId);

            default:
                return new SlotBinding(true, !source.IsFaulted);
        }
    }

    private static SourceTarget TargetOf(IAudioCaptureSource source) => source is ProcessLoopbackCapture app
        ? new SourceTarget.App(app.ProcessName, app.TargetProcessId)
        : new SourceTarget.Endpoint(source.Id);

    private TopologyOutcome Apply(MixEngine engine, MixerState state, TopologyPlan plan)
    {
        var rate          = engine.SampleRate;
        var inputPatches  = new List<ChannelPatch>();
        var outputPatches = new List<ChannelPatch>();

        foreach (var rebind in plan.InputRebinds)
        {
            var channel = state.Inputs[rebind.Index];
            IAudioCaptureSource? source = null;
            if (rebind.Target is not null)
            {
                // A target that won't open leaves the slot as it is; it backs off
                // now, so a later pass re-plans — detaching a dead binding if
                // nothing else can be opened.
                source = Open(rebind.Target, (t, quiet) => OpenCaptureTarget(t, rate, quiet));
                if (source is null) continue;
            }

            if (!TryReplace(() => engine.ReplaceCapture(rebind.Index, source), source, rebind.Target)) continue;

            LogRebind("input", channel, source?.FriendlyName, rebind.Target);
            if (ChangesChannel(channel, source is not null, source?.FriendlyName))
                inputPatches.Add(new ChannelPatch(rebind.Index, channel.Id, source is not null, source?.FriendlyName));
        }

        foreach (var rebind in plan.OutputRebinds)
        {
            var channel = state.Outputs[rebind.Index];
            IRenderDevice? device = null;
            if (rebind.Target is SourceTarget.Endpoint endpoint)
            {
                device = Open(endpoint, (t, quiet) => _factory.TryOpenRender(((SourceTarget.Endpoint)t).DeviceId, rate, quiet));
                if (device is null) continue;
            }

            if (!TryReplace(() => engine.ReplaceRender(rebind.Index, device), device, rebind.Target)) continue;

            LogRebind("output", channel, device?.FriendlyName, rebind.Target);
            if (ChangesChannel(channel, device is not null, device?.FriendlyName))
                outputPatches.Add(new ChannelPatch(rebind.Index, channel.Id, device is not null, device?.FriendlyName));
        }

        var appendedInputs  = new List<Channel>();
        var appendedOutputs = new List<Channel>();
        var outOfSlots      = false;

        foreach (var added in plan.NewInputs)
        {
            var source = Open(added.Target, (t, quiet) => OpenCaptureTarget(t, rate, quiet));
            if (source is null) continue;

            var channel = new Channel(added.ChannelId, source.FriendlyName, GainDb: 0f, Muted: false, Soloed: false);
            if (!TryAppend(() => engine.TryAppendCapture(source, channel), source, added.Target, ref outOfSlots)) continue;

            appendedInputs.Add(channel);
            _logger.LogInformation("Attached new input '{Name}'.", channel.Name);
        }

        foreach (var added in plan.NewOutputs)
        {
            if (outOfSlots) break;
            if (added.Target is not SourceTarget.Endpoint endpoint) continue;

            var device = Open(endpoint, (t, quiet) => _factory.TryOpenRender(((SourceTarget.Endpoint)t).DeviceId, rate, quiet));
            if (device is null) continue;

            var channel = new Channel(added.ChannelId, device.FriendlyName, GainDb: 0f, Muted: false, Soloed: false);
            if (!TryAppend(() => engine.TryAppendRender(device, channel), device, added.Target, ref outOfSlots)) continue;

            appendedOutputs.Add(channel);
            _logger.LogInformation("Attached new output '{Name}'.", channel.Name);
        }

        // Re-opening the same device leaves the visible state untouched — no push.
        if (inputPatches.Count == 0 && outputPatches.Count == 0 && appendedInputs.Count == 0 && appendedOutputs.Count == 0)
            return outOfSlots ? TopologyOutcome.Rebuild(state) : TopologyOutcome.Unchanged;

        var next = engine.UpdateState(s => ApplyPatches(s, inputPatches, outputPatches, appendedInputs, appendedOutputs));

        if (outOfSlots)
        {
            _logger.LogInformation("No spare channel slots left; rebuilding the engine with room to grow.");
            return TopologyOutcome.Rebuild(next);
        }
        return TopologyOutcome.ChangedState;
    }

    private static bool ChangesChannel(Channel channel, bool available, string? name)
        => channel.Available != available || (name is not null && name != channel.Name);

    /// <summary>A channel whose availability / name follows a rebind. <see cref="ExpectedId"/> guards the index.</summary>
    private readonly record struct ChannelPatch(int Index, string ExpectedId, bool Available, string? Name);

    /// <summary>
    /// Fold rebinds and newly attached channels into <paramref name="state"/>.
    /// Only availability, names and the channel list change — the user's gain,
    /// mute, routing and DSP settings are left exactly as they are.
    /// </summary>
    private static MixerState ApplyPatches(
        MixerState                  state,
        IReadOnlyList<ChannelPatch> inputPatches,
        IReadOnlyList<ChannelPatch> outputPatches,
        IReadOnlyList<Channel>      appendedInputs,
        IReadOnlyList<Channel>      appendedOutputs)
    {
        var inputs  = Patch(state.Inputs,  inputPatches,  appendedInputs);
        var outputs = Patch(state.Outputs, outputPatches, appendedOutputs);
        return new MixerState(inputs, outputs, state.Matrix.Resize(inputs.Length, outputs.Length));

        static ImmutableArray<Channel> Patch(
            ImmutableArray<Channel>     bus,
            IReadOnlyList<ChannelPatch> patches,
            IReadOnlyList<Channel>      appended)
        {
            var builder = bus.ToBuilder();
            foreach (var p in patches)
            {
                if (p.Index >= builder.Count || builder[p.Index].Id != p.ExpectedId) continue;
                var ch = builder[p.Index];
                builder[p.Index] = ch with { Available = p.Available, Name = p.Name ?? ch.Name };
            }
            builder.AddRange(appended);
            return builder.ToImmutable();
        }
    }

    private IAudioCaptureSource? OpenCaptureTarget(SourceTarget target, int rate, bool quiet) => target switch
    {
        SourceTarget.Endpoint e => _factory.TryOpenCapture(e.DeviceId, rate, quiet),
        SourceTarget.App a      => _factory.TryOpenProcessLoopback(a.ProcessName, a.RootProcessId, rate, quiet),
        _                       => null,
    };

    /// <summary>
    /// Open a target, recording the outcome for backoff. Only a target's first
    /// failure is logged loudly; retries log at debug level.
    /// </summary>
    private TDevice? Open<TDevice>(SourceTarget target, Func<SourceTarget, bool, TDevice?> open) where TDevice : class
    {
        bool failedBefore;
        lock (_failedOpens) failedBefore = _failedOpens.ContainsKey(target);

        var device = open(target, failedBefore);
        lock (_failedOpens)
        {
            var now = Environment.TickCount64;
            if (device is null)
                RecordFailure(target, now);
            else
                _openedAt[target] = now; // the streak ends once it stays healthy (UpdateFailureRecords)
        }
        return device;
    }

    private bool TryReplace<TDevice>(Func<TDevice?> replace, TDevice? incoming, SourceTarget? target)
        where TDevice : class, IDisposable
    {
        try
        {
            MixEngine.DisposeQuietly(replace());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not attach {Target}.", target);
            MixEngine.DisposeQuietly(incoming);
            if (target is not null) MarkFailed(target);
            return false;
        }
    }

    private bool TryAppend(Func<int> append, IDisposable device, SourceTarget target, ref bool outOfSlots)
    {
        try
        {
            if (append() >= 0) return true;
            outOfSlots = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not attach {Target}.", target);
            MarkFailed(target);
        }
        MixEngine.DisposeQuietly(device);
        return false;
    }

    private void MarkFailed(SourceTarget target)
    {
        lock (_failedOpens) RecordFailure(target, Environment.TickCount64);
    }

    /// <summary>Caller holds the <see cref="_failedOpens"/> lock.</summary>
    private void RecordFailure(SourceTarget target, long nowMs)
    {
        var count = _failedOpens.TryGetValue(target, out var previous) ? previous.Count + 1 : 1;
        _failedOpens[target] = new OpenFailure(nowMs, count);
    }

    private bool IsBlocked(SourceTarget target)
    {
        lock (_failedOpens)
            return _failedOpens.TryGetValue(target, out var failure) && !failure.RetryDue(Environment.TickCount64);
    }

    /// <summary>Caller holds the <see cref="_failedOpens"/> lock.</summary>
    private void ForgetFailures(Func<SourceTarget, bool> predicate)
    {
        foreach (var target in _failedOpens.Keys.Where(predicate).ToList())
            _failedOpens.Remove(target);
    }

    private void LogRebind(string bus, Channel channel, string? attachedName, SourceTarget? target)
    {
        switch (target)
        {
            case SourceTarget.App app when attachedName is not null:
                _logger.LogInformation("Re-attached {Bus} '{Name}' to process {Pid}.", bus, channel.Name, app.RootProcessId);
                break;
            case not null when attachedName is not null:
                _logger.LogInformation("Re-attached {Bus} '{Name}'.", bus, attachedName);
                break;
            default:
                _logger.LogInformation("Detached {Bus} '{Name}': its source is no longer available.", bus, channel.Name);
                break;
        }
    }

    /// <summary>A target's failure streak. The retry delay doubles per failure, capped at <see cref="MaxRetryDelay"/>.</summary>
    private readonly record struct OpenFailure(long AtMs, int Count)
    {
        public bool RetryDue(long nowMs)
        {
            var delayMs = Math.Min(
                FirstRetryDelay.TotalMilliseconds * Math.Pow(2, Count - 1),
                MaxRetryDelay.TotalMilliseconds);
            return nowMs - AtMs >= delayMs;
        }
    }
}
