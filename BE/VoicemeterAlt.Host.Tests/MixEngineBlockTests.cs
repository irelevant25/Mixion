using System.Collections.Immutable;
using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.State;
using Xunit;

namespace VoicemeterAlt.Host.Tests;

/// <summary>
/// Block-level tests for <see cref="MixEngine.MixBlock"/>. We construct the
/// same buffer layout the live engine uses, fill the input block with a
/// known signal, run one or more blocks, and inspect the output. After
/// BE-080 the bus is stereo: each side is asserted independently. The
/// passthrough tests use centre-pan inputs (gL = gR = 1 — see
/// <see cref="Pan.ComputeBalance"/>) so L and R should be identical.
/// </summary>
public class MixEngineBlockTests
{
    private static MixerState Passthrough(float inGainDb = 0f, float outGainDb = 0f, bool inMuted = false, bool outMuted = false)
    {
        var inputs = ImmutableArray.Create(
            new Channel("in0", "In 0", inGainDb, inMuted, false));
        var outputs = ImmutableArray.Create(
            new Channel("out0", "Out 0", outGainDb, outMuted, false));
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

    private static void Fill(float[] block, float value)
    {
        for (var i = 0; i < block.Length; i++) block[i] = value;
    }

    private static float Peak(float[] block)
    {
        var p = 0f;
        for (var i = 0; i < block.Length; i++)
        {
            var v = MathF.Abs(block[i]);
            if (v > p) p = v;
        }
        return p;
    }

    /// <summary>Run <paramref name="blocks"/> blocks of the same input through MixBlock; return final output peak (L side).</summary>
    private static float RunBlocks(MixerState state, float inputAmplitude, int blocks)
    {
        var b = AllocBuffers();
        var lastPeak = 0f;
        for (var i = 0; i < blocks; i++)
        {
            Fill(b.InL[0], inputAmplitude);
            Fill(b.InR[0], inputAmplitude);
            MixEngine.MixBlock(
                MixEngine.BlockFrames, state, b.InL, b.InR, b.OutL, b.OutR,
                b.InGain, b.OutGain,
                MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);
            lastPeak = Peak(b.OutL[0]);
        }
        return lastPeak;
    }

    [Fact]
    public void Passthrough_UnityGain_OutputMatchesInput()
    {
        var peak = RunBlocks(Passthrough(), inputAmplitude: 0.5f, blocks: 4);
        Assert.InRange(peak, 0.49f, 0.51f);
    }

    [Fact]
    public void OutputMuted_AfterRampConverges_OutputIsSilent()
    {
        var peak = RunBlocks(Passthrough(outMuted: true), inputAmplitude: 0.5f, blocks: 30);
        Assert.True(peak < 1e-3f, $"Expected near-silence after ramp; peak={peak}");
    }

    [Fact]
    public void OutputMuted_FinalBlockIsHardZero()
    {
        var b = AllocBuffers();
        var state = Passthrough(outMuted: true);
        for (var i = 0; i < 60; i++)
        {
            Fill(b.InL[0], 0.5f);
            Fill(b.InR[0], 0.5f);
            MixEngine.MixBlock(
                MixEngine.BlockFrames, state, b.InL, b.InR, b.OutL, b.OutR,
                b.InGain, b.OutGain,
                MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);
        }
        Assert.Equal(0f, Peak(b.OutL[0]));
        Assert.Equal(0f, Peak(b.OutR[0]));
    }

    [Fact]
    public void OutputGainNegative6Db_OutputAtHalfAmplitude()
    {
        var peak = RunBlocks(Passthrough(outGainDb: -6f), inputAmplitude: 1f, blocks: 50);
        // 10^(-6/20) ≈ 0.501
        Assert.InRange(peak, 0.49f, 0.52f);
    }

    [Fact]
    public void OutputGainNegative20Db_OutputAt10Percent()
    {
        var peak = RunBlocks(Passthrough(outGainDb: -20f), inputAmplitude: 1f, blocks: 50);
        // 10^(-20/20) = 0.1
        Assert.InRange(peak, 0.09f, 0.11f);
    }

    [Fact]
    public void InputMuted_OutputIsSilentAfterRamp()
    {
        var peak = RunBlocks(Passthrough(inMuted: true), inputAmplitude: 0.5f, blocks: 60);
        Assert.True(peak < 1e-3f, $"Expected near-silence; peak={peak}");
    }

    [Fact]
    public void OutputUnmuteAfterMute_RecoversToFullAmplitude()
    {
        var b = AllocBuffers();
        var muted   = Passthrough(outMuted: true);
        var unmuted = Passthrough(outMuted: false);

        for (var i = 0; i < 60; i++)
        {
            Fill(b.InL[0], 0.5f);
            Fill(b.InR[0], 0.5f);
            MixEngine.MixBlock(
                MixEngine.BlockFrames, muted, b.InL, b.InR, b.OutL, b.OutR,
                b.InGain, b.OutGain,
                MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);
        }
        Assert.Equal(0f, Peak(b.OutL[0]));

        var lastPeak = 0f;
        for (var i = 0; i < 60; i++)
        {
            Fill(b.InL[0], 0.5f);
            Fill(b.InR[0], 0.5f);
            MixEngine.MixBlock(
                MixEngine.BlockFrames, unmuted, b.InL, b.InR, b.OutL, b.OutR,
                b.InGain, b.OutGain,
                MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);
            lastPeak = Peak(b.OutL[0]);
        }
        Assert.InRange(lastPeak, 0.49f, 0.51f);
    }

    [Fact]
    public void GainRampDoesNotIntroduceArtifactsOnSteadyState()
    {
        var b = AllocBuffers();
        var state = Passthrough(outGainDb: -6f);
        for (var i = 0; i < 40; i++)
        {
            Fill(b.InL[0], 0.5f);
            Fill(b.InR[0], 0.5f);
            MixEngine.MixBlock(
                MixEngine.BlockFrames, state, b.InL, b.InR, b.OutL, b.OutR,
                b.InGain, b.OutGain,
                MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);
        }
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var s = 0; s < b.OutL[0].Length; s++)
        {
            var v = b.OutL[0][s];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        Assert.True(max - min < 1e-5f, $"Steady-state should be flat; ripple={max - min}");
    }
}
