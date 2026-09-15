using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc;

/// <summary>
/// Result of a <see cref="EngineHost.ChangeTopologyAsync"/> callback.
/// </summary>
/// <param name="Changed">The channel set or channel availability changed.</param>
/// <param name="RebuildSeed">When set, the engine couldn't take the change in place (no spare slots) and must be rebuilt from this state.</param>
public readonly record struct TopologyOutcome(bool Changed, MixerState? RebuildSeed = null)
{
    public static TopologyOutcome Unchanged    => new(false);
    public static TopologyOutcome ChangedState => new(true);
    public static TopologyOutcome Rebuild(MixerState seed) => new(true, seed);
}

/// <summary>
/// Indirection between the long-lived web stack and the <see cref="MixEngine"/>
/// instance, which is created after Kestrel is already running. Singleton in
/// DI; RPC handlers read the engine via <see cref="Current"/>.
///
/// Reads are <see cref="Volatile.Read{T}"/> so a freshly published engine is
/// observed promptly without locking.
///
/// The engine is installed, replaced and disposed only in here, and every
/// change to its <em>topology</em> — the startup build and full rebuilds
/// (<see cref="RebuildAsync"/>), in-place changes (<see cref="ChangeTopologyAsync"/>:
/// the device watcher attaching sources, a preset adding placeholder channels),
/// recovery after a failed rebuild (<see cref="TryRecoverAsync"/>) and shutdown
/// (<see cref="ShutdownAsync"/>) — holds one semaphore, so two changes never
/// race for the same WASAPI endpoints or slot indices. Ordinary control RPCs
/// (gain, mute, routing, DSP) don't need it: they go through
/// <see cref="MixEngine.UpdateState"/>.
/// </summary>
public sealed class EngineHost
{
    /// <summary>How long a request waits for startup before answering with whatever state exists.</summary>
    private static readonly TimeSpan StartupWaitLimit = TimeSpan.FromSeconds(15);

    private MixEngine? _engine;
    private readonly SemaphoreSlim _topologyLock = new(1, 1);
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The state the last rebuild started from or produced. When a rebuild
    /// fails and leaves no engine, the next one starts from here instead of from
    /// scratch, so channel settings, routes and placeholder channels survive.
    /// </summary>
    private MixerState? _lastKnownState;

    /// <summary>The last rebuild threw and left no engine; <see cref="TryRecoverAsync"/> retries it.</summary>
    private bool _rebuildFailed;

    private volatile bool _shutDown;

    /// <summary>
    /// Raised after the channel set or channel availability changed — a
    /// rebuild, a device or app attached/detached by the watcher, a preset that
    /// added placeholder channels. Not raised for ordinary control RPCs; the
    /// client that sent those already has the result.
    /// </summary>
    public event Action<MixerState>? TopologyChanged;

    public MixEngine? Current => Volatile.Read(ref _engine);

    /// <summary>
    /// Called once startup is over — the engine was built (or failed to) and the
    /// last preset was applied. Releases everyone in <see cref="WaitForStartupAsync"/>.
    /// </summary>
    public void CompleteStartup() => _startup.TrySetResult();

    /// <summary>
    /// Kestrel serves requests before the audio engine is up. <c>/api/session</c>
    /// and <c>getState</c> wait here (at most <see cref="StartupWaitLimit"/>) so a
    /// page that loads early doesn't render an empty mixer.
    /// </summary>
    public async Task WaitForStartupAsync(CancellationToken ct = default)
    {
        try
        {
            await _startup.Task.WaitAsync(StartupWaitLimit, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Answer with whatever state exists rather than hang the UI.
        }
    }

    /// <summary>
    /// Build the engine, tearing down the running one first. Optionally
    /// <paramref name="transform"/> the current state before passing it to
    /// <see cref="EngineFactory.Build"/> — handy for "remove this channel before
    /// rebuilding". With no engine running this starts from the last known
    /// state (a previous rebuild failed) or, at startup, from nothing.
    ///
    /// Pass <paramref name="autoDiscoverNewProcesses"/> = false when the caller
    /// wants the rebuild to honour the transformed state exactly. Refresh
    /// leaves it true so apps that started after the host launched are picked up.
    ///
    /// Returns the freshly-built <see cref="MixerState"/> so the caller can
    /// publish it back over the wire without a separate <c>getState</c>.
    /// </summary>
    public async Task<MixerState> RebuildAsync(
        EngineFactory factory,
        Func<MixerState?, MixerState?>? transform = null,
        bool autoDiscoverNewProcesses = true,
        CancellationToken ct = default)
    {
        MixerState state;
        await _topologyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _engine?.SnapshotState() ?? _lastKnownState;
            var seed     = transform is null ? previous : transform(previous);
            state = RebuildLocked(factory, seed, autoDiscoverNewProcesses);
        }
        finally
        {
            _topologyLock.Release();
        }

        OnTopologyChanged(state);
        return state;
    }

    /// <summary>
    /// Run <paramref name="change"/> against the running engine with exclusive
    /// access to its topology. The callback attaches / detaches sources and
    /// publishes the matching state; its outcome says whether listeners should
    /// hear about it, or that a rebuild is needed instead. Does nothing when no
    /// engine is running.
    /// </summary>
    public async Task<TopologyOutcome> ChangeTopologyAsync(
        EngineFactory factory,
        Func<MixEngine, TopologyOutcome> change,
        CancellationToken ct = default)
    {
        TopologyOutcome outcome;
        MixerState? changed = null;

        await _topologyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var engine = _engine;
            if (engine is null || _shutDown) return TopologyOutcome.Unchanged;

            outcome = change(engine);
            if (outcome.RebuildSeed is not null)
                changed = RebuildLocked(factory, outcome.RebuildSeed, autoDiscoverNewProcesses: true);
            else if (outcome.Changed)
                changed = engine.SnapshotState();
        }
        finally
        {
            _topologyLock.Release();
        }

        if (changed is not null) OnTopologyChanged(changed);
        return outcome;
    }

    /// <summary>
    /// If no engine is running because a rebuild failed — the startup build
    /// included — try again from the state that rebuild started with. Returns
    /// false when there was nothing to recover (an engine is running, nothing
    /// failed, or the host is shutting down); throws when the rebuild fails again.
    /// </summary>
    public async Task<bool> TryRecoverAsync(EngineFactory factory, CancellationToken ct = default)
    {
        MixerState state;
        await _topologyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null || _shutDown || !_rebuildFailed) return false;
            state = RebuildLocked(factory, _lastKnownState, autoDiscoverNewProcesses: true);
        }
        finally
        {
            _topologyLock.Release();
        }

        OnTopologyChanged(state);
        return true;
    }

    /// <summary>
    /// Stop and dispose the engine for good. Waits (at most
    /// <paramref name="timeout"/>) for a topology change in flight so it can't
    /// install an engine behind our back; later rebuilds are refused.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        var acquired = await _topologyLock.WaitAsync(timeout).ConfigureAwait(false);
        try
        {
            _shutDown = true;
            Interlocked.Exchange(ref _engine, null)?.Dispose();
        }
        finally
        {
            if (acquired) _topologyLock.Release();
        }
    }

    private MixerState RebuildLocked(EngineFactory factory, MixerState? seed, bool autoDiscoverNewProcesses)
    {
        if (_shutDown) throw new InvalidOperationException("The host is shutting down.");

        // Remember where this rebuild starts: if Build throws, recovery begins
        // here rather than from an empty mixer.
        if (seed is not null) _lastKnownState = seed;

        Interlocked.Exchange(ref _engine, null)?.Dispose();
        _rebuildFailed = true;

        var result = factory.Build(seed, autoDiscoverNewProcesses);
        Interlocked.Exchange(ref _engine, result.Engine);
        _rebuildFailed  = false;
        _lastKnownState = result.InitialState;

        // Checked after publishing: ShutdownAsync sets the flag before it takes
        // the engine, so whichever side runs second disposes it exactly once.
        if (_shutDown)
        {
            Interlocked.Exchange(ref _engine, null)?.Dispose();
            throw new InvalidOperationException("The host is shutting down.");
        }
        return result.InitialState;
    }

    private void OnTopologyChanged(MixerState state)
    {
        if (TopologyChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<MixerState>>())
        {
            try { handler(state); }
            catch { /* a failing listener must not break the change that already happened */ }
        }
    }
}
