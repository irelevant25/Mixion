using Microsoft.Extensions.Logging;
using Mixion.Host.Audio.Dsp;
using Mixion.Host.Interop;
using Mixion.Host.Ipc;
using Mixion.Host.State;

namespace Mixion.Host.Audio;

/// <summary>
/// Configuration for the spectrum tap. <see cref="Bus"/> picks the ring
/// the audio thread reads from; <see cref="Channel"/> selects which
/// position within that bus. Stored as a single record on the engine and
/// exchanged atomically — the audio thread observes a coherent pair on
/// every tick.
/// </summary>
public sealed record SpectrumTapConfig(SpectrumFrame.BusTag Bus, int Channel);

/// <summary>
/// The single mix thread. Owns capture and render devices, keeps the current
/// <see cref="MixerState"/> in a slot updated atomically by control RPCs,
/// and runs the inner loop that reads input ring buffers, applies the
/// per-input mono DSP chain (<c>gate → EQ → compressor</c>), splits each
/// input into a stereo (L/R) bus via its pan, applies the routing matrix +
/// per-channel gain/mute/solo into stereo output buses, runs the per-output
/// stereo DSP chain (<c>EQ → compressor → balance pan</c>) and writes
/// interleaved L/R pairs into each render's ring buffer (BE-076 / BE-080).
///
/// The audio path never allocates and never locks. State swaps publish via
/// <see cref="Interlocked.Exchange{T}"/>; the loop reads them with
/// <see cref="Volatile.Read{T}"/>.
/// </summary>
public sealed class MixEngine : IDisposable
{
    /// <summary>
    /// Default mono frames processed per mix tick when no devices report a
    /// granted period. 128 frames ≈ 2.7 ms @ 48 kHz — matches the typical
    /// shared-mode WASAPI engine period on Windows 10+.
    /// Real engine instances pick their <see cref="BlockFrames"/> from the
    /// minimum granted period across active devices, clamped to
    /// [<see cref="MinBlockFrames"/>, <see cref="MaxBlockFrames"/>].
    /// </summary>
    public const int DefaultBlockFrames = 128;

    /// <summary>Hard floor on the resolved block size — going below ~32 frames is mostly per-tick overhead with no audible win.</summary>
    public const int MinBlockFrames = 32;

    /// <summary>Hard ceiling — even slow virtual cables shouldn't push us past ~10 ms of mix-block latency.</summary>
    public const int MaxBlockFrames = 512;

    /// <summary>
    /// Resolved mono frames processed per mix tick. Derived in the
    /// constructor from the minimum granted period across active capture
    /// and render devices, clamped to
    /// [<see cref="MinBlockFrames"/>, <see cref="MaxBlockFrames"/>].
    /// Aligning with the slowest device's period keeps the mix tick from
    /// straddling two device periods (which would add "always one period
    /// behind" latency on the render side).
    /// </summary>
    public int BlockFrames { get; }

    /// <summary>
    /// Per-sample one-pole coefficient for the gain smoother. Picked so that
    /// the smoother reaches ~63% of a target step within ~5 ms at 48 kHz
    /// (≈240 samples). Fast enough to feel responsive on a slider, slow
    /// enough to keep zipper noise away. Tweakable; the engine works for any
    /// value in (0, 1] (1 = no ramp / instant).
    /// </summary>
    public const float GainRampCoefficient = 1f / 240f;

    /// <summary>
    /// Linear-gain delta below which a channel is treated as steady — no
    /// per-sample smoothing, just a constant multiply. Avoids burning cycles
    /// on a smoother that has nothing to do.
    /// </summary>
    public const float RampSnapEpsilon = 1e-6f;

    /// <summary>Length of the pan-gain crossfade in samples (~10 ms @ 48 kHz).</summary>
    public const int PanFadeSamples = 480;

    private readonly ILogger                _logger;
    // Capture and render arrays are nullable because the engine carries one
    // slot per channel in MixerState even when the underlying device or
    // process has gone missing. A null slot means "the channel exists in
    // state but has no backing source right now" — its block is treated as
    // silence by the mix loop. This is what lets the FE render a red,
    // greyed-out strip for a slot whose device the user unplugged or whose
    // app was closed, without losing the slot binding.
    private readonly IAudioCaptureSource?[] _captures;
    private readonly IRenderDevice?[]        _renders;
    private readonly LoopbackCapture?[]  _renderLoopbacks; // parallel to _renders; held open but no longer used for metering.

    /// <summary>Per-input mono block straight off the capture ring (post DSP, pre pan).</summary>
    private readonly float[][]           _inputBlocksMono;
    /// <summary>Per-input post-pan L block.</summary>
    private readonly float[][]           _inputBlocksL;
    /// <summary>Per-input post-pan R block.</summary>
    private readonly float[][]           _inputBlocksR;

    /// <summary>Per-output post-mix L block.</summary>
    private readonly float[][]           _outputBlocksL;
    /// <summary>Per-output post-mix R block.</summary>
    private readonly float[][]           _outputBlocksR;
    /// <summary>Per-output interleaved L,R scratch — what we write to the render ring each tick.</summary>
    private readonly float[][]           _renderInterleaved;

    /// <summary>Current (smoothed) per-input linear gain. Allocated once.</summary>
    private readonly float[]             _inputCurrentGain;
    /// <summary>Current (smoothed) per-output linear gain. Allocated once.</summary>
    private readonly float[]             _outputCurrentGain;

    /// <summary>Smoothed per-input pan: current/target gain on each side.</summary>
    private readonly float[]             _inputPanCurrentL;
    private readonly float[]             _inputPanCurrentR;
    private readonly float[]             _inputPanTargetL;
    private readonly float[]             _inputPanTargetR;

    private readonly MeterAggregator     _meters;

    // BE-076: per-channel DSP processors.
    // Inputs are mono — single processor each.
    private readonly NoiseGate[]   _inputGates;
    private readonly Equalizer[]   _inputEqs;
    private readonly Compressor[]  _inputComps;
    private readonly Channel?[]    _lastAppliedInput;

    // Outputs are stereo — paired processors per side share coefficients but
    // keep independent delay state so each side is filtered correctly.
    private readonly Equalizer[]   _outputEqsL;
    private readonly Equalizer[]   _outputEqsR;
    private readonly Compressor[]  _outputCompsL;
    private readonly Compressor[]  _outputCompsR;
    private readonly Channel?[]    _lastAppliedOutput;
    /// <summary>Smoothed per-output balance gains, per side.</summary>
    private readonly float[]       _outputPanCurrentL;
    private readonly float[]       _outputPanCurrentR;
    private readonly float[]       _outputPanTargetL;
    private readonly float[]       _outputPanTargetR;

    // BE-081: spectrum tap. Single global tap, last subscriber wins.
    // _spectrumBuffer is a SPSC ring — the audio thread is the sole writer,
    // SpectrumBroadcaster the sole reader. Sized to comfortably hold a
    // single FFT window plus jitter (~85 ms at 48 kHz).
    private SpectrumTapConfig? _spectrumTap;
    private readonly RingBuffer _spectrumBuffer = new(SpectrumAnalyzer.FftSize * 4);

    private MixerState  _state;
    private Thread?     _thread;
    private volatile bool _running;

    /// <summary>
    /// Set by every capture's <see cref="IAudioCaptureSource.DataReady"/>
    /// handler whenever a fresh block lands in the ring buffer; waited on by
    /// the mix loop in place of a 1 ms polling sleep. AutoReset = the loop
    /// consumes one signal per wake; if multiple captures fire between wakes
    /// we still only wake once (which is fine — Tick checks all rings).
    /// A 2 ms timeout backstops the wait so a missed signal can never
    /// stall the loop indefinitely.
    /// </summary>
    private readonly AutoResetEvent _captureWake = new(initialState: false);

    /// <summary>
    /// Optional one-shot impulse measurement probe. When non-null, the mix
    /// loop replaces the chosen output's block with a test burst and copies
    /// the chosen input's raw capture block into the probe's watch buffer
    /// — <see cref="MeasureRouteLatency"/> handler awaits + decodes the
    /// result. Atomic exchange so the audio thread observes one coherent
    /// reference per tick.
    /// </summary>
    private LatencyProbe? _probe;

    public int SampleRate { get; }
    public IReadOnlyList<IAudioCaptureSource?> Captures => _captures;
    public IReadOnlyList<IRenderDevice?>        Renders  => _renders;

    /// <summary>
    /// Per-channel-side peak / RMS aggregator. Layout is
    /// <c>[in0_L, in0_R, in1_L, in1_R, …, out0_L, out0_R, out1_L, out1_R, …]</c>;
    /// inputs are mono so L and R slots receive the same block. Size:
    /// <c>2 * (captures + renders)</c>.
    /// </summary>
    public MeterAggregator Meters => _meters;

    /// <summary>Number of input meter pairs (L + R per capture). Convenience for FE indexing.</summary>
    public int InputMeterChannelCount  => _captures.Length * 2;
    /// <summary>Number of output meter pairs (L + R per render).</summary>
    public int OutputMeterChannelCount => _renders.Length * 2;

    /// <summary>Current spectrum tap configuration; <c>null</c> when no client is subscribed.</summary>
    public SpectrumTapConfig? SpectrumTap
    {
        get => Volatile.Read(ref _spectrumTap);
    }

    /// <summary>Set or clear the spectrum tap. Clears the tap buffer on every transition so a re-subscribe never leaks samples from the previous channel.</summary>
    public void SetSpectrumTap(SpectrumTapConfig? config)
    {
        var prev = Interlocked.Exchange(ref _spectrumTap, config);
        if (!Equals(prev, config)) _spectrumBuffer.Clear();
    }

    /// <summary>SPSC ring of recent samples from the configured tap point. Reader is the spectrum broadcaster.</summary>
    public RingBuffer SpectrumBuffer => _spectrumBuffer;

    /// <summary>
    /// Install (or clear) the round-trip latency probe. Pass <c>null</c> to
    /// abort an in-flight measurement. The audio thread picks up the new
    /// reference on its next tick. Caller is responsible for awaiting
    /// <see cref="LatencyProbe.Completion"/> and clearing the probe when
    /// done — typically from <c>measureRouteLatency</c> in
    /// <c>LatencyHandlers</c>.
    /// </summary>
    public void SetMeasurementProbe(LatencyProbe? probe)
    {
        Interlocked.Exchange(ref _probe, probe);
    }

    /// <summary>
    /// Last-block gain reduction (dB, ≥ 0) per input compressor. Inputs are
    /// mono so a single GR per channel.
    /// </summary>
    public float GetInputCompressorGainReductionDb(int input)
        => (uint)input < (uint)_inputComps.Length ? _inputComps[input].GainReductionDb : 0f;

    /// <summary>
    /// Output GR — max of L/R sides. The compressors run independently per
    /// side; we surface the worse-case value so the FE meter follows the
    /// loudest moment of the bus.
    /// </summary>
    public float GetOutputCompressorGainReductionDb(int output)
    {
        if ((uint)output >= (uint)_outputCompsL.Length) return 0f;
        var l = _outputCompsL[output].GainReductionDb;
        var r = _outputCompsR[output].GainReductionDb;
        return l > r ? l : r;
    }

    public MixEngine(
        IEnumerable<IAudioCaptureSource?> captures,
        IEnumerable<IRenderDevice?>        renders,
        MixerState                        initialState,
        ILogger                           logger,
        IEnumerable<LoopbackCapture?>?    renderLoopbacks = null)
    {
        _logger   = logger;
        _captures = captures.ToArray();
        _renders  = renders.ToArray();
        _state    = initialState;

        _renderLoopbacks = renderLoopbacks?.ToArray() ?? new LoopbackCapture?[_renders.Length];
        if (_renderLoopbacks.Length != _renders.Length)
            throw new ArgumentException(
                $"renderLoopbacks length {_renderLoopbacks.Length} must match renders length {_renders.Length}.",
                nameof(renderLoopbacks));

        if (_captures.Length == 0)
            throw new InvalidOperationException("MixEngine requires at least one capture slot.");
        if (_renders.Length == 0)
            throw new InvalidOperationException("MixEngine requires at least one render slot.");

        // BE-028: reject mismatched sample rates up front. Null slots
        // (channels in state without a backing device — e.g. an unplugged
        // mic the user wants to keep visible until they replug it) are
        // skipped; their rate is inherited from whichever real device
        // anchors the engine.
        var firstReal = FirstReal(_captures, _renders)
            ?? throw new InvalidOperationException(
                "MixEngine requires at least one active capture or render device.");
        var rate = firstReal.SampleRate;
        foreach (var c in _captures)
        {
            if (c is null) continue;
            if (c.SampleRate != rate)
                throw new InvalidOperationException(
                    $"Sample-rate mismatch: capture device '{c.FriendlyName}' is {c.SampleRate} Hz " +
                    $"but engine rate is {rate} Hz. Match the device formats in Windows sound settings " +
                    "(e.g. set both to 48 000 Hz) and restart.");
        }
        foreach (var r in _renders)
        {
            if (r is null) continue;
            if (r.SampleRate != rate)
                throw new InvalidOperationException(
                    $"Sample-rate mismatch: render device '{r.FriendlyName}' is {r.SampleRate} Hz " +
                    $"but capture is {rate} Hz. Match the device formats in Windows sound settings.");
        }
        SampleRate = rate;
        BlockFrames = ResolveBlockFrames(_captures, _renders);
        _logger.LogInformation(
            "MixEngine block size: {BlockFrames} frames (~{Ms} ms @ {Rate} Hz)",
            BlockFrames, (BlockFrames * 1000.0 / rate).ToString("F2"), rate);

        // BE-027 / BE-080: pre-allocate per-input and per-output scratch
        // blocks. The mix loop never touches the GC after this point.
        _inputBlocksMono = new float[_captures.Length][];
        _inputBlocksL    = new float[_captures.Length][];
        _inputBlocksR    = new float[_captures.Length][];
        for (var i = 0; i < _captures.Length; i++)
        {
            _inputBlocksMono[i] = new float[BlockFrames];
            _inputBlocksL[i]    = new float[BlockFrames];
            _inputBlocksR[i]    = new float[BlockFrames];
        }

        _outputBlocksL     = new float[_renders.Length][];
        _outputBlocksR     = new float[_renders.Length][];
        _renderInterleaved = new float[_renders.Length][];
        for (var o = 0; o < _renders.Length; o++)
        {
            _outputBlocksL[o]     = new float[BlockFrames];
            _outputBlocksR[o]     = new float[BlockFrames];
            _renderInterleaved[o] = new float[BlockFrames * 2];
        }

        // BE-044: pre-allocate gain smoothers, seed them at the initial
        // target so we don't ramp from silence on first tick.
        _inputCurrentGain  = new float[_captures.Length];
        _inputPanCurrentL  = new float[_captures.Length];
        _inputPanCurrentR  = new float[_captures.Length];
        _inputPanTargetL   = new float[_captures.Length];
        _inputPanTargetR   = new float[_captures.Length];
        for (var i = 0; i < _captures.Length; i++)
        {
            _inputCurrentGain[i] = i < initialState.Inputs.Length ? initialState.Inputs[i].GainLinear : 1f;
            var p = i < initialState.Inputs.Length ? initialState.Inputs[i].Pan : 0f;
            Pan.ComputeBalance(p, out var gL, out var gR);
            _inputPanCurrentL[i] = gL;
            _inputPanCurrentR[i] = gR;
            _inputPanTargetL[i]  = gL;
            _inputPanTargetR[i]  = gR;
        }

        _outputCurrentGain = new float[_renders.Length];
        _outputPanCurrentL = new float[_renders.Length];
        _outputPanCurrentR = new float[_renders.Length];
        _outputPanTargetL  = new float[_renders.Length];
        _outputPanTargetR  = new float[_renders.Length];
        for (var o = 0; o < _renders.Length; o++)
        {
            _outputCurrentGain[o] = o < initialState.Outputs.Length ? initialState.Outputs[o].GainLinear : 1f;
            var p = o < initialState.Outputs.Length ? initialState.Outputs[o].Pan : 0f;
            Pan.ComputeBalance(p, out var gL, out var gR);
            _outputPanCurrentL[o] = gL;
            _outputPanCurrentR[o] = gR;
            _outputPanTargetL[o]  = gL;
            _outputPanTargetR[o]  = gR;
        }

        // BE-076: per-channel DSP. Filters are instantiated even if the
        // corresponding state is null — Apply(null) just keeps them in
        // bypass and Process is a fast no-op.
        _inputGates  = new NoiseGate[_captures.Length];
        _inputEqs    = new Equalizer[_captures.Length];
        _inputComps  = new Compressor[_captures.Length];
        _lastAppliedInput = new Channel?[_captures.Length];
        for (var i = 0; i < _captures.Length; i++)
        {
            _inputGates[i] = new NoiseGate(rate);
            _inputEqs[i]   = new Equalizer(rate);
            _inputComps[i] = new Compressor(rate);
        }

        _outputEqsL    = new Equalizer[_renders.Length];
        _outputEqsR    = new Equalizer[_renders.Length];
        _outputCompsL  = new Compressor[_renders.Length];
        _outputCompsR  = new Compressor[_renders.Length];
        _lastAppliedOutput = new Channel?[_renders.Length];
        for (var o = 0; o < _renders.Length; o++)
        {
            _outputEqsL[o]   = new Equalizer(rate);
            _outputEqsR[o]   = new Equalizer(rate);
            _outputCompsL[o] = new Compressor(rate);
            _outputCompsR[o] = new Compressor(rate);
        }

        // BE-050 / BE-080: per-channel-side meters. Each capture takes 2
        // slots (mono → both sides equal); each render takes 2 slots
        // (actual L and R post-mix). 33 ms window → ~30 Hz publish.
        _meters = new MeterAggregator((_captures.Length + _renders.Length) * 2, SampleRate);

        // Event-driven mix wake: every capture pings _captureWake the moment
        // it appends data, so the mix loop runs as soon as a block is
        // available instead of polling on a 1 ms timer (which is actually
        // 1–15 ms depending on the host timer resolution).
        foreach (var c in _captures)
        {
            if (c is null) continue;
            c.DataReady += OnCaptureDataReady;
        }
    }

    private void OnCaptureDataReady(object? sender, EventArgs e)
    {
        _captureWake.Set();
    }

    /// <summary>
    /// First non-null device across captures + renders. Used to anchor the
    /// engine sample rate when some slots are placeholders for missing
    /// devices.
    /// </summary>
    private static IAudioDevice? FirstReal(IAudioCaptureSource?[] captures, IRenderDevice?[] renders)
    {
        foreach (var c in captures) if (c is not null) return new CaptureAdapter(c);
        foreach (var r in renders)  if (r is not null) return new RenderAdapter(r);
        return null;
    }

    /// <summary>
    /// Pick the mix block size from the minimum granted period across all
    /// active devices. Going smaller than the slowest device's period would
    /// just spin extra ticks on starved rings; going larger straddles
    /// device periods and adds latency. Clamped so a misbehaving device
    /// reporting a tiny or huge value can't poison the engine.
    /// </summary>
    private static int ResolveBlockFrames(IAudioCaptureSource?[] captures, IRenderDevice?[] renders)
    {
        var min = int.MaxValue;
        foreach (var c in captures)
        {
            if (c is null) continue;
            if (c.BufferFrames > 0 && c.BufferFrames < min) min = c.BufferFrames;
        }
        foreach (var r in renders)
        {
            if (r is null) continue;
            if (r.BufferFrames > 0 && r.BufferFrames < min) min = r.BufferFrames;
        }
        if (min == int.MaxValue) return DefaultBlockFrames;
        return Math.Clamp(min, MinBlockFrames, MaxBlockFrames);
    }

    private interface IAudioDevice { int SampleRate { get; } }
    private sealed record CaptureAdapter(IAudioCaptureSource Inner) : IAudioDevice
    {
        public int SampleRate => Inner.SampleRate;
    }
    private sealed record RenderAdapter(IRenderDevice Inner) : IAudioDevice
    {
        public int SampleRate => Inner.SampleRate;
    }

    /// <summary>
    /// Replace the current mixer state. Called from Kestrel control threads;
    /// the audio thread observes the new instance on its next iteration.
    /// </summary>
    public void PublishState(MixerState next)
    {
        Interlocked.Exchange(ref _state, next);
    }

    public MixerState SnapshotState() => Volatile.Read(ref _state);

    public void Start()
    {
        if (_running) return;

        // Track everything we successfully start so we can roll it back
        // if a later step throws. Without this, a failure on (say) the
        // loopback meters would leak captures + renders that already had
        // their WASAPI clients running — including any that happen to be
        // holding an exclusive-mode lock on a physical device.
        var startedCaptures = new List<IAudioCaptureSource>();
        var startedRenders  = new List<IRenderDevice>();
        var startedLoopbacks = new List<LoopbackCapture>();

        try
        {
            foreach (var c in _captures)
            {
                if (c is null) continue;
                c.Start();
                startedCaptures.Add(c);
            }
            foreach (var r in _renders)
            {
                if (r is null) continue;
                r.Start();
                startedRenders.Add(r);
            }
            foreach (var l in _renderLoopbacks)
            {
                if (l is null) continue;
                l.Start();
                startedLoopbacks.Add(l);
            }
        }
        catch
        {
            // Roll back in reverse order. Stop first, then dispose — Stop
            // releases the device cleanly, Dispose drops the COM refs.
            // Each call is wrapped because a device that's already in a
            // bad state may throw on Stop too, and we still need to drain
            // the rest.
            foreach (var l in startedLoopbacks) try { l.Stop();   } catch { /* best effort */ }
            foreach (var r in startedRenders)  try { r.Stop();    } catch { /* best effort */ }
            foreach (var c in startedCaptures) try { c.Stop();    } catch { /* best effort */ }
            foreach (var l in startedLoopbacks) try { l.Dispose(); } catch { /* best effort */ }
            foreach (var r in startedRenders)  try { r.Dispose(); } catch { /* best effort */ }
            foreach (var c in startedCaptures) try { c.Dispose(); } catch { /* best effort */ }
            throw;
        }

        _running = true;
        _thread  = new Thread(Loop)
        {
            IsBackground = true,
            Name         = "MixEngine",
            Priority     = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;

        foreach (var l in _renderLoopbacks) l?.Stop();
        foreach (var r in _renders)         r?.Stop();
        foreach (var c in _captures)        c?.Stop();
    }

    public void Dispose()
    {
        Stop();
        foreach (var c in _captures)
        {
            if (c is null) continue;
            c.DataReady -= OnCaptureDataReady;
        }
        foreach (var l in _renderLoopbacks) l?.Dispose();
        foreach (var r in _renders)         r?.Dispose();
        foreach (var c in _captures)        c?.Dispose();
        _captureWake.Dispose();
    }

    private void Loop()
    {
        var mmcss = Mmcss.Begin();
        try
        {
            _logger.LogInformation("MixEngine loop online @ {Rate} Hz, block={Block}", SampleRate, BlockFrames);

            while (_running)
            {
                if (Tick()) continue;
                // Wait for the next capture event with a short backstop —
                // protects against a missed Set or a silent device that
                // never delivers, without burning a thread on a tight loop.
                _captureWake.WaitOne(2);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MixEngine loop crashed");
        }
        finally
        {
            Mmcss.Revert(mmcss);
            _logger.LogInformation("MixEngine loop stopped");
        }
    }

    /// <summary>
    /// One mix iteration. Returns false if any capture ring is short of a
    /// full block; the caller (the mix loop) yields and tries again.
    /// </summary>
    private bool Tick()
    {
        var state = Volatile.Read(ref _state);

        for (var i = 0; i < _captures.Length; i++)
        {
            // Missing-source slot: never blocks the tick — it just produces silence below.
            if (_captures[i] is { } cap && cap.Ring.Available < BlockFrames) return false;
        }

        for (var i = 0; i < _captures.Length; i++)
        {
            if (_captures[i] is { } cap)
                cap.Ring.Read(_inputBlocksMono[i]);
            else
                Array.Clear(_inputBlocksMono[i]);
        }

        // Latency probe — watch side. Read once per tick; apply to both the
        // capture-watch (here) and the inject (after MixBlock) so they're
        // anchored to the same probe instance.
        var probe = Volatile.Read(ref _probe);
        if (probe is not null)
            ProbeWatchInput(probe);

        // BE-050: meter inputs *before* DSP. Inputs are mono so both L and
        // R meter slots receive the same block.
        for (var i = 0; i < _captures.Length; i++)
        {
            _meters.Accumulate(i * 2,     _inputBlocksMono[i]);
            _meters.Accumulate(i * 2 + 1, _inputBlocksMono[i]);
        }

        // BE-081: spectrum tap for an input channel — pre-DSP raw mic
        // signal so the FE's EQ overlay reflects what's hitting the EQ.
        // (Latency probe pulls from this same pre-DSP point above.)
        var spectrumTap = Volatile.Read(ref _spectrumTap);
        if (spectrumTap is { Bus: SpectrumFrame.BusTag.Input } inTap
            && (uint)inTap.Channel < (uint)_captures.Length)
        {
            _spectrumBuffer.Write(_inputBlocksMono[inTap.Channel]);
        }

        // BE-076: per-input mono DSP — gate → EQ → compressor.
        for (var i = 0; i < _captures.Length; i++)
        {
            var ch = i < state.Inputs.Length ? state.Inputs[i] : null;
            ApplyInputDsp(i, ch);
            var block = _inputBlocksMono[i].AsSpan();
            _inputGates[i].Process(block);
            _inputEqs[i]  .Process(block);
            _inputComps[i].Process(block);
        }

        // BE-080: split each input's mono signal into stereo via the per-
        // channel pan, with a one-pole smoother to keep slider drags
        // click-free. _inputBlocksL / _inputBlocksR are what feed MixBlock.
        for (var i = 0; i < _captures.Length; i++)
        {
            ApplyInputPan(
                i,
                _inputBlocksMono[i].AsSpan(),
                _inputBlocksL[i].AsSpan(),
                _inputBlocksR[i].AsSpan());
        }

        MixBlock(
            BlockFrames,
            state,
            _inputBlocksL,
            _inputBlocksR,
            _outputBlocksL,
            _outputBlocksR,
            _inputCurrentGain,
            _outputCurrentGain,
            GainRampCoefficient,
            RampSnapEpsilon);

        // BE-081: spectrum tap for an output channel — post-mix L block,
        // captured *before* the per-output DSP loop so the FE shows the
        // bus content that's about to hit the EQ.
        if (spectrumTap is { Bus: SpectrumFrame.BusTag.Output } outTap
            && (uint)outTap.Channel < (uint)_renders.Length)
        {
            _spectrumBuffer.Write(_outputBlocksL[outTap.Channel]);
        }

        // BE-076: per-output stereo DSP — EQ → comp → balance pan. Each
        // side runs through its own filter instance (independent delay
        // state) but shares coefficients via the shared Apply call.
        for (var o = 0; o < _renders.Length; o++)
        {
            var ch = o < state.Outputs.Length ? state.Outputs[o] : null;
            ApplyOutputDsp(o, ch);

            var bL = _outputBlocksL[o].AsSpan();
            var bR = _outputBlocksR[o].AsSpan();
            _outputEqsL[o]  .Process(bL);
            _outputEqsR[o]  .Process(bR);
            _outputCompsL[o].Process(bL);
            _outputCompsR[o].Process(bR);
            ApplyOutputPan(o, bL, bR);
        }

        // Latency probe — inject side. Sits AFTER per-output DSP so the
        // burst hits the render ring unaltered (no EQ/compressor mauls the
        // detection peak). Wipes the chosen output's block first so user
        // audio doesn't bleed into the test signal during the watch window.
        if (probe is not null)
            ProbeInjectOutput(probe);

        // BE-080: meter outputs from the post-mix L/R blocks. (Loopback
        // metering is on hold while the loopback path is still mono — the
        // post-mix blocks are what reflect the live pan/EQ/compressor
        // state, which is what the FE wants to render per side.)
        var inMeterCount = _captures.Length * 2;
        for (var o = 0; o < _renders.Length; o++)
        {
            _meters.Accumulate(inMeterCount + o * 2,     _outputBlocksL[o]);
            _meters.Accumulate(inMeterCount + o * 2 + 1, _outputBlocksR[o]);
        }
        _meters.OnBlockComplete(BlockFrames);

        // Interleave L,R into the render ring as float pairs. The ring is
        // sized in floats (2 floats per stereo frame); the wave provider
        // pulls pairs and expands to the device's channel count. Missing
        // render slots discard the post-mix block — the engine still ran
        // the channel through DSP, the user just doesn't hear it.
        for (var o = 0; o < _renders.Length; o++)
        {
            if (_renders[o] is not { } render) continue;
            var inter = _renderInterleaved[o];
            var bL = _outputBlocksL[o];
            var bR = _outputBlocksR[o];
            for (var s = 0; s < BlockFrames; s++)
            {
                inter[s * 2]     = bL[s];
                inter[s * 2 + 1] = bR[s];
            }
            render.Ring.Write(inter);
        }

        return true;
    }

    /// <summary>
    /// Latency probe — capture watch. Append this tick's pre-DSP input
    /// block for the watched channel into the probe's watch buffer until
    /// the buffer is full, then complete the awaitable so the RPC handler
    /// scans for the burst and reports the result.
    /// </summary>
    private void ProbeWatchInput(LatencyProbe probe)
    {
        if ((uint)probe.WatchInputIndex >= (uint)_captures.Length) return;

        var src       = _inputBlocksMono[probe.WatchInputIndex];
        var dst       = probe.WatchBuffer;
        var remaining = dst.Length - probe.WatchPosition;
        if (remaining <= 0) return;

        var toCopy = Math.Min(BlockFrames, remaining);
        Array.Copy(src, 0, dst, probe.WatchPosition, toCopy);
        probe.WatchPosition += toCopy;

        if (probe.WatchPosition >= dst.Length)
            probe.Completion.TrySetResult();
    }

    /// <summary>
    /// Latency probe — render inject. Wipes the chosen output's stereo
    /// block (so other inputs in the matrix don't bleed audible material
    /// into the test signal) and copies the next slice of the burst on
    /// top. Once the burst is exhausted the output stays silent for the
    /// remainder of the measurement window — that's intentional: it gives
    /// the watch buffer a clean ambient floor against which the burst
    /// arrival is unambiguous.
    /// </summary>
    private void ProbeInjectOutput(LatencyProbe probe)
    {
        if ((uint)probe.InjectOutputIndex >= (uint)_renders.Length) return;

        var bL = _outputBlocksL[probe.InjectOutputIndex];
        var bR = _outputBlocksR[probe.InjectOutputIndex];
        Array.Clear(bL);
        Array.Clear(bR);

        var burst     = probe.Burst;
        var remaining = burst.Length - probe.BurstPosition;
        if (remaining <= 0) return;

        var toCopy = Math.Min(BlockFrames, remaining);
        Array.Copy(burst, probe.BurstPosition, bL, 0, toCopy);
        Array.Copy(burst, probe.BurstPosition, bR, 0, toCopy);
        probe.BurstPosition += toCopy;
    }

    /// <summary>Reconfigure the input DSP chain when the channel record reference changed.</summary>
    private void ApplyInputDsp(int i, Channel? ch)
    {
        var last = _lastAppliedInput[i];
        if (!ReferenceEquals(ch, last))
        {
            _inputGates[i].Apply(ch?.Gate);
            _inputEqs[i]  .Apply(ch?.Eq);
            _inputComps[i].Apply(ch?.Compressor);
            _lastAppliedInput[i] = ch;
        }

        // Pan target re-evaluated every tick — cheap, and it keeps the
        // smoother responsive even if Apply is short-circuited above.
        var p = ch?.Pan ?? 0f;
        Pan.ComputeBalance(p, out var gL, out var gR);
        _inputPanTargetL[i] = gL;
        _inputPanTargetR[i] = gR;
    }

    /// <summary>
    /// Split <paramref name="mono"/> into <paramref name="l"/>/<paramref name="r"/>
    /// using a smoothed balance gain pair (BE-080). At centre pan both gains
    /// are 1.0 (no centre dip); at the extremes the off-side hits zero.
    /// </summary>
    private void ApplyInputPan(int i, ReadOnlySpan<float> mono, Span<float> l, Span<float> r)
    {
        var curL = _inputPanCurrentL[i];
        var curR = _inputPanCurrentR[i];
        var tarL = _inputPanTargetL[i];
        var tarR = _inputPanTargetR[i];

        if (curL == tarL && curR == tarR)
        {
            for (var s = 0; s < mono.Length; s++)
            {
                var v = mono[s];
                l[s] = v * tarL;
                r[s] = v * tarR;
            }
            return;
        }

        var stepL = (tarL - curL) / PanFadeSamples;
        var stepR = (tarR - curR) / PanFadeSamples;
        for (var s = 0; s < mono.Length; s++)
        {
            curL += stepL;
            if ((stepL > 0f && curL > tarL) || (stepL < 0f && curL < tarL)) curL = tarL;
            curR += stepR;
            if ((stepR > 0f && curR > tarR) || (stepR < 0f && curR < tarR)) curR = tarR;
            var v = mono[s];
            l[s] = v * curL;
            r[s] = v * curR;
        }
        _inputPanCurrentL[i] = curL;
        _inputPanCurrentR[i] = curR;
    }

    /// <summary>Same as <see cref="ApplyInputDsp"/> for the per-output chain.</summary>
    private void ApplyOutputDsp(int o, Channel? ch)
    {
        var last = _lastAppliedOutput[o];
        if (!ReferenceEquals(ch, last))
        {
            _outputEqsL[o]  .Apply(ch?.Eq);
            _outputEqsR[o]  .Apply(ch?.Eq);
            _outputCompsL[o].Apply(ch?.Compressor);
            _outputCompsR[o].Apply(ch?.Compressor);
            _lastAppliedOutput[o] = ch;
        }

        var p = ch?.Pan ?? 0f;
        Pan.ComputeBalance(p, out var gL, out var gR);
        _outputPanTargetL[o] = gL;
        _outputPanTargetR[o] = gR;
    }

    /// <summary>
    /// Apply the smoothed balance pan to one stereo output block. The smoother
    /// fades current → target over <see cref="PanFadeSamples"/> samples
    /// (BE-078) so toggling pan or sliding the slider doesn't click.
    /// </summary>
    private void ApplyOutputPan(int o, Span<float> blockL, Span<float> blockR)
    {
        var curL = _outputPanCurrentL[o];
        var curR = _outputPanCurrentR[o];
        var tarL = _outputPanTargetL[o];
        var tarR = _outputPanTargetR[o];

        if (curL == tarL && curR == tarR)
        {
            if (tarL != 1f)
                for (var s = 0; s < blockL.Length; s++) blockL[s] *= tarL;
            if (tarR != 1f)
                for (var s = 0; s < blockR.Length; s++) blockR[s] *= tarR;
            return;
        }

        var stepL = (tarL - curL) / PanFadeSamples;
        var stepR = (tarR - curR) / PanFadeSamples;
        for (var s = 0; s < blockL.Length; s++)
        {
            curL += stepL;
            if ((stepL > 0f && curL > tarL) || (stepL < 0f && curL < tarL)) curL = tarL;
            curR += stepR;
            if ((stepR > 0f && curR > tarR) || (stepR < 0f && curR < tarR)) curR = tarR;
            blockL[s] *= curL;
            blockR[s] *= curR;
        }
        _outputPanCurrentL[o] = curL;
        _outputPanCurrentR[o] = curR;
    }

    /// <summary>
    /// Pure block-mix in stereo. Reads <paramref name="state"/>, applies the
    /// routing matrix and per-channel gain/mute/solo (with one-pole ramping)
    /// over <paramref name="blockFrames"/> samples, and writes the per-output
    /// L/R accumulators into <paramref name="outputBlocksL"/> /
    /// <paramref name="outputBlocksR"/>. Smoother state advances by exactly
    /// one block. No allocations.
    ///
    /// Inputs come in as a stereo pair already (the engine has applied the
    /// per-input pan upstream). For unit tests, callers can supply the same
    /// mono buffer for both L and R when pan-at-centre behaviour is desired.
    ///
    /// Pulled out of <see cref="Tick"/> so unit tests can exercise it
    /// directly without WASAPI / NAudio in the loop.
    /// </summary>
    public static void MixBlock(
        int blockFrames,
        MixerState state,
        float[][] inputBlocksL,
        float[][] inputBlocksR,
        float[][] outputBlocksL,
        float[][] outputBlocksR,
        float[] inputCurrentGain,
        float[] outputCurrentGain,
        float rampCoefficient,
        float rampSnapEpsilon)
    {
        var matrix  = state.Matrix;
        var inputs  = state.Inputs;
        var outputs = state.Outputs;
        var captures = inputBlocksL.Length;
        var renders  = outputBlocksL.Length;

        for (var o = 0; o < renders; o++)
        {
            Array.Clear(outputBlocksL[o]);
            Array.Clear(outputBlocksR[o]);
        }

        var anyInputSoloed = SoloLogic.HasAnySolo(inputs);

        var matIn  = Math.Min(matrix.Inputs,  captures);
        var matOut = Math.Min(matrix.Outputs, renders);

        for (var i = 0; i < matIn; i++)
        {
            float target;
            if (i < inputs.Length)
            {
                var ch = inputs[i];
                target = SoloLogic.IsAudible(ch, anyInputSoloed) ? ch.GainLinear : 0f;
            }
            else
            {
                target = 1f;
            }

            var current = inputCurrentGain[i];
            var alreadyAtTarget = MathF.Abs(current - target) <= rampSnapEpsilon;

            if (alreadyAtTarget && target == 0f)
            {
                inputCurrentGain[i] = 0f;
                continue;
            }

            var inL = inputBlocksL[i];
            var inR = inputBlocksR[i];

            for (var o = 0; o < matOut; o++)
            {
                if (!matrix[i, o]) continue;

                var outL = outputBlocksL[o];
                var outR = outputBlocksR[o];
                if (alreadyAtTarget)
                {
                    var g = target;
                    for (var s = 0; s < blockFrames; s++)
                    {
                        outL[s] += inL[s] * g;
                        outR[s] += inR[s] * g;
                    }
                }
                else
                {
                    var g = current;
                    for (var s = 0; s < blockFrames; s++)
                    {
                        g += (target - g) * rampCoefficient;
                        outL[s] += inL[s] * g;
                        outR[s] += inR[s] * g;
                    }
                }
            }

            inputCurrentGain[i] = alreadyAtTarget
                ? target
                : AdvanceRamp(current, target, blockFrames, rampCoefficient);
        }

        var anyOutputSoloed = SoloLogic.HasAnySolo(outputs);

        for (var o = 0; o < renders; o++)
        {
            var blockL = outputBlocksL[o];
            var blockR = outputBlocksR[o];

            float target;
            if (o < outputs.Length)
            {
                var ch = outputs[o];
                target = SoloLogic.IsAudible(ch, anyOutputSoloed) ? ch.GainLinear : 0f;
            }
            else
            {
                target = 1f;
            }

            var current = outputCurrentGain[o];
            var alreadyAtTarget = MathF.Abs(current - target) <= rampSnapEpsilon;

            if (alreadyAtTarget)
            {
                if (target == 0f)
                {
                    Array.Clear(blockL);
                    Array.Clear(blockR);
                }
                else if (MathF.Abs(target - 1f) > 0.0001f)
                {
                    var g = target;
                    for (var s = 0; s < blockFrames; s++)
                    {
                        blockL[s] *= g;
                        blockR[s] *= g;
                    }
                }
                outputCurrentGain[o] = target;
            }
            else
            {
                var g = current;
                for (var s = 0; s < blockFrames; s++)
                {
                    g += (target - g) * rampCoefficient;
                    blockL[s] *= g;
                    blockR[s] *= g;
                }
                outputCurrentGain[o] = g;
            }

            // Light clipping safety per side — a hot mix could exceed unity
            // after summing many inputs.
            for (var s = 0; s < blockFrames; s++)
            {
                var l = blockL[s];
                if (l >  1f) blockL[s] =  1f;
                else if (l < -1f) blockL[s] = -1f;
                var r = blockR[s];
                if (r >  1f) blockR[s] =  1f;
                else if (r < -1f) blockR[s] = -1f;
            }
        }
    }

    /// <summary>
    /// Closed-form advance of a one-pole smoother by <paramref name="frames"/>
    /// samples. Used when an input is unrouted so we can keep the smoother in
    /// step with the rendered path without iterating per sample.
    /// </summary>
    private static float AdvanceRamp(float current, float target, int frames, float coeff)
    {
        // x_n = target + (1 - coeff)^n * (x_0 - target)
        var decay = MathF.Pow(1f - coeff, frames);
        return target + decay * (current - target);
    }
}
