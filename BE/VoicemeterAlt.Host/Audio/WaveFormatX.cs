using NAudio.Wave;

namespace VoicemeterAlt.Host.Audio;

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
    /// native sample type) to mono float samples in <paramref name="dstMono"/>.
    /// Returns the number of mono frames written.
    ///
    /// Shared by capture and loopback paths — both feed mono float rings.
    /// </summary>
    public static int ConvertToMono(ReadOnlySpan<byte> bytes, WaveFormat fmt, Span<float> dstMono)
    {
        var channels = fmt.Channels;
        if (channels <= 0) return 0;
        var inv = 1f / channels;

        if (IsFloat(fmt) && fmt.BitsPerSample == 32)
        {
            var src    = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
            var frames = src.Length / channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0f;
                var off = i * channels;
                for (var c = 0; c < channels; c++) sum += src[off + c];
                dstMono[i] = sum * inv;
            }
            return frames;
        }

        if (IsPcm(fmt) && fmt.BitsPerSample == 16)
        {
            var src    = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes);
            var frames = src.Length / channels;
            const float scale = 1f / 32768f;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0f;
                var off = i * channels;
                for (var c = 0; c < channels; c++) sum += src[off + c] * scale;
                dstMono[i] = sum * inv;
            }
            return frames;
        }

        if (IsPcm(fmt) && fmt.BitsPerSample == 32)
        {
            var src    = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            var frames = src.Length / channels;
            const float scale = 1f / int.MaxValue;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0f;
                var off = i * channels;
                for (var c = 0; c < channels; c++) sum += src[off + c] * scale;
                dstMono[i] = sum * inv;
            }
            return frames;
        }

        throw new NotSupportedException($"Unsupported capture format: {Describe(fmt)}");
    }
}
