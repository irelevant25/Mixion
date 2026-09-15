using NAudio.CoreAudioApi;
using NAudio.Wave;
using Mixion.Host.Interop;

namespace Mixion.Host.Audio;

/// <summary>
/// Wraps a single <see cref="WasapiCapture"/> endpoint, converts incoming
/// frames to internal interleaved stereo <c>float</c> at the device's
/// mix-format sample rate, and pushes them into a <see cref="RingBuffer"/>.
///
/// Mono devices are duplicated to both sides; stereo devices keep their
/// image; wider devices fold their extra channels into both sides (see
/// <see cref="WaveFormatX.ConvertToStereo"/>).
/// </summary>
public sealed class CaptureDevice : IAudioCaptureSource
{
    private readonly MMDevice      _device;
    private readonly WasapiCapture _capture;
    private readonly RingBuffer    _ring;
    private readonly float[]       _scratch;
    /// <summary>Managed id of the NAudio capture thread already registered with MMCSS.</summary>
    private int                    _mmcssThreadId;
    private volatile bool          _faulted;

    /// <summary>
    /// Default capture buffer in ms when the user hasn't picked a value.
    /// Matches the typical shared-mode WASAPI engine period; the runtime
    /// settings store overrides this.
    /// </summary>
    public const int DefaultCaptureBufferMs = 10;

    private readonly int _bufferMs;

    public string Id           => _device.ID;
    public string FriendlyName => _device.FriendlyName;
    public int    SampleRate   => _capture.WaveFormat.SampleRate;
    public int    SourceChannels => _capture.WaveFormat.Channels;
    public int    BitsPerSample  => _capture.WaveFormat.BitsPerSample;
    public RingBuffer Ring     => _ring;
    public int    BufferMilliseconds => _bufferMs;
    // Legacy shared-mode WASAPI doesn't surface the granted period directly.
    // Approximate from the configured ms; the OS may round up but this is the
    // floor MixEngine should plan around.
    public int    BufferFrames => Math.Max(1, _bufferMs * SampleRate / 1000);
    public bool   IsFaulted    => _faulted;
    public event EventHandler? DataReady;

    public CaptureDevice(MMDevice device, int ringCapacityFrames, int bufferMs = DefaultCaptureBufferMs)
    {
        _device   = device;
        _bufferMs = bufferMs;
        // NAudio's default capture buffer is 100 ms — that's the dominant
        // chunk of end-to-end latency in a CABLE round-trip setup
        // (mic → us → CABLE Input → CABLE Output → us), where the signal
        // hits a WASAPI capture twice. 10 ms matches the typical shared-mode
        // engine period on Windows; the OS rounds up if the device can't
        // honour it. The 3-arg ctor is the only way to set this in
        // NAudio 2.x — there's no public BufferMilliseconds property.
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: bufferMs)
        {
            ShareMode = AudioClientShareMode.Shared,
        };

        // Interleaved stereo — 2 floats per frame.
        _ring    = new RingBuffer(ringCapacityFrames * 2);
        _scratch = new float[ringCapacityFrames * 2];

        _capture.DataAvailable    += OnData;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { /* device may have vanished */ }
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable    -= OnData;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _device.Dispose();
    }

    private void OnData(object? _, WaveInEventArgs e)
    {
        // DataAvailable runs on NAudio's capture thread — a new one per
        // recording — so register that thread with MMCSS the first time we see
        // it. (Start() runs on whichever thread attaches the device.)
        var thread = Environment.CurrentManagedThreadId;
        if (_mmcssThreadId != thread)
        {
            _mmcssThreadId = thread;
            Mmcss.Begin();
        }

        if (e.BytesRecorded == 0) return;

        var frames = WaveFormatX.ConvertToStereo(e.Buffer.AsSpan(0, e.BytesRecorded), _capture.WaveFormat, _scratch);
        _ring.Write(_scratch.AsSpan(0, frames * 2));
        DataReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// NAudio reports a stream that died under us (device unplugged, format
    /// changed, taken exclusively by another app) by stopping with an
    /// exception. A plain <see cref="Stop"/> stops without one.
    /// </summary>
    private void OnRecordingStopped(object? _, StoppedEventArgs e)
    {
        if (e.Exception is not null) _faulted = true;
    }
}
