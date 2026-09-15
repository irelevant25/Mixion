namespace Mixion.Host.Audio;

/// <summary>
/// Common shape every input source feeding the <see cref="MixEngine"/> must
/// honour. Decouples the engine from "is this a physical microphone, a virtual
/// cable, or a per-process loopback?" — each implementation just has to
/// produce interleaved stereo float frames into a <see cref="RingBuffer"/> at a
/// stable sample rate.
///
/// Implementations:
/// <list type="bullet">
///   <item><see cref="CaptureDevice"/> — WASAPI shared-mode capture (mics, virtual cables).</item>
///   <item><see cref="LowLatencyCaptureDevice"/> — IAudioClient3 shared-mode capture at the minimum engine period.</item>
///   <item><see cref="ProcessLoopbackCapture"/> — per-process loopback (Win10 20348+).</item>
/// </list>
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>Stable id used as the channel id in <see cref="State.Channel"/>. Must survive a host restart for the same source.</summary>
    string Id { get; }

    /// <summary>Human-readable label shown in the FE device picker.</summary>
    string FriendlyName { get; }

    /// <summary>Sample rate of the frames delivered into <see cref="Ring"/>. Must match all other engine sources.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Source channel count before our stereo fold. Reflects the device's mix
    /// format (or, for process loopback, the synthetic format we requested).
    /// Display-only — the engine's internal bus is always stereo.
    /// </summary>
    int SourceChannels { get; }

    /// <summary>
    /// Source bit depth before our float conversion. Same display-only
    /// caveat — the engine works in 32-bit float internally.
    /// </summary>
    int BitsPerSample { get; }

    /// <summary>
    /// Ring of interleaved stereo floats (<c>L, R, L, R, …</c>) — 2 floats per
    /// frame. The source's capture thread is the sole writer and only ever
    /// writes whole frames; the mix thread is the sole reader.
    /// </summary>
    RingBuffer Ring { get; }

    /// <summary>
    /// Requested capture buffer in ms — the value we asked WASAPI for at
    /// open time. Used by the latency estimator on the signal-flow page;
    /// the OS may round up under the hood, but this is the configured floor.
    /// </summary>
    int BufferMilliseconds { get; }

    /// <summary>
    /// Granted (or configured) period in frames at <see cref="SampleRate"/>.
    /// Low-latency devices report what the OS actually granted via
    /// <c>GetSharedModeEnginePeriod</c>; legacy devices approximate from
    /// <see cref="BufferMilliseconds"/>. Used by <see cref="MixEngine"/> to
    /// align its mix block size with the slowest device on the bus.
    /// </summary>
    int BufferFrames { get; }

    /// <summary>
    /// True once the stream has failed under us — the device was removed or
    /// reconfigured, or the loopback broke. A faulted source delivers nothing
    /// more; the device watcher re-opens or detaches it without rebuilding the
    /// engine.
    /// </summary>
    bool IsFaulted { get; }

    /// <summary>
    /// Fired immediately after a fresh block is appended to <see cref="Ring"/>.
    /// Lets the mix loop replace its 1 ms polling sleep with a cooperative
    /// wait — capture handlers signal, mix loop wakes, no scheduler tax.
    /// </summary>
    event EventHandler? DataReady;

    /// <summary>Begin delivering frames. Idempotent for repeated calls.</summary>
    void Start();

    /// <summary>Stop delivering frames. Implementations should swallow "device gone" errors.</summary>
    void Stop();
}
