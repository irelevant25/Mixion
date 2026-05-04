using Mixion.Host.Audio;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// BE-050: peak/RMS over a ~33 ms window. The classical sanity check is a
/// pure sine: RMS = peak / √2. We feed a few full sine cycles through the
/// aggregator at exactly the window length and verify the published pair.
/// </summary>
public class MeterAggregatorTests
{
    private const int SampleRate = 48_000;
    private const int WindowMs   = 33;

    private static float[] Sine(int samples, float amplitude, float freqHz)
    {
        var buf = new float[samples];
        var w = 2.0 * Math.PI * freqHz / SampleRate;
        for (var i = 0; i < samples; i++)
            buf[i] = amplitude * (float)Math.Sin(w * i);
        return buf;
    }

    [Fact]
    public void Sine_input_yields_rms_at_one_over_sqrt_two_of_peak()
    {
        var agg = new MeterAggregator(channelCount: 1, sampleRate: SampleRate, windowMs: WindowMs);
        // Use an integer number of cycles so the RMS isn't biased by a
        // partial last cycle. 1 kHz at 48 kHz with a 1584-sample window
        // gives 33 cycles per window — exact integer count.
        var window = SampleRate * WindowMs / 1000;
        var sine   = Sine(window, amplitude: 0.5f, freqHz: 1_000f);

        // Feed in several block-sized chunks (mimicking the live engine,
        // which calls Accumulate 256 frames at a time).
        const int block = 256;
        var consumed = 0;
        while (consumed + block <= sine.Length)
        {
            agg.Accumulate(0, sine.AsSpan(consumed, block));
            agg.OnBlockComplete(block);
            consumed += block;
        }
        // Pad the trailing remainder so the window publishes.
        var rem = sine.Length - consumed;
        if (rem > 0)
        {
            agg.Accumulate(0, sine.AsSpan(consumed, rem));
            agg.OnBlockComplete(rem);
        }

        Span<float> dst = stackalloc float[2];
        var frameId = agg.TrySnapshot(dst);

        // Peak is the analytical sine amplitude (sample grid won't quite
        // hit 0.5 — allow 1% tolerance).
        Assert.InRange(dst[0], 0.49f, 0.5005f);

        // RMS for a sine = peak / √2 ≈ 0.3536 at peak = 0.5.
        var expectedRms = 0.5f / MathF.Sqrt(2);
        Assert.InRange(dst[1], expectedRms - 0.01f, expectedRms + 0.01f);

        // Frame id moved off zero — i.e. the publisher actually fired.
        Assert.True(frameId >= 1);
    }

    [Fact]
    public void Silence_yields_zero_peak_and_rms()
    {
        var agg = new MeterAggregator(channelCount: 1, sampleRate: SampleRate, windowMs: WindowMs);
        var window = SampleRate * WindowMs / 1000;
        var silence = new float[window];

        agg.Accumulate(0, silence);
        agg.OnBlockComplete(silence.Length);

        Span<float> dst = stackalloc float[2];
        agg.TrySnapshot(dst);

        Assert.Equal(0f, dst[0]);
        Assert.Equal(0f, dst[1]);
    }

    [Fact]
    public void Multiple_channels_are_independent()
    {
        var agg = new MeterAggregator(channelCount: 3, sampleRate: SampleRate, windowMs: WindowMs);
        var window = SampleRate * WindowMs / 1000;

        var chanA = Sine(window, amplitude: 1.0f,  freqHz: 1_000f);
        var chanB = new float[window];                        // silent
        var chanC = Sine(window, amplitude: 0.25f, freqHz: 1_000f);

        agg.Accumulate(0, chanA);
        agg.Accumulate(1, chanB);
        agg.Accumulate(2, chanC);
        agg.OnBlockComplete(window);

        Span<float> dst = stackalloc float[6];
        agg.TrySnapshot(dst);

        Assert.InRange(dst[0], 0.99f, 1.001f);                   // peak A
        Assert.InRange(dst[1], (1f / MathF.Sqrt(2)) - 0.02f, (1f / MathF.Sqrt(2)) + 0.02f); // rms A
        Assert.Equal(0f, dst[2]);                                // peak B
        Assert.Equal(0f, dst[3]);                                // rms B
        Assert.InRange(dst[4], 0.249f, 0.2505f);                 // peak C
        Assert.InRange(dst[5], (0.25f / MathF.Sqrt(2)) - 0.01f, (0.25f / MathF.Sqrt(2)) + 0.01f); // rms C
    }

    [Fact]
    public void Frame_id_advances_once_per_published_window()
    {
        var agg = new MeterAggregator(channelCount: 1, sampleRate: SampleRate, windowMs: WindowMs);
        Span<float> dst = stackalloc float[2];

        // Before any window completes, the snapshot is all zeros and frame
        // id is 0.
        Assert.Equal(0u, agg.TrySnapshot(dst));

        var window = SampleRate * WindowMs / 1000;
        var quarter = window / 4;
        var block = new float[quarter];
        // Fill three quarter-windows: still below threshold, no publish.
        for (var i = 0; i < 3; i++)
        {
            agg.Accumulate(0, block);
            agg.OnBlockComplete(block.Length);
        }
        Assert.Equal(0u, agg.TrySnapshot(dst));

        // Final quarter completes the window — exactly one publish.
        agg.Accumulate(0, block);
        agg.OnBlockComplete(block.Length);
        Assert.Equal(1u, agg.TrySnapshot(dst));

        // Another full window → frame id 2.
        for (var i = 0; i < 4; i++)
        {
            agg.Accumulate(0, block);
            agg.OnBlockComplete(block.Length);
        }
        Assert.Equal(2u, agg.TrySnapshot(dst));
    }
}
