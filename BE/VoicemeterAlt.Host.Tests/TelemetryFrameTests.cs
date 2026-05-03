using VoicemeterAlt.Host.Ipc;
using Xunit;

namespace VoicemeterAlt.Host.Tests;

/// <summary>
/// BE-051: pack/unpack round-trip and on-the-wire layout. The SPA decoder
/// reads the same little-endian byte order, so the tests pin both — values
/// survive a round trip, and the byte-level layout matches the spec.
/// Updated for the BinaryFrameType prefix added with the spectrum-analyzer
/// frame; the meter frame's first byte is now <c>'M'</c>.
/// </summary>
public class TelemetryFrameTests
{
    [Fact]
    public void Round_trip_recovers_frame_id_channel_count_and_pairs()
    {
        var pairs = new[]
        {
            0.10f, 0.07f,   // channel 0: peak, rms
            0.95f, 0.55f,   // channel 1
            0.00f, 0.00f,   // channel 2: silent
            1.00f, 0.71f,   // channel 3: full-scale sine
        };
        const uint frameId = 0xDEADBEEF;
        const int  count   = 4;

        var buf = new byte[TelemetryFrame.SizeFor(count)];
        TelemetryFrame.Pack(buf, frameId, count, pairs);

        var (gotId, gotCount, gotPairs) = TelemetryFrame.Unpack(buf);

        Assert.Equal(frameId, gotId);
        Assert.Equal(count,   gotCount);
        Assert.Equal(pairs,   gotPairs);
    }

    [Fact]
    public void Layout_is_little_endian_and_in_documented_order()
    {
        Span<byte> buf = stackalloc byte[TelemetryFrame.SizeFor(1)];
        TelemetryFrame.Pack(buf, frameId: 0x01020304, channelCount: 1, pairs: new[] { 0.5f, 0.25f });

        // First byte is the frame-type discriminator: 'M' for meter.
        Assert.Equal(BinaryFrameType.Meter, buf[0]);
        Assert.Equal(0x00, buf[1]);
        Assert.Equal(0x00, buf[2]);
        Assert.Equal(0x00, buf[3]);

        // Second u32 (offset 4) is frameId, little-endian → 04 03 02 01.
        Assert.Equal(0x04, buf[4]);
        Assert.Equal(0x03, buf[5]);
        Assert.Equal(0x02, buf[6]);
        Assert.Equal(0x01, buf[7]);

        // Third u32 (offset 8) is channelCount = 1 → 01 00 00 00.
        Assert.Equal(0x01, buf[8]);
        Assert.Equal(0x00, buf[9]);
        Assert.Equal(0x00, buf[10]);
        Assert.Equal(0x00, buf[11]);

        // Floats are IEEE-754 binary32 little-endian.
        Assert.Equal(BitConverter.GetBytes(0.5f),  buf.Slice(12, 4).ToArray());
        Assert.Equal(BitConverter.GetBytes(0.25f), buf.Slice(16, 4).ToArray());
    }

    [Fact]
    public void Zero_channels_packs_just_the_header()
    {
        var size = TelemetryFrame.SizeFor(0);
        Assert.Equal(12, size);

        var buf = new byte[size];
        TelemetryFrame.Pack(buf, frameId: 7, channelCount: 0, pairs: ReadOnlySpan<float>.Empty);

        var (id, count, pairs) = TelemetryFrame.Unpack(buf);
        Assert.Equal(7u, id);
        Assert.Equal(0,  count);
        Assert.Empty(pairs);
    }

    [Fact]
    public void Pack_throws_when_destination_is_too_small()
    {
        var buf = new byte[8]; // header alone needs 12
        Assert.Throws<ArgumentException>(() =>
            TelemetryFrame.Pack(buf, 0, 1, new[] { 0.5f, 0.25f }));
    }

    [Fact]
    public void Pack_throws_when_pairs_buffer_is_short()
    {
        var buf = new byte[TelemetryFrame.SizeFor(2)];
        Assert.Throws<ArgumentException>(() =>
            TelemetryFrame.Pack(buf, 0, 2, new[] { 0.1f, 0.2f })); // only 1 pair, need 2
    }
}
