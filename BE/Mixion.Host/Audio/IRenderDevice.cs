namespace Mixion.Host.Audio;

/// <summary>
/// Common shape for any render endpoint the mix engine writes to. Decouples
/// the engine from "is this a regular NAudio-backed <see cref="RenderDevice"/>
/// or the IAudioClient3-driven <see cref="LowLatencyRenderDevice"/>?" — both
/// expose the same interleaved-stereo-float ring + lifecycle hooks the engine
/// needs.
/// </summary>
public interface IRenderDevice : IDisposable
{
    /// <summary>WASAPI <c>MMDevice.ID</c>. Stable across enumerations and across host restarts for the same physical endpoint.</summary>
    string Id { get; }

    /// <summary>Human-readable label shown in the FE device picker.</summary>
    string FriendlyName { get; }

    /// <summary>Sample rate of the float pairs the mix engine writes here. Must match every other engine endpoint.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Native channel count the device exposes (1 = mono, 2 = stereo, 6 =
    /// 5.1, …). The mix engine writes interleaved stereo into <see cref="Ring"/>
    /// and the device implementation expands as needed. Display-only.
    /// </summary>
    int DestChannels { get; }

    /// <summary>
    /// Native bit depth the device's mix format reports. Display-only —
    /// rendering uses 32-bit float regardless because that's what shared-mode
    /// WASAPI mix format always exposes.
    /// </summary>
    int BitsPerSample { get; }

    /// <summary>
    /// Ring of interleaved stereo floats (L, R, L, R, …). The mix engine is
    /// the sole writer; the device's render thread is the sole reader.
    /// Sized in floats — 2 floats per stereo frame.
    /// </summary>
    RingBuffer Ring { get; }

    /// <summary>
    /// Configured render-side buffer in ms. Surfaced via the latency
    /// estimator so the FE shows what the OS actually granted (low-latency
    /// devices report ~3 ms on Win10 1803+, regular devices 10 ms).
    /// </summary>
    int LatencyMs { get; }

    /// <summary>
    /// Granted (or configured) period in stereo frames at <see cref="SampleRate"/>.
    /// Low-latency devices report what the OS actually granted via
    /// <c>GetSharedModeEnginePeriod</c>; legacy devices approximate from
    /// <see cref="LatencyMs"/>. Used by <see cref="MixEngine"/> to align its
    /// mix block size with the slowest device on the bus.
    /// </summary>
    int BufferFrames { get; }

    /// <summary>
    /// How this device was actually opened — independent of what the user
    /// asked for. A device the user opted into exclusive mode that fell
    /// back (virtual cable, picky driver) reports the resolved mode
    /// (<see cref="RenderMode.SharedLowLatency"/> or
    /// <see cref="RenderMode.Shared"/>), not <see cref="RenderMode.Exclusive"/>.
    /// Surfaced to the FE so the user sees what they got, not what they
    /// requested.
    /// </summary>
    RenderMode Mode { get; }

    /// <summary>
    /// Set when the user opted this device into exclusive mode but the
    /// driver refused — captures the underlying exception message so the
    /// FE can show <em>why</em> (format mismatch, device in use,
    /// alignment, etc.) instead of a generic "fell back" warning.
    /// Mutable so <see cref="EngineFactory"/> can stamp the reason onto
    /// whichever shared-mode device class it ended up using as the
    /// fallback. Null when no exclusive attempt was made or the attempt
    /// succeeded.
    /// </summary>
    string? ExclusiveFallbackReason { get; set; }

    /// <summary>Begin pulling from <see cref="Ring"/> and rendering to the device.</summary>
    void Start();

    /// <summary>Stop rendering. Implementations should swallow "device gone" errors.</summary>
    void Stop();
}

/// <summary>
/// How a render device was actually opened by <see cref="EngineFactory"/>.
/// Wire enum — the FE shows different badges per value.
/// </summary>
public enum RenderMode
{
    /// <summary>Legacy shared-mode WASAPI via NAudio — buffer ms set by user, OS may round up.</summary>
    Shared = 0,

    /// <summary><c>IAudioClient3::InitializeSharedAudioStream</c> at the OS-reported minimum engine period (~3 ms).</summary>
    SharedLowLatency = 1,

    /// <summary>WASAPI exclusive mode — device is locked to the engine; smallest latency at the cost of other apps' audio.</summary>
    Exclusive = 2,
}
