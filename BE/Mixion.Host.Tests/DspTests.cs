using System.Collections.Immutable;
using Mixion.Host.Audio;
using Mixion.Host.Audio.Dsp;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Per-stage DSP unit tests (BE-079). Each test stays small and pointed:
///
/// - Biquad: a +6 dB peaking band at 1 kHz roughly doubles a 1 kHz sine.
/// - EQ:     bypass when state is null / disabled; gain change does not
///           click after the crossfade settles.
/// - Compressor: a sustained sine above threshold reduces level by ~the
///               static-curve formula; the GR readout is non-zero.
/// - Gate:   below close → attenuated; above open → passes; chatter near
///           threshold doesn't flap.
/// - Pan:    centre = 1/√2; extremes split clean.
/// - MixEngine: full chain leaves audible signal and zero allocations
///              once warmed up.
/// </summary>
public class DspTests
{
    private const int  Fs = 48_000;
    private const int  Block = MixEngine.BlockFrames;

    private static float[] Sine(int frames, float freq, float amplitude = 0.5f, int sampleRate = Fs)
    {
        var buf = new float[frames];
        var dPhi = 2.0 * Math.PI * freq / sampleRate;
        var phi = 0.0;
        for (var i = 0; i < frames; i++)
        {
            buf[i] = (float)(Math.Sin(phi) * amplitude);
            phi += dPhi;
            if (phi > 2.0 * Math.PI) phi -= 2.0 * Math.PI;
        }
        return buf;
    }

    private static float Peak(ReadOnlySpan<float> block)
    {
        var p = 0f;
        for (var i = 0; i < block.Length; i++)
        {
            var v = MathF.Abs(block[i]);
            if (v > p) p = v;
        }
        return p;
    }

    private static double Rms(ReadOnlySpan<float> block)
    {
        var sum = 0.0;
        for (var i = 0; i < block.Length; i++) sum += block[i] * block[i];
        return Math.Sqrt(sum / block.Length);
    }

    // --------------------------------------------------------------------
    // Biquad
    // --------------------------------------------------------------------

    [Fact]
    public void Biquad_PeakingPlus6dBAt1kHz_Doubles1kHzSine()
    {
        var biquad = new BiquadFilter();
        biquad.SetBand(EqBandType.Peaking, frequency: 1000f, gainDb: 6f, q: 1.0f, sampleRate: Fs);

        // Run 1 second of sine through, measure RMS from the latter half
        // (after filter settles).
        var samples = Sine(Fs, 1000f, amplitude: 0.5f);
        biquad.Process(samples);

        var settled = samples.AsSpan(Fs / 2);
        var rms = Rms(settled);
        var inputRms = 0.5f / Math.Sqrt(2.0);
        var ratio = rms / inputRms;

        // 10^(6/20) ≈ 1.995. Allow ±20% slack: filter settling and the
        // peaking shape's bandwidth touch 1 kHz only approximately.
        Assert.InRange(ratio, 1.6, 2.3);
    }

    [Fact]
    public void Biquad_PeakingZeroDb_IsNearUnity()
    {
        var biquad = new BiquadFilter();
        biquad.SetBand(EqBandType.Peaking, 1000f, 0f, 1f, Fs);

        var samples = Sine(Fs, 1000f, 0.5f);
        biquad.Process(samples);

        var rms = Rms(samples.AsSpan(Fs / 2));
        Assert.InRange(rms, 0.34, 0.36); // 0.5/sqrt(2)
    }

    [Fact]
    public void Biquad_LowPassAt500Hz_AttenuatesAt5kHz()
    {
        var biquad = new BiquadFilter();
        biquad.SetBand(EqBandType.LowPass, 500f, 0f, 0.707f, Fs);

        var samples = Sine(Fs, 5000f, 0.5f);
        biquad.Process(samples);

        var rms = Rms(samples.AsSpan(Fs / 2));
        // Two decades above cutoff for a 12 dB/oct LPF — well attenuated.
        Assert.True(rms < 0.05, $"5 kHz through 500 Hz LPF: rms={rms}");
    }

    // --------------------------------------------------------------------
    // Equalizer
    // --------------------------------------------------------------------

    [Fact]
    public void Equalizer_NullState_PassesThrough()
    {
        var eq = new Equalizer(Fs);
        eq.Apply(null);

        var samples = Sine(Block, 1000f, 0.5f);
        var copy = (float[])samples.Clone();
        eq.Process(samples);
        Assert.Equal(copy, samples);
    }

    [Fact]
    public void Equalizer_DisabledState_PassesThrough()
    {
        var eq = new Equalizer(Fs);
        eq.Apply(new EqState(false, new[] { new EqBand("b1", EqBandType.Peaking, 1000f, 12f, 1f) }));

        var samples = Sine(Block, 1000f, 0.5f);
        var copy = (float[])samples.Clone();
        eq.Process(samples);
        Assert.Equal(copy, samples);
    }

    [Fact]
    public void Equalizer_RapidGainSweep_HasNoClicks()
    {
        // Continuous slider drag: change gain by a small step every block.
        // After warmup the maximum sample-to-sample delta should stay
        // bounded — clicks would show up as a single huge jump.
        var eq = new Equalizer(Fs);

        var samples = new float[Block * 200];
        var phi = 0.0;
        var dPhi = 2.0 * Math.PI * 1000f / Fs;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(Math.Sin(phi) * 0.3);
            phi += dPhi;
        }

        // Warm up the filter cascade.
        eq.Apply(new EqState(true, new[] { new EqBand("b1", EqBandType.Peaking, 1000f, 0f, 1f) }));
        eq.Process(samples.AsSpan(0, Block));

        for (var b = 1; b < 100; b++)
        {
            var gainDb = (b % 10) * 0.5f;
            eq.Apply(new EqState(true, new[] { new EqBand("b1", EqBandType.Peaking, 1000f, gainDb, 1f) }));
            eq.Process(samples.AsSpan(b * Block, Block));
        }

        var maxDelta = 0f;
        for (var i = Block; i < 100 * Block; i++)
        {
            var d = MathF.Abs(samples[i] - samples[i - 1]);
            if (d > maxDelta) maxDelta = d;
        }
        // A 1 kHz sine at amp 0.3 has max sample-to-sample delta ≈
        // 0.3·sin(2π·1000/48000) ≈ 0.039. Allow generous slack for the EQ
        // resonance + the crossfade. A click would be > 0.5.
        Assert.True(maxDelta < 0.2f, $"Max sample delta {maxDelta} suggests a click.");
    }

    // --------------------------------------------------------------------
    // Compressor
    // --------------------------------------------------------------------

    [Fact]
    public void Compressor_AboveThreshold_ReducesAndReportsGr()
    {
        var comp = new Compressor(Fs);
        comp.Apply(new CompressorState(
            Enabled: true,
            ThresholdDb: -12f,
            Ratio: 4f,
            AttackMs: 1f,
            ReleaseMs: 50f,
            KneeDb: 0f, // hard knee for a clean closed-form prediction
            MakeupDb: 0f));

        // Drive a sine at -6 dB FS (amplitude ≈ 0.5). Peak input dB ≈ -6.
        // Steady-state envelope settles at the input level; overshoot ≈ 6
        // dB; reduction = 6 · (1 − 1/4) = 4.5 dB → linear ≈ 0.596.
        var samples = Sine(Fs, 1000f, 0.5f);
        comp.Process(samples);

        var settledRms = Rms(samples.AsSpan(Fs / 2));
        var unprocessedRms = 0.5 / Math.Sqrt(2.0);
        var ratio = settledRms / unprocessedRms;

        Assert.InRange(ratio, 0.45, 0.75); // ~ -4.5 dB ± slack
        Assert.True(comp.GainReductionDb > 0f, $"GR readout should be > 0; got {comp.GainReductionDb}");
    }

    [Fact]
    public void Compressor_BelowThreshold_PassesThrough()
    {
        var comp = new Compressor(Fs);
        comp.Apply(new CompressorState(
            Enabled: true,
            ThresholdDb: -10f,
            Ratio: 4f,
            AttackMs: 1f, ReleaseMs: 50f, KneeDb: 0f, MakeupDb: 0f));

        var samples = Sine(Fs, 1000f, 0.05f); // ~ -26 dB FS
        var copy = (float[])samples.Clone();
        comp.Process(samples);

        // Within a few percent of the unprocessed signal.
        var deltaRms = 0.0;
        for (var i = Fs / 2; i < Fs; i++) deltaRms += Math.Abs(samples[i] - copy[i]);
        deltaRms /= Fs / 2;
        Assert.True(deltaRms < 0.005, $"Below-threshold drift {deltaRms}");
    }

    [Fact]
    public void Compressor_NullOrDisabled_NoOp()
    {
        var comp = new Compressor(Fs);
        comp.Apply(null);
        var samples = Sine(Block, 1000f, 0.8f);
        var copy = (float[])samples.Clone();
        comp.Process(samples);
        Assert.Equal(copy, samples);
        Assert.Equal(0f, comp.GainReductionDb);
    }

    // --------------------------------------------------------------------
    // NoiseGate
    // --------------------------------------------------------------------

    [Fact]
    public void NoiseGate_BelowCloseThreshold_AttenuatesByRange()
    {
        var gate = new NoiseGate(Fs);
        gate.Apply(new GateState(
            Enabled: true,
            ThresholdDb: -40f,
            AttackMs: 1f,
            HoldMs: 0f,
            ReleaseMs: 5f,
            RangeDb: -40f));

        // -60 dB FS: amp ≈ 0.001 — well below the close threshold.
        var samples = Sine(Fs, 1000f, 0.001f);
        gate.Process(samples);

        var settledPeak = Peak(samples.AsSpan(Fs / 2));
        // Down by 40 dB from 0.001 = 0.00001. Ample slack:
        Assert.True(settledPeak < 5e-5f, $"Closed-gate peak {settledPeak}");
    }

    [Fact]
    public void NoiseGate_AboveOpenThreshold_PassesThrough()
    {
        var gate = new NoiseGate(Fs);
        gate.Apply(new GateState(
            Enabled: true,
            ThresholdDb: -40f,
            AttackMs: 1f, HoldMs: 0f, ReleaseMs: 5f, RangeDb: -40f));

        // -6 dB FS, well above the open threshold.
        var samples = Sine(Fs, 1000f, 0.5f);
        gate.Process(samples);

        var settledPeak = Peak(samples.AsSpan(Fs / 2));
        Assert.InRange(settledPeak, 0.45f, 0.51f);
    }

    [Fact]
    public void NoiseGate_HoversNearThreshold_DoesNotChatter()
    {
        // Sweep amplitude up across the close→open band, then a long
        // sustain just above open. The output amplitude must never reach
        // exactly zero after the gate has opened. (Chatter would show as
        // alternating zero / non-zero windows.)
        var gate = new NoiseGate(Fs);
        gate.Apply(new GateState(
            Enabled: true,
            ThresholdDb: -40f,
            AttackMs: 1f,
            HoldMs: 50f,
            ReleaseMs: 100f,
            RangeDb: -40f));

        // Open by sustaining a louder signal first.
        gate.Process(Sine(Fs / 4, 1000f, 0.3f));

        // Then hold just above the open threshold.
        var hovering = Sine(Fs / 2, 1000f, amplitude: 0.012f); // ≈ -38 dB
        gate.Process(hovering);

        // Look at last 100 ms — the gate must still be passing audio.
        var tail = hovering.AsSpan(hovering.Length - Fs / 10);
        Assert.True(Peak(tail) > 0.005f, $"Gate may have closed during hover: {Peak(tail)}");
    }

    // --------------------------------------------------------------------
    // Pan
    // --------------------------------------------------------------------

    [Fact]
    public void Pan_Centre_BothSidesAtInverseSqrt2()
    {
        Pan.Compute(0f, out var l, out var r);
        var inv = 1f / MathF.Sqrt(2f);
        Assert.InRange(l, inv - 1e-5f, inv + 1e-5f);
        Assert.InRange(r, inv - 1e-5f, inv + 1e-5f);
    }

    [Fact]
    public void Pan_HardLeft_LeftFullRightZero()
    {
        Pan.Compute(-1f, out var l, out var r);
        Assert.InRange(l, 0.999f, 1.001f);
        Assert.InRange(r, -1e-5f, 1e-5f);
    }

    [Fact]
    public void Pan_HardRight_RightFullLeftZero()
    {
        Pan.Compute(1f, out var l, out var r);
        Assert.InRange(r, 0.999f, 1.001f);
        Assert.InRange(l, -1e-5f, 1e-5f);
    }

    [Fact]
    public void Pan_OutOfRangeClamped()
    {
        Pan.Compute(2f, out var l, out var r);
        Assert.InRange(r, 0.999f, 1.001f);
        Pan.Compute(-2f, out l, out r);
        Assert.InRange(l, 0.999f, 1.001f);
    }

    [Fact]
    public void Pan_MonoGain_ConstantPowerCentreDip()
    {
        Assert.InRange(Pan.MonoGain(0f), 0.706f, 0.708f);
        Assert.InRange(Pan.MonoGain(1f), 0.999f, 1.001f);
        Assert.InRange(Pan.MonoGain(-1f), 0.999f, 1.001f);
    }

    // --------------------------------------------------------------------
    // Full chain wired through MixEngine.MixBlock + per-channel DSP shape
    // --------------------------------------------------------------------

    [Fact]
    public void FullChain_GateEqCompPan_LeavesSignal()
    {
        // Emulate one input + one output with a full DSP chain on each.
        // Drive a 1 kHz sine through and verify that audio still arrives
        // at the output at a sensible level — i.e. each stage individually
        // bypasses cleanly via null state, and an enabled stage doesn't
        // explode.
        var inputGate = new NoiseGate(Fs);
        var inputEq   = new Equalizer(Fs);
        var inputComp = new Compressor(Fs);

        inputGate.Apply(new GateState(true, -50f, 1f, 50f, 100f, -40f));
        inputEq.Apply(new EqState(true, new[]
        {
            new EqBand("b1", EqBandType.Peaking,   200f, 3f, 1f),
            new EqBand("b2", EqBandType.Peaking,  1000f, 0f, 1f),
            new EqBand("b3", EqBandType.Peaking,  3000f, -3f, 1f),
            new EqBand("b4", EqBandType.HighShelf, 8000f, 2f, 0.7f),
            new EqBand("b5", EqBandType.LowShelf,   80f, 1f, 0.7f),
        }));
        inputComp.Apply(new CompressorState(true, -18f, 4f, 5f, 80f, 6f, 3f));

        var samples = Sine(Fs, 1000f, 0.5f);
        inputGate.Process(samples);
        inputEq.Process(samples);
        inputComp.Process(samples);

        var rms = Rms(samples.AsSpan(Fs / 2));
        Assert.InRange(rms, 0.05, 1.0);
    }

    [Fact]
    public void FullChain_AllocationSmoke_NoGcOnHotPath()
    {
        // BE-079 / BE-027: no allocations once warmed up. We don't ship
        // BenchmarkDotNet — this smoke test uses
        // GC.GetAllocatedBytesForCurrentThread() before/after a pile of
        // ticks.
        var captures = new[] { new Channel("in0", "In", 0f, false, false,
            Pan: 0f,
            Gate: new GateState(true, -50f, 1f, 50f, 100f, -40f),
            Compressor: new CompressorState(true, -18f, 4f, 5f, 80f, 6f, 3f),
            Eq: new EqState(true, new[] {
                new EqBand("a", EqBandType.Peaking, 1000f, 3f, 1f),
                new EqBand("b", EqBandType.HighShelf, 6000f, 2f, 0.7f),
            })) };
        var renders = new[] { new Channel("out0", "Out", 0f, false, false,
            Pan: 0.3f,
            Compressor: new CompressorState(true, -10f, 2f, 5f, 80f, 0f, 0f),
            Eq: new EqState(true, new[] {
                new EqBand("c", EqBandType.Peaking, 800f, -3f, 1f),
            })) };

        var state = new MixerState(
            captures.ToImmutableArray(),
            renders.ToImmutableArray(),
            new RoutingMatrix(1, 1).With(0, 0, true));

        var inputBlocksMono = new[] { new float[Block] };
        var inputBlocksL = new[] { new float[Block] };
        var inputBlocksR = new[] { new float[Block] };
        var outputBlocksL = new[] { new float[Block] };
        var outputBlocksR = new[] { new float[Block] };
        var inGain  = new[] { 1f };
        var outGain = new[] { 1f };

        var inputGate = new NoiseGate(Fs);
        var inputEq   = new Equalizer(Fs);
        var inputComp = new Compressor(Fs);
        var outputEqL   = new Equalizer(Fs);
        var outputEqR   = new Equalizer(Fs);
        var outputCompL = new Compressor(Fs);
        var outputCompR = new Compressor(Fs);

        // Warm everything up — first calls JIT + lazy alloc the filters'
        // shadow buffers / coefficient arrays.
        inputGate.Apply(captures[0].Gate);
        inputEq.Apply(captures[0].Eq);
        inputComp.Apply(captures[0].Compressor);
        outputEqL.Apply(renders[0].Eq);
        outputEqR.Apply(renders[0].Eq);
        outputCompL.Apply(renders[0].Compressor);
        outputCompR.Apply(renders[0].Compressor);
        for (var w = 0; w < 200; w++) RunTick();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 0; t < 1000; t++) RunTick();
        var after  = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(after - before == 0, $"Hot-path allocations: {after - before} bytes over 1000 ticks");

        void RunTick()
        {
            for (var i = 0; i < Block; i++) inputBlocksMono[0][i] = 0.3f;
            inputGate.Process(inputBlocksMono[0]);
            inputEq.Process(inputBlocksMono[0]);
            inputComp.Process(inputBlocksMono[0]);
            // Centre pan for the smoke test → both sides see the mono signal at unity.
            for (var i = 0; i < Block; i++)
            {
                var v = inputBlocksMono[0][i];
                inputBlocksL[0][i] = v;
                inputBlocksR[0][i] = v;
            }
            MixEngine.MixBlock(Block, state, inputBlocksL, inputBlocksR,
                outputBlocksL, outputBlocksR,
                inGain, outGain, MixEngine.GainRampCoefficient, MixEngine.RampSnapEpsilon);
            outputEqL.Process(outputBlocksL[0]);
            outputEqR.Process(outputBlocksR[0]);
            outputCompL.Process(outputBlocksL[0]);
            outputCompR.Process(outputBlocksR[0]);
        }
    }

    [Fact]
    public void Compressor_GainReductionRamp_NoClickOnEnable()
    {
        // Toggle compressor on while audio is hot. The makeup-gain
        // crossfade (BE-078) should produce a smooth transition.
        var comp = new Compressor(Fs);
        var block1 = Sine(Block, 1000f, 0.3f);
        comp.Apply(null); // bypass
        comp.Process(block1);

        // Re-enable with +6 dB makeup; the next block should not jump
        // amplitude by 6 dB on sample 0.
        comp.Apply(new CompressorState(true, -60f, 1f, 1f, 50f, 0f, 6f));
        var block2 = Sine(Block, 1000f, 0.3f);
        comp.Process(block2);

        var maxDelta = 0f;
        for (var i = 1; i < block2.Length; i++)
        {
            var d = MathF.Abs(block2[i] - block2[i - 1]);
            if (d > maxDelta) maxDelta = d;
        }
        // A 1 kHz sine at 0.3 has max sample delta ≈ 0.039. A click would
        // be massive (> 0.4). Allow 3× slack for the ongoing ramp.
        Assert.True(maxDelta < 0.15f, $"Compressor enable click: max sample delta {maxDelta}");
    }
}
