using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc;

/// <summary>
/// Indirection between the long-lived web stack and the <see cref="MixEngine"/>
/// instance, which is created after Kestrel is already running. Singleton in
/// DI; the Program bootstrap publishes the engine here once it has started,
/// and RPC handlers read it via <see cref="Current"/>.
///
/// Reads are <see cref="Volatile.Read{T}"/> so a freshly published engine is
/// observed promptly without locking.
///
/// All engine REBUILDS — refresh, add/remove process loopback, auto-rebind
/// after a target PID dies — funnel through <see cref="RebuildAsync"/>. The
/// internal semaphore serialises concurrent rebuilds (e.g. user clicks Refresh
/// while the health monitor is mid-rebind) so we never end up with two
/// engines competing for the same WASAPI endpoints.
/// </summary>
public sealed class EngineHost
{
    private MixEngine? _engine;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    public MixEngine? Current => Volatile.Read(ref _engine);

    public void Set(MixEngine? engine) => Interlocked.Exchange(ref _engine, engine);

    /// <summary>
    /// Tear down the running engine and rebuild it via <paramref name="factory"/>.
    /// Optionally <paramref name="transform"/> the snapshot before passing it
    /// back to <see cref="EngineFactory.Build"/> — handy for "remove this
    /// channel before rebuilding" or "drop dead process loopbacks first".
    ///
    /// Pass <paramref name="autoDiscoverNewProcesses"/> = false when the caller
    /// wants the rebuild to honour the transformed state EXACTLY (no surprise
    /// re-addition of a channel the user just removed). Refresh leaves it
    /// true so apps that started after the host launched are picked up.
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
        await _rebuildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _engine?.SnapshotState();
            var seedState = transform is null ? previous : transform(previous);

            var oldEngine = _engine;
            Interlocked.Exchange(ref _engine, null);
            oldEngine?.Dispose();

            var result = factory.Build(seedState, autoDiscoverNewProcesses);
            Interlocked.Exchange(ref _engine, result.Engine);
            return result.InitialState;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }
}
