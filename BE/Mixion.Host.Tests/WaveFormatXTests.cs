using System.Runtime.InteropServices;
using Mixion.Host.Audio;
using NAudio.Wave;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Every capture source funnels its native samples through
/// <see cref="WaveFormatX.ConvertToStereo"/> — these pin down that the stereo
/// image survives it and that other layouts map to sensible L/R pairs.
/// </summary>
public class WaveFormatXTests
{
    private static byte[] Bytes(float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    private static byte[] Bytes(short[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

    [Fact]
    public void Stereo_KeepsLeftAndRightApart()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var dst = new float[4];

        var frames = WaveFormatX.ConvertToStereo(Bytes(new[] { 0.8f, 0f, 0f, -0.3f }), fmt, dst);

        Assert.Equal(2, frames);
        Assert.Equal(new[] { 0.8f, 0f, 0f, -0.3f }, dst);
    }

    [Fact]
    public void ExtensibleFloatMixFormat_KeepsLeftAndRightApart()
    {
        // Shared-mode mix formats are reported as WAVEFORMATEXTENSIBLE.
        var fmt = new WaveFormatExtensible(48_000, 32, 2);
        var dst = new float[2];

        WaveFormatX.ConvertToStereo(Bytes(new[] { 0.5f, 0f }), fmt, dst);

        Assert.Equal(new[] { 0.5f, 0f }, dst);
    }

    [Fact]
    public void Mono_IsDuplicatedToBothSides()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var dst = new float[4];

        var frames = WaveFormatX.ConvertToStereo(Bytes(new[] { 0.25f, -0.5f }), fmt, dst);

        Assert.Equal(2, frames);
        Assert.Equal(new[] { 0.25f, 0.25f, -0.5f, -0.5f }, dst);
    }

    [Fact]
    public void Pcm16_IsScaledToUnitRange()
    {
        var fmt = new WaveFormat(48_000, 16, 2);
        var dst = new float[2];

        WaveFormatX.ConvertToStereo(Bytes(new short[] { short.MinValue, 16384 }), fmt, dst);

        Assert.Equal(-1f, dst[0]);
        Assert.Equal(0.5f, dst[1]);
    }

    [Fact]
    public void Multichannel_FoldsExtraChannelsIntoBothSidesAtUnity()
    {
        var dst = new float[2];

        // 5.1 with the same signal everywhere keeps unity on both sides.
        WaveFormatX.ConvertToStereo(
            Bytes(new[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f }),
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 6),
            dst);
        Assert.Equal(0.5f, dst[0], 5);
        Assert.Equal(0.5f, dst[1], 5);

        // Content only on the front-left channel stays on the left.
        WaveFormatX.ConvertToStereo(
            Bytes(new[] { 1f, 0f, 0f, 0f }),
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 4),
            dst);
        Assert.True(dst[0] > 0f);
        Assert.Equal(0f, dst[1]);
    }

    [Fact]
    public void FramesThatDontFit_AreDropped()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var dst = new float[2]; // room for one frame

        var frames = WaveFormatX.ConvertToStereo(Bytes(new[] { 0.1f, 0.2f, 0.3f, 0.4f }), fmt, dst);

        Assert.Equal(1, frames);
        Assert.Equal(new[] { 0.1f, 0.2f }, dst);
    }
}
