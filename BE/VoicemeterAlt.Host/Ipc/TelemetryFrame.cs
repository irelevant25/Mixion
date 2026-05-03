using System.Buffers.Binary;

namespace VoicemeterAlt.Host.Ipc;

/// <summary>Discriminator for the first byte of every binary WebSocket frame.</summary>
public static class BinaryFrameType
{
    /// <summary>VU-meter frame, see <see cref="TelemetryFrame"/>.</summary>
    public const byte Meter    = 0x4D; // 'M'
    /// <summary>Spectrum-magnitude frame, see <see cref="SpectrumFrame"/>.</summary>
    public const byte Spectrum = 0x53; // 'S'
}

/// <summary>
/// Binary VU-meter frame layout (little-endian; matches the SPA decoder):
///
/// <code>
///   [u8 type='M'][u8 pad][u8 pad][u8 pad][u32 frameId][u32 channelCount][float32 peak0][float32 rms0]…
/// </code>
///
/// Headers are 12 bytes (1 type + 3 padding to align frameId on a 4-byte
/// boundary + 4 frameId + 4 channelCount); payload is
/// <c>channelCount × 8</c> bytes (one peak + one RMS per channel as
/// IEEE-754 binary32).
/// </summary>
public static class TelemetryFrame
{
    /// <summary>Header size in bytes (type + padding + frameId + channelCount).</summary>
    public const int HeaderSize = 12;

    /// <summary>Bytes per channel (peak + RMS, both float32).</summary>
    public const int BytesPerChannel = 8;

    /// <summary>Total frame size in bytes for the given channel count.</summary>
    public static int SizeFor(int channelCount)
    {
        if (channelCount < 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        return HeaderSize + channelCount * BytesPerChannel;
    }

    /// <summary>
    /// Pack a frame into <paramref name="dst"/>. <paramref name="pairs"/> is
    /// laid out <c>[peak0, rms0, peak1, rms1, …]</c> and must contain
    /// <c>channelCount × 2</c> floats.
    /// </summary>
    public static void Pack(Span<byte> dst, uint frameId, int channelCount, ReadOnlySpan<float> pairs)
    {
        if (channelCount < 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (pairs.Length < channelCount * 2)
            throw new ArgumentException(
                $"pairs too short: need {channelCount * 2} floats, got {pairs.Length}.", nameof(pairs));
        var size = SizeFor(channelCount);
        if (dst.Length < size)
            throw new ArgumentException(
                $"dst too small: need {size} bytes, got {dst.Length}.", nameof(dst));

        dst[0] = BinaryFrameType.Meter;
        dst[1] = 0; dst[2] = 0; dst[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4),  frameId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8),  (uint)channelCount);

        var off = HeaderSize;
        for (var i = 0; i < channelCount * 2; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(off), pairs[i]);
            off += sizeof(float);
        }
    }

    /// <summary>
    /// Unpack a frame from <paramref name="src"/> into a freshly allocated
    /// <c>(frameId, channelCount, pairs)</c> tuple. Used by tests; the live
    /// SPA path decodes inline.
    /// </summary>
    public static (uint FrameId, int ChannelCount, float[] Pairs) Unpack(ReadOnlySpan<byte> src)
    {
        if (src.Length < HeaderSize)
            throw new ArgumentException("Frame shorter than header.", nameof(src));
        if (src[0] != BinaryFrameType.Meter)
            throw new ArgumentException(
                $"Expected meter frame (0x{BinaryFrameType.Meter:X2}); got 0x{src[0]:X2}.",
                nameof(src));

        var frameId      = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4));
        var channelCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8));
        var expected     = SizeFor(channelCount);
        if (src.Length < expected)
            throw new ArgumentException(
                $"Frame truncated: header claims {channelCount} channels ({expected} bytes) " +
                $"but buffer is {src.Length} bytes.", nameof(src));

        var pairs = new float[channelCount * 2];
        var off   = HeaderSize;
        for (var i = 0; i < pairs.Length; i++)
        {
            pairs[i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(off));
            off += sizeof(float);
        }

        return (frameId, channelCount, pairs);
    }
}
