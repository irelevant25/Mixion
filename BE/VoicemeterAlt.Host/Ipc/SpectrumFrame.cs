using System.Buffers.Binary;

namespace VoicemeterAlt.Host.Ipc;

/// <summary>
/// Binary spectrum-magnitude frame layout (little-endian; matches the SPA
/// decoder):
///
/// <code>
///   [u8 type='S'][u8 bus][u8 channel][u8 pad]
///   [u32 frameId][u32 binCount][u32 sampleRate]
///   [float32 magnitudeDb_0]…[float32 magnitudeDb_(binCount-1)]
/// </code>
///
/// Payload is <c>binCount × 4</c> bytes (one magnitude in dB per bin as
/// IEEE-754 binary32). Bin <c>k</c> corresponds to frequency
/// <c>k × sampleRate / fftSize</c> Hz where <c>fftSize = 2 × binCount</c>;
/// the decoder needs <see cref="SpectrumFrameHeader.SampleRate"/> to map
/// bins to Hz on the EQ canvas.
/// </summary>
public static class SpectrumFrame
{
    /// <summary>Header size in bytes (type + bus + channel + pad + frameId + binCount + sampleRate).</summary>
    public const int HeaderSize = 16;

    /// <summary>Bytes per bin (single magnitude in dB, float32).</summary>
    public const int BytesPerBin = 4;

    public static int SizeFor(int binCount)
    {
        if (binCount < 0) throw new ArgumentOutOfRangeException(nameof(binCount));
        return HeaderSize + binCount * BytesPerBin;
    }

    public enum BusTag : byte
    {
        Input  = 0,
        Output = 1,
    }

    public static void Pack(
        Span<byte> dst,
        uint frameId,
        BusTag bus,
        byte channel,
        int binCount,
        int sampleRate,
        ReadOnlySpan<float> magnitudesDb)
    {
        if (binCount < 0) throw new ArgumentOutOfRangeException(nameof(binCount));
        if (magnitudesDb.Length < binCount)
            throw new ArgumentException(
                $"magnitudesDb too short: need {binCount} floats, got {magnitudesDb.Length}.",
                nameof(magnitudesDb));
        var size = SizeFor(binCount);
        if (dst.Length < size)
            throw new ArgumentException(
                $"dst too small: need {size} bytes, got {dst.Length}.", nameof(dst));

        dst[0] = BinaryFrameType.Spectrum;
        dst[1] = (byte)bus;
        dst[2] = channel;
        dst[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4),  frameId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8),  (uint)binCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12), (uint)sampleRate);

        var off = HeaderSize;
        for (var i = 0; i < binCount; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(off), magnitudesDb[i]);
            off += sizeof(float);
        }
    }

    public static (SpectrumFrameHeader Header, float[] MagnitudesDb) Unpack(ReadOnlySpan<byte> src)
    {
        if (src.Length < HeaderSize)
            throw new ArgumentException("Frame shorter than header.", nameof(src));
        if (src[0] != BinaryFrameType.Spectrum)
            throw new ArgumentException(
                $"Expected spectrum frame (0x{BinaryFrameType.Spectrum:X2}); got 0x{src[0]:X2}.",
                nameof(src));

        var header = new SpectrumFrameHeader(
            Bus:        (BusTag)src[1],
            Channel:    src[2],
            FrameId:    BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4)),
            BinCount:   (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8)),
            SampleRate: (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12)));

        var expected = SizeFor(header.BinCount);
        if (src.Length < expected)
            throw new ArgumentException(
                $"Frame truncated: header claims {header.BinCount} bins ({expected} bytes) " +
                $"but buffer is {src.Length} bytes.", nameof(src));

        var mags = new float[header.BinCount];
        var off  = HeaderSize;
        for (var i = 0; i < mags.Length; i++)
        {
            mags[i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(off));
            off += sizeof(float);
        }
        return (header, mags);
    }
}

public sealed record SpectrumFrameHeader(
    SpectrumFrame.BusTag Bus,
    byte                 Channel,
    uint                 FrameId,
    int                  BinCount,
    int                  SampleRate);
