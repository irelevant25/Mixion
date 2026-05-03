using VoicemeterAlt.Host.Audio;

namespace VoicemeterAlt.Host.Ipc;

/// <summary>
/// Indirection between the long-lived web stack and the <see cref="MixEngine"/>
/// instance, which is created after Kestrel is already running. Singleton in
/// DI; the Program bootstrap publishes the engine here once it has started,
/// and RPC handlers read it via <see cref="Current"/>.
///
/// Reads are <see cref="Volatile.Read{T}"/> so a freshly published engine is
/// observed promptly without locking.
/// </summary>
public sealed class EngineHost
{
    private MixEngine? _engine;

    public MixEngine? Current => Volatile.Read(ref _engine);

    public void Set(MixEngine? engine) => Interlocked.Exchange(ref _engine, engine);
}
