using Mixion.Host.Audio;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// <see cref="RingBuffer.Discard"/> — the consumer-side drop the mix loop uses
/// to shed a backlog without copying it out.
/// </summary>
public class RingBufferTests
{
    [Fact]
    public void Discard_DropsTheOldestSamples()
    {
        var ring = new RingBuffer(8);
        ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });

        Assert.Equal(4, ring.Discard(4));

        var rest = new float[4];
        Assert.Equal(2, ring.Read(rest));
        Assert.Equal(new float[] { 5, 6 }, rest[..2]);
    }

    [Fact]
    public void Discard_StopsAtWhatIsQueued()
    {
        var ring = new RingBuffer(8);
        ring.Write(new float[] { 1, 2 });

        Assert.Equal(2, ring.Discard(10));
        Assert.Equal(0, ring.Available);
        Assert.Equal(0, ring.Discard(2));
    }

    [Fact]
    public void Discard_KeepsWrappedDataIntact()
    {
        var ring = new RingBuffer(8);
        ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        ring.Read(new float[6]);                           // read position near the end of the array
        ring.Write(new float[] { 7, 8, 9, 10, 11, 12 });   // wraps around

        ring.Discard(3);

        var rest = new float[3];
        Assert.Equal(3, ring.Read(rest));
        Assert.Equal(new float[] { 10, 11, 12 }, rest);
    }
}
