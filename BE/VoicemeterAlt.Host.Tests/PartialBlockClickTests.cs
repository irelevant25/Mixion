using System.Collections.Immutable;
using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.State;
using Xunit;

namespace VoicemeterAlt.Host.Tests;

/// <summary>
/// Regression test for the click/distortion bug the user reported when
/// routing the mic to an output: when the capture ring had fewer than
/// <c>BlockFrames</c> samples available, the engine read what it could and
/// zero-filled the rest. The amplitude step at the partial→zero boundary
/// landed in the middle of the waveform every ~10 ms (one WASAPI capture
/// chunk), creating an audible buzz.
///
/// These tests model the same boundary discontinuity to demonstrate the
/// problem, and verify <see cref="MixEngine.MixBlock"/>'s output is clean
/// when callers feed it whole blocks (the contract the new <c>Tick()</c>
/// preserves). Updated for stereo (BE-080): we feed identical L/R input
/// and assert on the L side.
/// </summary>
public class PartialBlockClickTests
{
    private static MixerState Passthrough()
    {
        var inputs = ImmutableArray.Create(new Channel("in", "In", 0f, false, false));
        var outputs = ImmutableArray.Create(new Channel("out", "Out", 0f, false, false));
        var matrix = new RoutingMatrix(1, 1).With(0, 0, true);
        return new MixerState(inputs, outputs, matrix);
    }

    private record struct Buffers(
        float[][] InL, float[][] InR,
        float[][] OutL, float[][] OutR,
        float[] InGain, float[] OutGain);

    private static Buffers AllocBuffers()
    {
        return new Buffers(
            InL:    new[] { new float[MixEngine.BlockFrames] },
            InR:    new[] { new float[MixEngine.BlockFrames] },
            OutL:   new[] { new float[MixEngine.BlockFrames] },
            OutR:   new[] { new float[MixEngine.BlockFrames] },
            InGain: new[] { 1f },
            OutGain:new[] { 1f });
    }

    /// <summary>Maximum |Δsample| between consecutive samples in a block.</summary>
    private static float MaxJump(float[] block)
    {
        var max = 0f;
        for (var i = 1; i < block.Length; i++)
        {
            var j = MathF.Abs(block[i] - block[i - 1]);
            if (j > max) max = j;
        }
        return max;
    }

    [Fact]
    public void PartialReadWithZeroTail_LeavesAudibleStep()
    {
        var b = AllocBuffers();
        var state = Passthrough();

        // 1 kHz sine at 48 kHz: 48 samples per cycle. Fill 200 samples, zero the rest.
        for (var s = 0; s < b.InL[0].Length; s++) { b.InL[0][s] = 0f; b.InR[0][s] = 0f; }
        for (var s = 0; s < 200; s++)
        {
            var v = 0.8f * MathF.Sin(2 * MathF.PI * s / 48f);
            b.InL[0][s] = v;
            b.InR[0][s] = v;
        }

        var stepBefore = MathF.Abs(b.InL[0][200] - b.InL[0][199]);

        MixEngine.MixBlock(
            MixEngine.BlockFrames, state, b.InL, b.InR, b.OutL, b.OutR,
            b.InGain, b.OutGain,
            MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);

        var maxJumpOut = MaxJump(b.OutL[0]);
        Assert.True(stepBefore > 0.5f, "test setup should produce a real step");
        Assert.True(maxJumpOut > 0.5f, $"old behavior should preserve the step; got {maxJumpOut}");
    }

    [Fact]
    public void FullBlock_ProducesSmoothOutput()
    {
        var b = AllocBuffers();
        var state = Passthrough();

        for (var s = 0; s < b.InL[0].Length; s++)
        {
            var v = 0.8f * MathF.Sin(2 * MathF.PI * s / 48f);
            b.InL[0][s] = v;
            b.InR[0][s] = v;
        }

        MixEngine.MixBlock(
            MixEngine.BlockFrames, state, b.InL, b.InR, b.OutL, b.OutR,
            b.InGain, b.OutGain,
            MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);

        var maxJumpOut = MaxJump(b.OutL[0]);
        // 1 kHz sine at 48 kHz: max per-sample slope ≈ 0.8 * 2π * 1000 / 48000 ≈ 0.105.
        Assert.True(maxJumpOut < 0.15f, $"full-block output should be smooth; got max jump {maxJumpOut}");
    }
}
