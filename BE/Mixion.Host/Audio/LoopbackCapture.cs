using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Mixion.Host.Audio;

/// <summary>
/// Wraps <see cref="WasapiLoopbackCapture"/> on a render endpoint and pushes
/// the captured mix (down-converted to internal float mono) into a ring
/// buffer.
///
/// Loopback gives us the sum of <em>all</em> audio Windows is sending to
/// that device — our app's render output, Chrome, system sounds, anything
/// else. That's the right signal for the output VU meter: it shows what
/// the user actually hears, not just our app's contribution.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private readonly MMDevice              _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly RingBuffer            _ring;
    private readonly float[]               _scratchMono;

    public string Id           => _device.ID;
    public string FriendlyName => _device.FriendlyName;
    public int    SampleRate   => _capture.WaveFormat.SampleRate;
    public RingBuffer Ring     => _ring;

    public LoopbackCapture(MMDevice device, int ringCapacitySamples)
    {
        _device      = device;
        _capture     = new WasapiLoopbackCapture(device);
        _ring        = new RingBuffer(ringCapacitySamples);
        _scratchMono = new float[ringCapacitySamples];

        _capture.DataAvailable += OnData;
    }

    public void Start()  => _capture.StartRecording();

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { /* device may have vanished */ }
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
