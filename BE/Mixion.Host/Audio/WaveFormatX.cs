using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Mixion.Host.Audio;

/// <summary>
/// Helpers for inspecting a WASAPI <see cref="WaveFormat"/>. Modern Windows
/// reports the shared-mode mix format as <c>WAVEFORMATEXTENSIBLE</c> rather
/// than the legacy basic encodings; both wrap the same byte layout, but the
/// <see cref="WaveFormat.Encoding"/> field reads as <c>Extensible</c>. These
/// helpers normalise that so the rest of the audio path doesn't have to.
/// </summary>
internal static class WaveFormatX
{
    /// <summary>
    /// <c>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</c> — appears in
    /// <see cref="WaveFormatExtensible.SubFormat"/> when the underlying
    /// stream is IEEE 32-bit float.
    /// </summary>
    public static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// <c>KSDATAFORMAT_SUBTYPE_PCM</c> — appears in
    /// <see cref="WaveFormatExtensible.SubFormat"/> for plain integer PCM.
    /// </summary>
    public static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat;
    }

    public static bool IsPcm(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.Pcm) return true;
        return fmt is WaveFormatExtensible ext && ext.SubFormat == PcmSubFormat;
    }

    /// <summary>
    /// Short label for diagnostics (e.g. <c>"IEEE float 32-bit"</c>) regardless
    /// of whether the format is basic or <c>WaveFormatExtensible</c>.
    /// </summary>
    public static string Describe(WaveFormat fmt)
    {
        if (IsFloat(fmt)) return $"IEEE float {fmt.BitsPerSample}-bit";
        if (IsPcm(fmt))   return $"PCM {fmt.BitsPerSample}-bit";
        return $"{fmt.Encoding} {fmt.BitsPerSample}-bit";
    }

    /// <summary>
    /// Convert <paramref name="bytes"/> (interleaved, in <paramref name="fmt"/>'s
    /// native sample type) to interleaved stereo float frames
    /// (<c>L, R, L, R, …</c>) in <paramref name="dstStereo"/>. Returns the number
    /// of frames written — i.e. <c>2 × frames</c> floats. Frames that don't fit
    /// in the destination are dropped rather than overrunning it.
    ///
    /// Channel mapping:
    /// <list type="bullet">
    ///   <item>1 channel — duplicated to both sides.</item>
    ///   <item>2 channels — L and R pass straight through.</item>
    ///   <item>3+ channels — the first pair stays the L/R image; every further
    ///         channel (centre, LFE, surrounds, extra interface inputs) is folded
    ///         equally into both sides, normalised so a signal present on every
    ///         channel keeps unity level.</item>
    /// </list>
    ///
    /// Shared by every capture source — they all feed stereo float rings. No
    /// allocations.
    /// </summary>
    public static int ConvertToStereo(ReadOnlySpan<byte> bytes, WaveFormat fmt, Span<float> dstStereo)
    {
        var channels = fmt.Channels;
        if (channels <= 0) return 0;

        if (IsFloat(fmt) && fmt.BitsPerSample == 32)
            return FoldToStereo<float, Float32Sample>(MemoryMarshal.Cast<byte, float>(bytes), channels, dstStereo);

        if (IsPcm(fmt) && fmt.BitsPerSample == 16)
            return FoldToStereo<short, Pcm16Sample>(MemoryMarshal.Cast<byte, short>(bytes), channels, dstStereo);

        if (IsPcm(fmt) && fmt.BitsPerSample == 32)
            return FoldToStereo<int, Pcm32Sample>(MemoryMarshal.Cast<byte, int>(bytes), channels, dstStereo);

        throw new NotSupportedException($"Unsupported capture format: {Describe(fmt)}");
    }

    private static int FoldToStereo<T, TSample>(ReadOnlySpan<T> src, int channels, Span<float> dst)
        where T : unmanaged
        where TSample : ISampleConverter<T>
    {
        var frames = Math.Min(src.Length / channels, dst.Length / 2);

        switch (channels)
        {
            case 1:
                for (var i = 0; i < frames; i++)
                {
                    var v = TSample.ToFloat(src[i]);
                    dst[i * 2]     = v;
                    dst[i * 2 + 1] = v;
                }
                break;

            case 2:
                for (var i = 0; i < frames; i++)
                {
                    dst[i * 2]     = TSample.ToFloat(src[i * 2]);
                    dst[i * 2 + 1] = TSample.ToFloat(src[i * 2 + 1]);
                }
                break;

            default:
                // (front side + Σ extras) / (channels − 1).
                var inv = 1f / (channels - 1);
                for (var i = 0; i < frames; i++)
                {
                    var off    = i * channels;
                    var extras = 0f;
                    for (var c = 2; c < channels; c++) extras += TSample.ToFloat(src[off + c]);
                    dst[i * 2]     = (TSample.ToFloat(src[off])     + extras) * inv;
                    dst[i * 2 + 1] = (TSample.ToFloat(src[off + 1]) + extras) * inv;
                }
                break;
        }

        return frames;
    }

    /// <summary>
    /// Per-sample-type scaling to <c>[-1, 1]</c> float. Static abstract so the
    /// JIT specialises <see cref="FoldToStereo{T, TSample}"/> per format with no
    /// indirect call on the capture thread.
    /// </summary>
    private interface ISampleConverter<T> where T : unmanaged
    {
        static abstract float ToFloat(T sample);
    }

    private readonly struct Float32Sample : ISampleConverter<float>
    {
        public static float ToFloat(float sample) => sample;
    }

    private readonly struct Pcm16Sample : ISampleConverter<short>
    {
        public static float ToFloat(short sample) => sample * (1f / 32768f);
    }

    private readonly struct Pcm32Sample : ISampleConverter<int>
    {
        public static float ToFloat(int sample) => sample * (1f / 2147483648f);
    }
}
