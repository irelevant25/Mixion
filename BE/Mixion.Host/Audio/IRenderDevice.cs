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

    /// <summary>Begin pulling from <see cref="Ring"/> and rendering to the device.</summary>
    void Start();

    /// <summary>Stop rendering. Implementations should swallow "device gone" errors.</summary>
    void Stop();
}
