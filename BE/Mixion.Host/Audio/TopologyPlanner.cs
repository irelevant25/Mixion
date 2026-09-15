using Mixion.Host.State;

namespace Mixion.Host.Audio;

/// <summary>What a channel slot should be bound to.</summary>
public abstract record SourceTarget
{
    private SourceTarget() { }

    /// <summary>A WASAPI endpoint, by <c>MMDevice.ID</c>.</summary>
    public sealed record Endpoint(string DeviceId) : SourceTarget;

    /// <summary>A per-process loopback on an app's root process.</summary>
    public sealed record App(string ProcessName, int RootProcessId) : SourceTarget;
}

/// <summary>How an existing slot is bound right now.</summary>
/// <param name="HasSource">A source or device is attached to the slot.</param>
/// <param name="Healthy">The attached source is still delivering: not faulted and, for an app, its process is alive.</param>
/// <param name="TargetProcessId">For an app slot, the root process the loopback is bound to.</param>
public readonly record struct SlotBinding(bool HasSource, bool Healthy, int? TargetProcessId = null)
{
    public static SlotBinding Empty => new(false, false);
}

/// <summary>The OS side of one reconciliation pass.</summary>
/// <param name="CaptureEndpoints">Active capture endpoints, or null when devices weren't scanned this pass.</param>
/// <param name="RenderEndpoints">Active render endpoints, or null when devices weren't scanned this pass.</param>
/// <param name="AudioProcesses">Apps that own an audio session.</param>
/// <param name="Processes">Every running process — lets a known app re-attach before it opens a session.</param>
public sealed record TopologySnapshot(
    IReadOnlyList<EndpointInfo>? CaptureEndpoints,
    IReadOnlyList<EndpointInfo>? RenderEndpoints,
    IReadOnlyList<AudioProcess>  AudioProcesses,
    ProcessSnapshot              Processes);

/// <summary>Bind existing slot <paramref name="Index"/> to <paramref name="Target"/>, or detach it when null.</summary>
public sealed record SlotRebind(int Index, SourceTarget? Target);

/// <summary>A channel to attach that isn't in the state yet.</summary>
public sealed record NewChannel(string ChannelId, string Name, SourceTarget Target);

public sealed record TopologyPlan(
    IReadOnlyList<SlotRebind> InputRebinds,
    IReadOnlyList<SlotRebind> OutputRebinds,
    IReadOnlyList<NewChannel> NewInputs,
    IReadOnlyList<NewChannel> NewOutputs)
{
    public bool IsEmpty =>
        InputRebinds.Count == 0 && OutputRebinds.Count == 0 && NewInputs.Count == 0 && NewOutputs.Count == 0;
}

/// <summary>
/// Decides how the engine's channels should follow the OS: pure, so every rule
/// is unit-testable without audio hardware. <see cref="TopologyReconciler"/>
/// feeds it a snapshot and applies the resulting plan.
///
/// Rules:
/// <list type="bullet">
///   <item><b>Apps</b> (<c>process:&lt;name&gt;</c>) are identified by name only. A healthy binding stays put — unless it's bound to an instance that never played audio while another instance of the app does, in which case it follows the one making sound. A dead or missing binding is re-bound to a running instance — preferring one with an audio session, else any capturable root process of that name — or detached when the app isn't running.</item>
///   <item><b>Devices</b> are identified by endpoint id and only reconsidered on passes that scanned endpoints. An active endpoint at the engine rate is (re-)attached when its slot is empty or its stream faulted; a slot whose endpoint went away or changed rate is detached.</item>
///   <item><b>New</b> endpoints at the engine rate and new apps with an audio session are attached as new channels, except apps the user detached this session.</item>
///   <item>Targets that recently failed to open are skipped until the caller's cooldown expires.</item>
/// </list>
/// </summary>
public static class TopologyPlanner
{
    public static TopologyPlan Plan(
        MixerState                   state,
        IReadOnlyList<SlotBinding>   inputBindings,
        IReadOnlyList<SlotBinding>   outputBindings,
        TopologySnapshot             os,
        int                          engineSampleRate,
        Func<string, bool>           isSuppressed,
        Func<SourceTarget, bool>     isBlocked)
    {
        var sessionRootsByName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in os.AudioProcesses)
        {
            if (!sessionRootsByName.TryGetValue(p.ProcessName, out var roots))
                sessionRootsByName[p.ProcessName] = roots = new List<int>();
            roots.Add(p.RootProcessId);
        }

        var inputRebinds = new List<SlotRebind>();
        var inputIds     = new HashSet<string>(StringComparer.Ordinal);
        var knownApps    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < state.Inputs.Length; i++)
        {
            var channelId = state.Inputs[i].Id;
            var binding   = i < inputBindings.Count ? inputBindings[i] : SlotBinding.Empty;
            inputIds.Add(channelId);

            if (ProcessChannelId.TryGetProcessName(channelId, out var app))
            {
                knownApps.Add(app);
                PlanAppSlot(i, app, binding, sessionRootsByName, os.Processes, isBlocked, inputRebinds);
            }
            else
            {
                PlanEndpointSlot(i, channelId, binding, os.CaptureEndpoints, engineSampleRate, isBlocked, inputRebinds);
            }
        }

        var outputRebinds = new List<SlotRebind>();
        var outputIds     = new HashSet<string>(StringComparer.Ordinal);
        for (var o = 0; o < state.Outputs.Length; o++)
        {
            var channelId = state.Outputs[o].Id;
            var binding   = o < outputBindings.Count ? outputBindings[o] : SlotBinding.Empty;
            outputIds.Add(channelId);
            PlanEndpointSlot(o, channelId, binding, os.RenderEndpoints, engineSampleRate, isBlocked, outputRebinds);
        }

        // New channels: physical inputs first, then apps — the same order a
        // rebuild appends them in.
        var newInputs = NewEndpoints(os.CaptureEndpoints, inputIds, engineSampleRate, isBlocked);
        foreach (var p in os.AudioProcesses)
        {
            if (knownApps.Contains(p.ProcessName) || isSuppressed(p.ProcessName)) continue;
            var target = new SourceTarget.App(p.ProcessName, p.RootProcessId);
            if (isBlocked(target)) continue;

            knownApps.Add(p.ProcessName);
            newInputs.Add(new NewChannel(
                ProcessChannelId.For(p.ProcessName),
                ProcessChannelId.DisplayName(p.ProcessName),
                target));
        }

        var newOutputs = NewEndpoints(os.RenderEndpoints, outputIds, engineSampleRate, isBlocked);

        return new TopologyPlan(inputRebinds, outputRebinds, newInputs, newOutputs);
    }

    private static void PlanAppSlot(
        int                                       index,
        string                                    app,
        SlotBinding                               binding,
        IReadOnlyDictionary<string, List<int>>    sessionRootsByName,
        ProcessSnapshot                           processes,
        Func<SourceTarget, bool>                  isBlocked,
        List<SlotRebind>                          rebinds)
    {
        sessionRootsByName.TryGetValue(app, out var sessionRoots);

        if (binding.HasSource && binding.Healthy)
        {
            // Stay on the bound instance unless it never played audio while
            // another instance does — then follow the one making sound.
            if (sessionRoots is null
                || binding.TargetProcessId is not { } bound
                || sessionRoots.Contains(bound))
            {
                return;
            }

            var follow = new SourceTarget.App(app, sessionRoots[0]);
            if (!isBlocked(follow)) rebinds.Add(new SlotRebind(index, follow));
            return;
        }

        SourceTarget? target = null;
        if (sessionRoots is { Count: > 0 })
        {
            target = new SourceTarget.App(app, sessionRoots[0]);
        }
        else if (processes.FindCapturableAppRoots(app) is { Count: > 0 } roots)
        {
            target = new SourceTarget.App(app, roots[0]);
        }

        if (target is not null && !isBlocked(target))
        {
            rebinds.Add(new SlotRebind(index, target));
        }
        else if (binding.HasSource)
        {
            // The app is gone (or can't be opened right now): let go of the
            // dead stream so the channel reads as unavailable.
            rebinds.Add(new SlotRebind(index, null));
        }
    }

    private static void PlanEndpointSlot(
        int                          index,
        string                       deviceId,
        SlotBinding                  binding,
        IReadOnlyList<EndpointInfo>? endpoints,
        int                          engineSampleRate,
        Func<SourceTarget, bool>     isBlocked,
        List<SlotRebind>             rebinds)
    {
        // Devices weren't scanned this pass; nothing is known to have changed.
        if (endpoints is null) return;

        var usable = false;
        foreach (var e in endpoints)
        {
            if (string.Equals(e.Id, deviceId, StringComparison.Ordinal) && e.SampleRate == engineSampleRate)
            {
                usable = true;
                break;
            }
        }

        if (usable)
        {
            if (binding.HasSource && binding.Healthy) return;

            var target = new SourceTarget.Endpoint(deviceId);
            if (!isBlocked(target))
                rebinds.Add(new SlotRebind(index, target));
            else if (binding.HasSource)
                rebinds.Add(new SlotRebind(index, null));
        }
        else if (binding.HasSource)
        {
            rebinds.Add(new SlotRebind(index, null));
        }
    }

    private static List<NewChannel> NewEndpoints(
        IReadOnlyList<EndpointInfo>? endpoints,
        HashSet<string>              knownIds,
        int                          engineSampleRate,
        Func<SourceTarget, bool>     isBlocked)
    {
        var result = new List<NewChannel>();
        if (endpoints is null) return result;

        foreach (var e in endpoints)
        {
            if (e.SampleRate != engineSampleRate || knownIds.Contains(e.Id)) continue;
            var target = new SourceTarget.Endpoint(e.Id);
            if (isBlocked(target)) continue;

            knownIds.Add(e.Id);
            result.Add(new NewChannel(e.Id, e.FriendlyName, target));
        }
        return result;
    }
}
