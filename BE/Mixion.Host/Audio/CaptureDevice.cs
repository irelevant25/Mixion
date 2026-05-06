using NAudio.CoreAudioApi;
using NAudio.Wave;
using Mixion.Host.Interop;

namespace Mixion.Host.Audio;

/// <summary>
/// Wraps a single <see cref="WasapiCapture"/> endpoint, converts incoming
/// frames to internal <c>float</c> mono at the device's mix-format sample
/// rate, and pushes them into a <see cref="RingBuffer"/>.
///
/// "Internal mono" is the common denominator the mix engine reasons about.
/// Multi-channel mixing per output bus is handled in the matrix, not here.
/// </summary>
public sealed class CaptureDevice : IAudioCaptureSource
{
    private readonly MMDevice      _device;
    private readonly WasapiCapture _capture;
    private readonly RingBuffer    _ring;
    private readonly float[]       _scratchMono;
    private IntPtr                 _mmcssHandle;

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
    public event EventHandler? DataReady;

    public CaptureDevice(MMDevice device, int ringCapacitySamples, int bufferMs = DefaultCaptureBufferMs)
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

        _ring        = new RingBuffer(ringCapacitySamples);
        _scratchMono = new float[ringCapacitySamples];

        _capture.DataAvailable += OnData;
    }

    public void Start()
    {
        _capture.StartRecording();

        // NAudio runs DataAvailable on its own thread; we can't easily reach
        // it. Tag *this* thread as Pro Audio anyway in case StartRecording
        // pumps inline (it doesn't on current NAudio, but it's harmless).
        _mmcssHandle = Mmcss.Begin();
    }

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { /* device may have vanished */ }
        Mmcss.Revert(_mmcssHandle);
        _mmcssHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _device.Dispose();
    }

    private void OnData(object? _, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var fmt    = _capture.WaveFormat;
        var span   = _scratchMono.AsSpan();
        var frames = WaveFormatX.ConvertToMono(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, span);
        _ring.Write(span.Slice(0, frames));
        DataReady?.Invoke(this, EventArgs.Empty);
    }
}
