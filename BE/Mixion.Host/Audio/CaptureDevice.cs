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

    public string Id           => _device.ID;
    public string FriendlyName => _device.FriendlyName;
    public int    SampleRate   => _capture.WaveFormat.SampleRate;
    public int    SourceChannels => _capture.WaveFormat.Channels;
    public RingBuffer Ring     => _ring;

    public CaptureDevice(MMDevice device, int ringCapacitySamples)
    {
        _device  = device;
        _capture = new WasapiCapture(device, useEventSync: true)
        {
            // M2: keep buffer compact for low latency. NAudio's default is
            // chunky on capture; we shrink to ~10 ms equivalent at 48 kHz.
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
    }
}
