using NAudio.CoreAudioApi;
using NAudio.Wave;
using Mixion.Host.Interop;

namespace Mixion.Host.Audio;

/// <summary>
/// Wraps a single <see cref="WasapiOut"/> endpoint and exposes a
/// <see cref="RingBuffer"/> that the mix engine writes <em>interleaved
/// stereo</em> floats into (L, R, L, R, ...). NAudio drains the ring buffer
/// on its own render thread; the wave provider expands stereo into the
/// device's native channel count.
///
/// The ring's float capacity is doubled internally — callers still pass a
/// "frames" value, and we hold 2 floats per frame.
/// </summary>
public sealed class RenderDevice : IRenderDevice
{
    private readonly MMDevice                _device;
    private readonly WasapiOut               _output;
    private readonly RingBuffer              _ring;
    private readonly RingBufferWaveProvider  _provider;
    private IntPtr                           _mmcssHandle;

    public string Id           => _device.ID;
    public string FriendlyName => _device.FriendlyName;
    public int    SampleRate   => _output.OutputWaveFormat.SampleRate;
    public int    DestChannels => _output.OutputWaveFormat.Channels;
    public int    BitsPerSample => _output.OutputWaveFormat.BitsPerSample;
    public RingBuffer Ring     => _ring;
    public int    LatencyMs    { get; }
    // Legacy shared-mode WASAPI doesn't surface the granted period directly.
    // Approximate from the configured ms — used by MixEngine for block-size
    // alignment.
    public int    BufferFrames => Math.Max(1, LatencyMs * SampleRate / 1000);
    public RenderMode Mode => RenderMode.Shared;
    public string? ExclusiveFallbackReason { get; set; }

    public RenderDevice(MMDevice device, int ringCapacityFrames, int latencyMs = 10)
    {
        _device   = device;
        _output   = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
        LatencyMs = latencyMs;
        var fmt   = device.AudioClient.MixFormat;

        // Ring holds interleaved stereo floats — 2 floats per frame.
        _ring     = new RingBuffer(ringCapacityFrames * 2);
        _provider = new RingBufferWaveProvider(_ring, fmt);

        _output.Init(_provider);
    }

    public void Start()
    {
        _output.Play();
        _mmcssHandle = Mmcss.Begin();
    }

    public void Stop()
    {
        try { _output.Stop(); } catch { /* device may have vanished */ }
        Mmcss.Revert(_mmcssHandle);
        _mmcssHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
        _output.Dispose();
        _device.Dispose();
    }
}

/// <summary>
/// IWaveProvider that pulls interleaved stereo (L, R) <c>float</c> samples
/// from a <see cref="RingBuffer"/> and expands each pair to the device's
/// native channel layout. Allocation-free after construction.
///
/// • Mono devices receive <c>(L + R) / 2</c>.
/// • Stereo devices receive L on channel 0, R on channel 1.
/// • Multichannel devices receive L on 0, R on 1, and the L/R average on
///   any further channel — leaves surround/centre legs at a sensible level
///   without sending phantom audio.
/// </summary>
internal sealed class RingBufferWaveProvider : IWaveProvider
{
    private readonly RingBuffer _ring;
    private readonly WaveFormat _format;
    private readonly float[]   _scratch;

    public WaveFormat WaveFormat => _format;

    public RingBufferWaveProvider(RingBuffer ring, WaveFormat format)
    {
        if (!WaveFormatX.IsFloat(format) || format.BitsPerSample != 32)
            throw new NotSupportedException(
                "Render device must expose 32-bit IEEE float mix format. " +
                $"Got {WaveFormatX.Describe(format)}.");

        _ring    = ring;
        _format  = format;
        // 8192 floats = 4096 stereo frames per Read call — generously above
        // any single WasapiOut request size.
        _scratch = new float[8192];
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var channels = _format.Channels;
        var dst      = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                           buffer.AsSpan(offset, count));
        var frames   = dst.Length / channels;
        if (frames == 0) return 0;

        // We need 2 floats per frame from the ring. Cap by both the scratch
        // size (rounded down to an even count) and the requested frames.
        var maxFloats = _scratch.Length & ~1; // ensure even
        var needFloats = Math.Min(frames * 2, maxFloats);
        var read = _ring.Read(_scratch.AsSpan(0, needFloats));

        // Underrun handling: zero the remainder of the slot we asked for so
        // any frame we partially produce is silent rather than garbage.
        if (read < needFloats)
            _scratch.AsSpan(read, needFloats - read).Clear();

        var stereoFrames = needFloats / 2;

        if (channels == 1)
        {
            // Sum to mono so a panned signal still reaches a mono device.
            for (var i = 0; i < stereoFrames; i++)
            {
                var L = _scratch[i * 2];
                var R = _scratch[i * 2 + 1];
                dst[i] = (L + R) * 0.5f;
            }
        }
        else
        {
            for (var i = 0; i < stereoFrames; i++)
            {
                var L = _scratch[i * 2];
                var R = _scratch[i * 2 + 1];
                var off = i * channels;
                dst[off]     = L;
                dst[off + 1] = R;
                if (channels > 2)
                {
                    var mid = (L + R) * 0.5f;
                    for (var c = 2; c < channels; c++) dst[off + c] = mid;
                }
            }
        }

        // Tail beyond what we filled: silence the rest of the request.
        if (stereoFrames < frames)
            dst.Slice(stereoFrames * channels).Clear();

        return frames * channels * sizeof(float);
    }
}
