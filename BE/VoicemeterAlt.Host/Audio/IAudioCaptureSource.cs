namespace VoicemeterAlt.Host.Audio;

/// <summary>
/// Common shape every input source feeding the <see cref="MixEngine"/> must
/// honour. Decouples the engine from "is this a physical microphone, a virtual
/// cable, or a per-process loopback?" — each implementation just has to
/// produce mono float frames into a <see cref="RingBuffer"/> at a stable
/// sample rate.
///
/// Implementations:
/// <list type="bullet">
///   <item><see cref="CaptureDevice"/> — WASAPI shared-mode capture (mics, virtual cables).</item>
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

    /// <summary>Mono float ring the source writes into; the mix thread reads from here.</summary>
    RingBuffer Ring { get; }

    /// <summary>Begin delivering frames. Idempotent for repeated calls.</summary>
    void Start();

    /// <summary>Stop delivering frames. Implementations should swallow "device gone" errors.</summary>
    void Stop();
}
