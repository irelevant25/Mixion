using System.Diagnostics;
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
/// and runs the inner loop: read each input's stereo ring, run the per-input
/// stereo DSP chain (<c>gate → EQ → compressor → balance</c>), apply the
/// routing matrix + per-channel gain/mute/solo into stereo output buses, run
/// the per-output chain (<c>EQ → compressor → balance</c>) and write
/// interleaved L/R pairs into each render's ring (BE-076 / BE-080).
///
/// <para>
/// Channel slots are allocated with spare capacity so the topology can change
/// while audio keeps flowing: <see cref="ReplaceCapture"/> / <see cref="ReplaceRender"/>
/// swap the source behind an existing channel (an app restarted, a USB device
/// came back) and <see cref="TryAppendCapture"/> / <see cref="TryAppendRender"/>
/// attach newly discovered ones — without re-opening any other device.
/// <see cref="EngineHost"/> coordinates those calls with the matching state
/// changes.
/// </para>
///
/// The audio path never allocates and never locks. State swaps publish via
/// <see cref="UpdateState"/>; the loop reads them with <see cref="Volatile.Read{T}"/>.
/// </summary>
public sealed class MixEngine : IDisposable
{
    /// <summary>
    /// Default frames processed per mix tick when no devices report a
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
    /// Resolved frames processed per mix tick. Derived in the constructor from
    /// the minimum granted period across the initial capture and render
    /// devices, clamped to [<see cref="MinBlockFrames"/>, <see cref="MaxBlockFrames"/>].
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

    /// <summary>Length of the balance-gain crossfade in samples (~10 ms @ 48 kHz).</summary>
    public const int PanFadeSamples = 480;

    /// <summary>Input slots allocated beyond the initial channel count, so devices and apps that appear later attach without a rebuild.</summary>
    public const int SpareInputSlots = 32;

    /// <summary>Output slots allocated beyond the initial channel count.</summary>
    public const int SpareOutputSlots = 16;

    /// <summary>
    /// Shortest time a live capture may go without delivering a block before the
    /// mix stops waiting for it. Within this window the tick waits so every live
    /// source stays aligned; past it the source counts as silent, so an unplugged
    /// device or a closed app costs a brief dropout instead of stalling every
    /// other channel. A source that normally delivers in longer bursts gets twice
    /// its measured delivery gap instead (see <see cref="MaxDeliveryGapMs"/>).
    /// Capture rings hold more than the longest wait
    /// (<see cref="EngineFactory.CaptureRingFrames"/>), so the others can't
    /// overflow meanwhile.
    /// </summary>
    public const int StarvedSourceTimeoutMs = 50;

    /// <summary>
    /// Shortest hold-back that counts as a stall. When ready sources were held
    /// back this long, the tick that finally runs first trims each capture ring
    /// down to what the sources' rhythms explain — one delivery's worth, at
    /// least a block. The rest built up while the mix waited; mixed as-is it
    /// would sit in the render rings as permanent latency. A late source is
    /// judged by the rhythm it had before it caught up, so a one-off delay
    /// can't excuse itself.
    /// </summary>
    public const int StallTrimThresholdMs = 30;

    /// <summary>
    /// Longest delivery rhythm the pacing adapts to. Most captures deliver every
    /// 3–10 ms, but some drivers deliver 20–45 ms at a time (Bluetooth, or an
    /// interface whose Windows buffer follows a large ASIO buffer). The mix
    /// thread learns each source's gap between deliveries (see
    /// <see cref="OnDelivery"/>): the source gets twice that before it's skipped,
    /// and a stall trim leaves its ring one gap's worth.
    /// </summary>
    public const int MaxDeliveryGapMs = 60;

    /// <summary>A gap between deliveries longer than this is a pause (the app went quiet, the source is new), not a rhythm.</summary>
    private const int PauseGapMs = 250;

    private readonly ILogger _logger;

    // Slot arrays are sized to capacity; only indices below _inputCount /
    // _outputCount are live. Elements are swapped whole so the mix thread
    // always reads a coherent reference. A null slot is a channel that exists
    // in MixerState without a backing source right now (device unplugged, app
    // closed) — its input is silence, its output is discarded. That is what
    // lets the FE keep a red, greyed-out strip with all its settings instead
    // of losing the slot binding.
    private readonly IAudioCaptureSource?[] _captures;
    private readonly IRenderDevice?[]        _renders;
    private int _inputCount;
    private int _outputCount;

    /// <summary>Serialises slot mutations and the start/stop lifecycle. The mix thread never takes it.</summary>
    private readonly object _topologyLock = new();
    /// <summary>Serialises <see cref="UpdateState"/> writers. The mix thread never takes it.</summary>
    private readonly object _stateWriteLock = new();
    private bool _disposed;

    // Mix-thread-only pacing bookkeeping, in Stopwatch timestamps.
    private static readonly long StarvedSourceTimeoutTicks = MsToTicks(StarvedSourceTimeoutMs);
    private static readonly long StallTrimThresholdTicks   = MsToTicks(StallTrimThresholdMs);
    private static readonly long MaxDeliveryGapTicks       = MsToTicks(MaxDeliveryGapMs);
    private static readonly long PauseGapTicks             = MsToTicks(PauseGapMs);
    private readonly IAudioCaptureSource?[] _tickCaptures;
    private readonly IAudioCaptureSource?[] _lastSeenCaptures;
    /// <summary>When the mix last took a block from each slot.</summary>
    private readonly long[]                 _lastBlockAt;
    /// <summary>Each slot's ring level as the mix thread last left it — a higher reading means the source delivered.</summary>
    private readonly int[]                  _knownLevel;
    /// <summary>When each slot's source last delivered.</summary>
    private readonly long[]                 _lastDeliveryAt;
    /// <summary>Each slot's learned gap between deliveries — its rhythm (see <see cref="OnDelivery"/>).</summary>
    private readonly long[]                 _deliveryGap;
    /// <summary>Per tick: the rhythm each slot's backlog is judged by if the tick trims.</summary>
    private readonly long[]                 _trimRhythm;
    /// <summary>One block's duration in Stopwatch ticks.</summary>
    private readonly long                   _blockTicks;
    /// <summary>When ready sources started being held back for a late one; 0 while not holding back.</summary>
    private long                            _heldBackSince;

    /// <summary>Per-input interleaved block straight off the capture ring.</summary>
    private readonly float[][] _inputInterleaved;
    /// <summary>Per-input L block — raw, then DSP'd in place; what MixBlock reads.</summary>
    private readonly float[][] _inputBlocksL;
    /// <summary>Per-input R block — raw, then DSP'd in place; what MixBlock reads.</summary>
    private readonly float[][] _inputBlocksR;

    /// <summary>Per-output post-mix L block.</summary>
    private readonly float[][] _outputBlocksL;
    /// <summary>Per-output post-mix R block.</summary>
    private readonly float[][] _outputBlocksR;
    /// <summary>Per-output interleaved L,R scratch — what we write to the render ring each tick.</summary>
    private readonly float[][] _renderInterleaved;
    /// <summary>Mono mixdown scratch for the spectrum tap.</summary>
    private readonly float[]   _monoScratch;

    /// <summary>Current (smoothed) per-input linear gain. Allocated once.</summary>
    private readonly float[] _inputCurrentGain;
    /// <summary>Current (smoothed) per-output linear gain. Allocated once.</summary>
    private readonly float[] _outputCurrentGain;

    /// <summary>Smoothed per-input balance: current/target gain on each side.</summary>
    private readonly float[] _inputPanCurrentL;
    private readonly float[] _inputPanCurrentR;
    private readonly float[] _inputPanTargetL;
    private readonly float[] _inputPanTargetR;

    private readonly MeterAggregator _meters;

    // BE-076: per-channel DSP. EQs run one instance per side (independent
    // delay state, identical coefficients); the gate and compressor are
    // stereo-linked so they never pull the image to one side.
    private readonly NoiseGate[]  _inputGates;
    private readonly Equalizer[]  _inputEqsL;
    private readonly Equalizer[]  _inputEqsR;
    private readonly Compressor[] _inputComps;
    private readonly Channel?[]   _lastAppliedInput;

    private readonly Equalizer[]  _outputEqsL;
    private readonly Equalizer[]  _outputEqsR;
    private readonly Compressor[] _outputComps;
    private readonly Channel?[]   _lastAppliedOutput;
    /// <summary>Smoothed per-output balance gains, per side.</summary>
    private readonly float[] _outputPanCurrentL;
    private readonly float[] _outputPanCurrentR;
    private readonly float[] _outputPanTargetL;
    private readonly float[] _outputPanTargetR;

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
    /// — the <c>measureRouteLatency</c> handler awaits + decodes the
    /// result. Atomic exchange so the audio thread observes one coherent
    /// reference per tick.
    /// </summary>
    private LatencyProbe? _probe;

    public int SampleRate { get; }

    /// <summary>Live input channels — index-aligned with <see cref="MixerState.Inputs"/>.</summary>
    public int InputCount => Volatile.Read(ref _inputCount);

    /// <summary>Live output channels — index-aligned with <see cref="MixerState.Outputs"/>.</summary>
    public int OutputCount => Volatile.Read(ref _outputCount);

    /// <summary>Maximum input channels before a rebuild is needed to grow.</summary>
    public int InputCapacity => _captures.Length;

    /// <summary>Maximum output channels before a rebuild is needed to grow.</summary>
    public int OutputCapacity => _renders.Length;

    /// <summary>
    /// Snapshot of the sources behind the live input channels, index-aligned
    /// with <see cref="MixerState.Inputs"/>. Allocates — control plane only.
    /// </summary>
    public IReadOnlyList<IAudioCaptureSource?> Captures
    {
        get
        {
            var copy = new IAudioCaptureSource?[InputCount];
            for (var i = 0; i < copy.Length; i++) copy[i] = Volatile.Read(ref _captures[i]);
            return copy;
        }
    }

    /// <summary>
    /// Snapshot of the devices behind the live output channels, index-aligned
    /// with <see cref="MixerState.Outputs"/>. Allocates — control plane only.
    /// </summary>
    public IReadOnlyList<IRenderDevice?> Renders
    {
        get
        {
            var copy = new IRenderDevice?[OutputCount];
            for (var o = 0; o < copy.Length; o++) copy[o] = Volatile.Read(ref _renders[o]);
            return copy;
        }
    }

    /// <summary>
    /// Upper bound on the meter sides <see cref="SnapshotMeters"/> can report.
    /// Size telemetry buffers with it so a channel attached between two reads
    /// can never overflow them.
    /// </summary>
    public int MeterSideCapacity => (_captures.Length + _renders.Length) * 2;

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

    /// <summary>Last-block gain reduction (dB, ≥ 0) of an input's stereo-linked compressor.</summary>
    public float GetInputCompressorGainReductionDb(int input)
        => (uint)input < (uint)InputCount ? _inputComps[input].GainReductionDb : 0f;

    /// <summary>Last-block gain reduction (dB, ≥ 0) of an output's stereo-linked compressor.</summary>
    public float GetOutputCompressorGainReductionDb(int output)
        => (uint)output < (uint)OutputCount ? _outputComps[output].GainReductionDb : 0f;

    public MixEngine(
        IEnumerable<IAudioCaptureSource?> captures,
        IEnumerable<IRenderDevice?>        renders,
        MixerState                        initialState,
        ILogger                           logger,
        int                               spareInputSlots  = SpareInputSlots,
        int                               spareOutputSlots = SpareOutputSlots)
    {
        _logger = logger;
        _state  = initialState;

        var initialCaptures = captures.ToArray();
        var initialRenders  = renders.ToArray();

        if (initialCaptures.Length == 0)
            throw new InvalidOperationException("MixEngine requires at least one capture slot.");
        if (initialRenders.Length == 0)
            throw new InvalidOperationException("MixEngine requires at least one render slot.");

        // BE-028: reject mismatched sample rates up front. Null slots
        // (channels in state without a backing device — e.g. an unplugged
        // mic the user wants to keep visible until they replug it) are
        // skipped; their rate is inherited from whichever real device
        // anchors the engine.
        var rate = FirstRealSampleRate(initialCaptures, initialRenders)
            ?? throw new InvalidOperationException(
                "MixEngine requires at least one active capture or render device.");
        foreach (var c in initialCaptures)
        {
            if (c is null) continue;
            if (c.SampleRate != rate)
                throw new InvalidOperationException(
                    $"Sample-rate mismatch: capture device '{c.FriendlyName}' is {c.SampleRate} Hz " +
                    $"but engine rate is {rate} Hz. Match the device formats in Windows sound settings " +
                    "(e.g. set both to 48 000 Hz) and restart.");
        }
        foreach (var r in initialRenders)
        {
            if (r is null) continue;
            if (r.SampleRate != rate)
                throw new InvalidOperationException(
                    $"Sample-rate mismatch: render device '{r.FriendlyName}' is {r.SampleRate} Hz " +
                    $"but capture is {rate} Hz. Match the device formats in Windows sound settings.");
        }
        SampleRate  = rate;
        BlockFrames = ResolveBlockFrames(initialCaptures, initialRenders);
        _logger.LogInformation(
            "MixEngine block size: {BlockFrames} frames (~{Ms} ms @ {Rate} Hz)",
            BlockFrames, (BlockFrames * 1000.0 / rate).ToString("F2"), rate);

        var inputCapacity  = initialCaptures.Length + Math.Max(0, spareInputSlots);
        var outputCapacity = initialRenders.Length  + Math.Max(0, spareOutputSlots);

        _captures = new IAudioCaptureSource?[inputCapacity];
        Array.Copy(initialCaptures, _captures, initialCaptures.Length);
        _renders = new IRenderDevice?[outputCapacity];
        Array.Copy(initialRenders, _renders, initialRenders.Length);
        _inputCount  = initialCaptures.Length;
        _outputCount = initialRenders.Length;

        _tickCaptures     = new IAudioCaptureSource?[inputCapacity];
        _lastSeenCaptures = new IAudioCaptureSource?[inputCapacity];
        _lastBlockAt      = new long[inputCapacity];
        _knownLevel       = new int[inputCapacity];
        _lastDeliveryAt   = new long[inputCapacity];
        _deliveryGap      = new long[inputCapacity];
        _trimRhythm       = new long[inputCapacity];
        _blockTicks       = Math.Max(1, Stopwatch.Frequency * BlockFrames / rate);

        // BE-027 / BE-080: pre-allocate every per-slot scratch block up to
        // capacity. The mix loop never touches the GC after this point, and
        // attaching a channel later doesn't either.
        _inputInterleaved  = AllocateBlocks(inputCapacity,  BlockFrames * 2);
        _inputBlocksL      = AllocateBlocks(inputCapacity,  BlockFrames);
        _inputBlocksR      = AllocateBlocks(inputCapacity,  BlockFrames);
        _outputBlocksL     = AllocateBlocks(outputCapacity, BlockFrames);
        _outputBlocksR     = AllocateBlocks(outputCapacity, BlockFrames);
        _renderInterleaved = AllocateBlocks(outputCapacity, BlockFrames * 2);
        _monoScratch       = new float[BlockFrames];

        // BE-044: gain + balance smoothers, seeded at the initial targets so
        // nothing ramps from silence on the first tick.
        _inputCurrentGain = new float[inputCapacity];
        _inputPanCurrentL = new float[inputCapacity];
        _inputPanCurrentR = new float[inputCapacity];
        _inputPanTargetL  = new float[inputCapacity];
        _inputPanTargetR  = new float[inputCapacity];
        for (var i = 0; i < inputCapacity; i++)
            SeedInputSlot(i, i < initialState.Inputs.Length ? initialState.Inputs[i] : null);

        _outputCurrentGain = new float[outputCapacity];
        _outputPanCurrentL = new float[outputCapacity];
        _outputPanCurrentR = new float[outputCapacity];
        _outputPanTargetL  = new float[outputCapacity];
        _outputPanTargetR  = new float[outputCapacity];
        for (var o = 0; o < outputCapacity; o++)
            SeedOutputSlot(o, o < initialState.Outputs.Length ? initialState.Outputs[o] : null);

        // BE-076: per-channel DSP. Processors are instantiated even if the
        // corresponding state is null — Apply(null) just keeps them in
        // bypass and Process is a fast no-op.
        _inputGates       = new NoiseGate[inputCapacity];
        _inputEqsL        = new Equalizer[inputCapacity];
        _inputEqsR        = new Equalizer[inputCapacity];
        _inputComps       = new Compressor[inputCapacity];
        _lastAppliedInput = new Channel?[inputCapacity];
        for (var i = 0; i < inputCapacity; i++)
        {
            _inputGates[i] = new NoiseGate(rate);
            _inputEqsL[i]  = new Equalizer(rate);
            _inputEqsR[i]  = new Equalizer(rate);
            _inputComps[i] = new Compressor(rate);
        }

        _outputEqsL        = new Equalizer[outputCapacity];
        _outputEqsR        = new Equalizer[outputCapacity];
        _outputComps       = new Compressor[outputCapacity];
        _lastAppliedOutput = new Channel?[outputCapacity];
        for (var o = 0; o < outputCapacity; o++)
        {
            _outputEqsL[o]  = new Equalizer(rate);
            _outputEqsR[o]  = new Equalizer(rate);
            _outputComps[o] = new Compressor(rate);
        }

        // BE-050 / BE-080: two meter sides per slot. Inputs occupy
        // [0, 2·inputCapacity), outputs the range after that; SnapshotMeters
        // packs the live ones into the wire layout. 33 ms window → ~30 Hz.
        _meters = new MeterAggregator((inputCapacity + outputCapacity) * 2, SampleRate);

        // Event-driven mix wake: every capture pings _captureWake the moment
        // it appends data, so the mix loop runs as soon as a block is
        // available instead of polling on a 1 ms timer (which is actually
        // 1–15 ms depending on the host timer resolution).
        foreach (var c in initialCaptures)
        {
            if (c is null) continue;
            c.DataReady += OnCaptureDataReady;
        }
    }

    private void OnCaptureDataReady(object? sender, EventArgs e)
    {
        // A capture thread can race Dispose: it may read the delegate just
        // before being detached and fire after the engine is gone.
        try { _captureWake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private static float[][] AllocateBlocks(int count, int length)
    {
        var blocks = new float[count][];
        for (var i = 0; i < count; i++) blocks[i] = new float[length];
        return blocks;
    }

    private void SeedInputSlot(int index, Channel? channel)
    {
        _inputCurrentGain[index] = channel?.GainLinear ?? 1f;
        Pan.ComputeBalance(channel?.Pan ?? 0f, out var gL, out var gR);
        _inputPanCurrentL[index] = gL;
        _inputPanCurrentR[index] = gR;
        _inputPanTargetL[index]  = gL;
        _inputPanTargetR[index]  = gR;
    }

    private void SeedOutputSlot(int index, Channel? channel)
    {
        _outputCurrentGain[index] = channel?.GainLinear ?? 1f;
        Pan.ComputeBalance(channel?.Pan ?? 0f, out var gL, out var gR);
        _outputPanCurrentL[index] = gL;
        _outputPanCurrentR[index] = gR;
        _outputPanTargetL[index]  = gL;
        _outputPanTargetR[index]  = gR;
    }

    /// <summary>
    /// Sample rate of the first non-null device across captures + renders.
    /// Anchors the engine rate when some slots are placeholders for missing
    /// devices.
    /// </summary>
    private static int? FirstRealSampleRate(IAudioCaptureSource?[] captures, IRenderDevice?[] renders)
    {
        foreach (var c in captures) if (c is not null) return c.SampleRate;
        foreach (var r in renders)  if (r is not null) return r.SampleRate;
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

    // ------------------------------------------------------------------ state

    /// <summary>
    /// Atomically derive and publish the next <see cref="MixerState"/>.
    /// <paramref name="update"/> runs under a control-plane lock against the
    /// latest published state, so concurrent writers — RPC handlers, the
    /// device watcher, preset loads — can't lose each other's changes. The mix
    /// thread never takes the lock; it observes the new reference on its next
    /// tick. If <paramref name="update"/> throws, nothing is published.
    ///
    /// The state may never describe more channels than the engine has live
    /// slots — attach slots first (see <see cref="TryAppendCapture"/>).
    /// </summary>
    public MixerState UpdateState(Func<MixerState, MixerState> update)
    {
        lock (_stateWriteLock)
        {
            var next = update(_state);
            if (next.Inputs.Length > InputCount || next.Outputs.Length > OutputCount)
                throw new InvalidOperationException(
                    $"State describes {next.Inputs.Length} inputs / {next.Outputs.Length} outputs but the engine has " +
                    $"{InputCount} / {OutputCount} slots. Attach slots before publishing channels.");
            Volatile.Write(ref _state, next);
            return next;
        }
    }

    public MixerState SnapshotState() => Volatile.Read(ref _state);

    // --------------------------------------------------------------- topology

    /// <summary>
    /// Bind <paramref name="source"/> (or nothing, when null) to the existing
    /// input slot <paramref name="index"/>. The new source is started before
    /// it becomes visible to the mix thread. The previous source is detached
    /// and returned still running — the caller stops and disposes it.
    /// </summary>
    public IAudioCaptureSource? ReplaceCapture(int index, IAudioCaptureSource? source)
    {
        lock (_topologyLock)
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)InputCount) throw new ArgumentOutOfRangeException(nameof(index));

            AttachCapture(source);
            var previous = Interlocked.Exchange(ref _captures[index], source);
            if (previous is not null) previous.DataReady -= OnCaptureDataReady;
            return previous;
        }
    }

    /// <summary>
    /// Attach a new input slot bound to <paramref name="source"/> (null for a
    /// placeholder channel whose source isn't available yet), with smoothers
    /// seeded from <paramref name="channel"/>. Returns the slot index, or -1
    /// when every spare slot is taken — the caller then rebuilds the engine.
    /// Publish <paramref name="channel"/> through <see cref="UpdateState"/>
    /// afterwards; until then the slot mixes as a bypassed, unrouted input.
    /// </summary>
    public int TryAppendCapture(IAudioCaptureSource? source, Channel channel)
    {
        lock (_topologyLock)
        {
            ThrowIfDisposed();
            var index = InputCount;
            if (index >= _captures.Length) return -1;

            AttachCapture(source);
            // Slots at or above _inputCount are invisible to the mix thread,
            // so seeding them here can't race the audio path.
            SeedInputSlot(index, channel);
            _lastAppliedInput[index] = null;
            Volatile.Write(ref _captures[index], source);
            Volatile.Write(ref _inputCount, index + 1);
            return index;
        }
    }

    /// <summary>Output counterpart of <see cref="ReplaceCapture"/>.</summary>
    public IRenderDevice? ReplaceRender(int index, IRenderDevice? device)
    {
        lock (_topologyLock)
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)OutputCount) throw new ArgumentOutOfRangeException(nameof(index));

            AttachRender(device);
            return Interlocked.Exchange(ref _renders[index], device);
        }
    }

    /// <summary>Output counterpart of <see cref="TryAppendCapture"/>.</summary>
    public int TryAppendRender(IRenderDevice? device, Channel channel)
    {
        lock (_topologyLock)
        {
            ThrowIfDisposed();
            var index = OutputCount;
            if (index >= _renders.Length) return -1;

            AttachRender(device);
            SeedOutputSlot(index, channel);
            _lastAppliedOutput[index] = null;
            Volatile.Write(ref _renders[index], device);
            Volatile.Write(ref _outputCount, index + 1);
            return index;
        }
    }

    private void AttachCapture(IAudioCaptureSource? source)
    {
        if (source is null) return;
        if (source.SampleRate != SampleRate)
            throw new InvalidOperationException(
                $"Capture '{source.FriendlyName}' runs at {source.SampleRate} Hz but the engine runs at {SampleRate} Hz.");

        source.DataReady += OnCaptureDataReady;
        if (!_running) return;
        try
        {
            source.Start();
        }
        catch
        {
            source.DataReady -= OnCaptureDataReady;
            throw;
        }
    }

    private void AttachRender(IRenderDevice? device)
    {
        if (device is null) return;
        if (device.SampleRate != SampleRate)
            throw new InvalidOperationException(
                $"Render '{device.FriendlyName}' runs at {device.SampleRate} Hz but the engine runs at {SampleRate} Hz.");
        if (_running) device.Start();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MixEngine));
    }

    // -------------------------------------------------------------- lifecycle

    public void Start()
    {
        lock (_topologyLock)
        {
            ThrowIfDisposed();
            if (_running) return;

            // Track everything we successfully start so we can stop it again
            // if a later device throws — otherwise a failure on (say) the
            // third render would leave the first two running, including any
            // that hold an exclusive-mode lock on a physical device. Disposal
            // stays with Dispose(), which the caller runs on failure.
            var startedCaptures = new List<IAudioCaptureSource>();
            var startedRenders  = new List<IRenderDevice>();

            try
            {
                for (var i = 0; i < InputCount; i++)
                {
                    if (_captures[i] is not { } c) continue;
                    c.Start();
                    startedCaptures.Add(c);
                }
                for (var o = 0; o < OutputCount; o++)
                {
                    if (_renders[o] is not { } r) continue;
                    r.Start();
                    startedRenders.Add(r);
                }
            }
            catch
            {
                // Each call is wrapped because a device that's already in a
                // bad state may throw on Stop too, and we still need to drain
                // the rest.
                foreach (var r in startedRenders)  try { r.Stop(); } catch { /* best effort */ }
                foreach (var c in startedCaptures) try { c.Stop(); } catch { /* best effort */ }
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
    }

    public void Stop()
    {
        lock (_topologyLock)
        {
            if (!_running) return;
            _running = false;
            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;

            // Render first, then capture — the last mixed blocks drain out
            // before their sources go quiet (BE-092).
            for (var o = 0; o < OutputCount; o++) StopQuietly(_renders[o]);
            for (var i = 0; i < InputCount; i++)  StopQuietly(_captures[i]);
        }
    }

    public void Dispose()
    {
        lock (_topologyLock)
        {
            if (_disposed) return;
            Stop();

            for (var i = 0; i < InputCount; i++)
            {
                if (_captures[i] is not { } c) continue;
                c.DataReady -= OnCaptureDataReady;
                DisposeQuietly(c);
            }
            for (var o = 0; o < OutputCount; o++) DisposeQuietly(_renders[o]);

            _disposed = true;
        }
        _captureWake.Dispose();
    }

    /// <summary>Stop + dispose a source or device detached from the engine, swallowing "device gone" errors.</summary>
    public static void DisposeQuietly(IDisposable? device)
    {
        if (device is null) return;
        try { device.Dispose(); } catch { /* the device may already be gone */ }
    }

    private static void StopQuietly(IAudioCaptureSource? source)
    {
        if (source is null) return;
        try { source.Stop(); } catch { /* best effort */ }
    }

    private static void StopQuietly(IRenderDevice? device)
    {
        if (device is null) return;
        try { device.Stop(); } catch { /* best effort */ }
    }

    private void Loop()
    {
        var mmcss = Mmcss.Begin();
        try
        {
            _logger.LogInformation("MixEngine loop online @ {Rate} Hz, block={Block}", SampleRate, BlockFrames);

            while (_running)
            {
                if (Tick(Stopwatch.GetTimestamp())) continue;
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

    // ------------------------------------------------------------------- tick

    /// <summary>
    /// One mix iteration at <paramref name="now"/> (a Stopwatch timestamp).
    /// Returns false when the tick can't run yet: a live capture is still short
    /// of a full block (and hasn't been silent long enough to be skipped), or no
    /// capture has anything to mix at all. Called only by the mix thread — or by
    /// tests driving an engine that was never started.
    /// </summary>
    internal bool Tick(long now)
    {
        var state        = Volatile.Read(ref _state);
        var inCount      = Volatile.Read(ref _inputCount);
        var outCount     = Volatile.Read(ref _outputCount);
        var need         = BlockFrames * 2;
        var anyReady     = false;
        var awaiting     = false;
        var lateRhythm   = 0L;

        // Pass 1: can we run? Every live source must have a full block, except
        // sources that have delivered nothing for longer than their timeout —
        // a closed app or an unplugged device must not hold every other
        // channel hostage. Freshly attached sources start out "starved" so
        // they don't stall the mix while they warm up.
        for (var i = 0; i < inCount; i++)
        {
            var cap = Volatile.Read(ref _captures[i]);
            _tickCaptures[i] = cap;
            if (cap is null)
            {
                _lastSeenCaptures[i] = null; // don't keep a detached source reachable
                continue;
            }

            if (!ReferenceEquals(cap, _lastSeenCaptures[i]))
            {
                _lastSeenCaptures[i] = cap;
                _lastBlockAt[i]      = long.MinValue / 2;
                _lastDeliveryAt[i]   = long.MinValue / 2;
                _knownLevel[i]       = 0;
                _deliveryGap[i]      = 0;
            }

            var level  = cap.Ring.Available;
            var rhythm = _deliveryGap[i];
            if (level > _knownLevel[i])
            {
                OnDelivery(i, now, level - _knownLevel[i]);

                // A source's first delivery has just seeded its rhythm.
                if (rhythm == 0) rhythm = _deliveryGap[i];

                // Short of a block until now, so it may be what the mix waited
                // for. Judge that wait by the rhythm the source had before this
                // delivery, so a one-off late delivery can't excuse itself.
                if (_knownLevel[i] < need && rhythm > lateRhythm) lateRhythm = rhythm;
            }
            _knownLevel[i] = level;
            _trimRhythm[i] = rhythm;

            if (level >= need)
            {
                anyReady = true;
            }
            else if (now - _lastBlockAt[i] < StarvationTimeout(_deliveryGap[i]))
            {
                // Live but late — wait, as long as its rhythm explains the delay.
                awaiting = true;
            }
        }

        if (!anyReady)
        {
            // Nothing is buffered anywhere, so no backlog is building up.
            _heldBackSince = 0;
            return false;
        }
        if (awaiting)
        {
            // Ready sources wait (and keep buffering) while a live one catches up.
            if (_heldBackSince == 0) _heldBackSince = now;
            return false;
        }

        // Coming out of a stall: trim each ring to what a rhythm explains — the
        // late source's, if one caught up just now, or the ring's own; at least
        // a block. The rest built up while the mix waited and would otherwise
        // become permanent output latency. A source that timed out explains nothing.
        if (_heldBackSince != 0)
        {
            if (now - _heldBackSince >= StallTrimThresholdTicks)
            {
                for (var i = 0; i < inCount; i++)
                {
                    if (_tickCaptures[i] is not { } cap) continue;
                    var keep = KeepBlocks(Math.Max(lateRhythm, _trimRhythm[i]), _blockTicks);
                    _knownLevel[i] -= TrimBacklog(cap.Ring, need, keep);
                }
            }
            _heldBackSince = 0;
        }

        // Pass 2: pull one stereo block per input.
        for (var i = 0; i < inCount; i++)
        {
            var cap = _tickCaptures[i];
            if (cap is not null && cap.Ring.Available >= need)
            {
                cap.Ring.Read(_inputInterleaved[i]);
                _knownLevel[i] -= need;
                Deinterleave(_inputInterleaved[i], _inputBlocksL[i], _inputBlocksR[i]);
                _lastBlockAt[i] = now;
            }
            else
            {
                Array.Clear(_inputBlocksL[i]);
                Array.Clear(_inputBlocksR[i]);
            }
        }

        // Latency probe — watch side. Read once per tick; apply to both the
        // capture-watch (here) and the inject (after MixBlock) so they're
        // anchored to the same probe instance.
        var probe = Volatile.Read(ref _probe);
        if (probe is not null)
            ProbeWatchInput(probe, inCount);

        // BE-050: meter inputs *before* DSP, one meter per side.
        for (var i = 0; i < inCount; i++)
        {
            _meters.Accumulate(i * 2,     _inputBlocksL[i]);
            _meters.Accumulate(i * 2 + 1, _inputBlocksR[i]);
        }

        // BE-081: spectrum tap for an input channel — pre-DSP so the FE's EQ
        // overlay reflects what's hitting the EQ.
        var spectrumTap = Volatile.Read(ref _spectrumTap);
        if (spectrumTap is { Bus: SpectrumFrame.BusTag.Input } inTap
            && (uint)inTap.Channel < (uint)inCount)
        {
            WriteSpectrum(_inputBlocksL[inTap.Channel], _inputBlocksR[inTap.Channel]);
        }

        // BE-076 / BE-080: per-input stereo chain — gate → EQ → compressor →
        // balance (with a smoother so slider drags stay click-free).
        for (var i = 0; i < inCount; i++)
        {
            var ch = i < state.Inputs.Length ? state.Inputs[i] : null;
            ApplyInputDsp(i, ch);

            var l = _inputBlocksL[i].AsSpan();
            var r = _inputBlocksR[i].AsSpan();
            _inputGates[i].ProcessStereo(l, r);
            _inputEqsL[i].Process(l);
            _inputEqsR[i].Process(r);
            _inputComps[i].ProcessStereo(l, r);
            ApplyBalance(_inputPanCurrentL, _inputPanCurrentR, _inputPanTargetL, _inputPanTargetR, i, l, r);
        }

        MixBlock(
            BlockFrames,
            state,
            inCount,
            outCount,
            _inputBlocksL,
            _inputBlocksR,
            _outputBlocksL,
            _outputBlocksR,
            _inputCurrentGain,
            _outputCurrentGain,
            GainRampCoefficient,
            RampSnapEpsilon);

        // BE-081: spectrum tap for an output channel — post-mix, captured
        // *before* the per-output DSP loop so the FE shows the bus content
        // that's about to hit the EQ.
        if (spectrumTap is { Bus: SpectrumFrame.BusTag.Output } outTap
            && (uint)outTap.Channel < (uint)outCount)
        {
            WriteSpectrum(_outputBlocksL[outTap.Channel], _outputBlocksR[outTap.Channel]);
        }

        // BE-076: per-output stereo chain — EQ → compressor → balance.
        for (var o = 0; o < outCount; o++)
        {
            var ch = o < state.Outputs.Length ? state.Outputs[o] : null;
            ApplyOutputDsp(o, ch);

            var bL = _outputBlocksL[o].AsSpan();
            var bR = _outputBlocksR[o].AsSpan();
            _outputEqsL[o].Process(bL);
            _outputEqsR[o].Process(bR);
            _outputComps[o].ProcessStereo(bL, bR);
            ApplyBalance(_outputPanCurrentL, _outputPanCurrentR, _outputPanTargetL, _outputPanTargetR, o, bL, bR);
        }

        // Latency probe — inject side. Sits AFTER per-output DSP so the
        // burst hits the render ring unaltered (no EQ/compressor mauls the
        // detection peak). Wipes the chosen output's block first so user
        // audio doesn't bleed into the test signal during the watch window.
        if (probe is not null)
            ProbeInjectOutput(probe, outCount);

        // BE-080: meter outputs from the final L/R blocks.
        var outputMeterBase = _captures.Length * 2;
        for (var o = 0; o < outCount; o++)
        {
            _meters.Accumulate(outputMeterBase + o * 2,     _outputBlocksL[o]);
            _meters.Accumulate(outputMeterBase + o * 2 + 1, _outputBlocksR[o]);
        }
        _meters.OnBlockComplete(BlockFrames);

        // Interleave L,R into the render ring as float pairs. The ring is
        // sized in floats (2 floats per stereo frame); the device pulls pairs
        // and expands to its channel count. Missing render slots discard the
        // post-mix block — the engine still ran the channel through DSP, the
        // user just doesn't hear it.
        for (var o = 0; o < outCount; o++)
        {
            if (Volatile.Read(ref _renders[o]) is not { } render) continue;
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
    /// Slot <paramref name="i"/>'s source delivered <paramref name="samples"/>
    /// since the mix last looked: learn its rhythm from the gap since its previous
    /// delivery. A longer gap moves the rhythm halfway toward it — a one-off late
    /// delivery shouldn't redefine a steady source — and a shorter one eases it
    /// down by 1/16 of the difference. Capped at <see cref="MaxDeliveryGapMs"/>.
    /// <para>
    /// Not rhythm, and ignored: gaps over <see cref="PauseGapMs"/>, deliveries the
    /// mix had already stopped waiting for, and deliveries carrying less than half
    /// their gap in audio — an app playing short sounds or sparse packets keeps its
    /// short timeout. A source's first delivery is always such a pause; it seeds
    /// the rhythm from the audio it carries, so a new source is judged like any
    /// other from its second delivery on, and one that delivers in big bursts is
    /// waited for from the start.
    /// </para>
    /// </summary>
    private void OnDelivery(int i, long now, int samples)
    {
        var gap = now - _lastDeliveryAt[i];
        _lastDeliveryAt[i] = now;

        var rhythm = _deliveryGap[i];
        if (gap > PauseGapTicks
            || now - _lastBlockAt[i] >= StarvationTimeout(rhythm)
            || 2L * samples * _blockTicks < gap * (BlockFrames * 2))
        {
            if (rhythm == 0)
                _deliveryGap[i] = Math.Clamp(samples * _blockTicks / (BlockFrames * 2), 1L, MaxDeliveryGapTicks);
            return;
        }

        if (gap > MaxDeliveryGapTicks) gap = MaxDeliveryGapTicks;
        _deliveryGap[i] = gap >= rhythm
            ? rhythm + (gap - rhythm) / 2
            : rhythm - ((rhythm - gap) >> 4);
    }

    /// <summary>
    /// Blocks a stall trim leaves in a ring whose backlog <paramref name="rhythm"/>
    /// explains: one delivery's worth, rounded to the nearest block, at least one.
    /// Rounding up would keep a whole extra block of latency for a rhythm just over
    /// a block (jitter, or a rhythm still easing back down after a hiccup).
    /// </summary>
    internal static int KeepBlocks(long rhythm, long blockTicks)
        => (int)Math.Max(1, (rhythm + blockTicks / 2) / blockTicks);

    /// <summary>How long the mix waits for a live source that's short of a block: <see cref="StarvedSourceTimeoutMs"/>, or twice its rhythm if longer.</summary>
    private static long StarvationTimeout(long rhythm) => Math.Max(StarvedSourceTimeoutTicks, 2 * rhythm);

    private static long MsToTicks(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;

    /// <summary>
    /// Leave between <paramref name="keepBlocks"/> and one more block in
    /// <paramref name="ring"/>, dropping the oldest samples. Discards whole
    /// blocks only, so interleaved frames stay aligned. Returns how many samples
    /// were dropped.
    /// </summary>
    internal static int TrimBacklog(RingBuffer ring, int blockSamples, int keepBlocks = 1)
    {
        var excess = ring.Available - blockSamples * keepBlocks;
        if (excess < blockSamples) return 0;
        return ring.Discard(excess - excess % blockSamples);
    }

    private static void Deinterleave(float[] interleaved, float[] left, float[] right)
    {
        for (var s = 0; s < left.Length; s++)
        {
            left[s]  = interleaved[s * 2];
            right[s] = interleaved[s * 2 + 1];
        }
    }

    private void WriteSpectrum(float[] left, float[] right)
    {
        var mono = _monoScratch;
        for (var s = 0; s < mono.Length; s++) mono[s] = (left[s] + right[s]) * 0.5f;
        _spectrumBuffer.Write(mono);
    }

    /// <summary>
    /// Copy the latest published meter pairs of the live channels in the wire
    /// layout <c>[in0_L, in0_R, in1_L, …, out0_L, out0_R, …]</c> — each side a
    /// (peak, RMS) pair. Returns the number of sides written.
    /// <paramref name="pairs"/> must hold <c>2 × <see cref="MeterSideCapacity"/></c>
    /// floats.
    /// </summary>
    public int SnapshotMeters(Span<float> pairs, out uint frameId)
    {
        var inSides  = Volatile.Read(ref _inputCount)  * 2;
        var outSides = Volatile.Read(ref _outputCount) * 2;
        _meters.ReadPairs(0, pairs.Slice(0, inSides * 2));
        _meters.ReadPairs(_captures.Length * 2, pairs.Slice(inSides * 2, outSides * 2));
        frameId = _meters.FrameId;
        return inSides + outSides;
    }

    /// <summary>
    /// Latency probe — capture watch. Append this tick's pre-DSP input block
    /// for the watched channel (the louder side per sample, so a return path
    /// wired to one side only still reads at full level) into the probe's
    /// watch buffer until it's full, then complete the awaitable so the RPC
    /// handler scans for the burst and reports the result.
    /// </summary>
    private void ProbeWatchInput(LatencyProbe probe, int inCount)
    {
        if ((uint)probe.WatchInputIndex >= (uint)inCount) return;

        var left      = _inputBlocksL[probe.WatchInputIndex];
        var right     = _inputBlocksR[probe.WatchInputIndex];
        var dst       = probe.WatchBuffer;
        var position  = probe.WatchPosition;
        var remaining = dst.Length - position;
        if (remaining <= 0) return;

        var toCopy = Math.Min(BlockFrames, remaining);
        for (var s = 0; s < toCopy; s++)
        {
            var l = left[s];
            var r = right[s];
            dst[position + s] = MathF.Abs(l) >= MathF.Abs(r) ? l : r;
        }
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
    private void ProbeInjectOutput(LatencyProbe probe, int outCount)
    {
        if ((uint)probe.InjectOutputIndex >= (uint)outCount) return;

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
            _inputEqsL[i] .Apply(ch?.Eq);
            _inputEqsR[i] .Apply(ch?.Eq);
            _inputComps[i].Apply(ch?.Compressor);
            _lastAppliedInput[i] = ch;
        }

        // Balance target re-evaluated every tick — cheap, and it keeps the
        // smoother responsive even if Apply is short-circuited above.
        Pan.ComputeBalance(ch?.Pan ?? 0f, out var gL, out var gR);
        _inputPanTargetL[i] = gL;
        _inputPanTargetR[i] = gR;
    }

    /// <summary>Same as <see cref="ApplyInputDsp"/> for the per-output chain.</summary>
    private void ApplyOutputDsp(int o, Channel? ch)
    {
        var last = _lastAppliedOutput[o];
        if (!ReferenceEquals(ch, last))
        {
            _outputEqsL[o] .Apply(ch?.Eq);
            _outputEqsR[o] .Apply(ch?.Eq);
            _outputComps[o].Apply(ch?.Compressor);
            _lastAppliedOutput[o] = ch;
        }

        Pan.ComputeBalance(ch?.Pan ?? 0f, out var gL, out var gR);
        _outputPanTargetL[o] = gL;
        _outputPanTargetR[o] = gR;
    }

    /// <summary>
    /// Apply the smoothed balance gains for slot <paramref name="index"/> to a
    /// stereo block in place. At centre both gains are 1.0 (no centre dip);
    /// towards an extreme the opposite side fades to zero. The smoother fades
    /// current → target over <see cref="PanFadeSamples"/> samples (BE-078) so
    /// sliding the control doesn't click.
    /// </summary>
    private static void ApplyBalance(
        float[] currentL, float[] currentR,
        float[] targetL,  float[] targetR,
        int index, Span<float> left, Span<float> right)
    {
        var curL = currentL[index];
        var curR = currentR[index];
        var tarL = targetL[index];
        var tarR = targetR[index];

        if (curL == tarL && curR == tarR)
        {
            if (tarL != 1f)
                for (var s = 0; s < left.Length; s++) left[s] *= tarL;
            if (tarR != 1f)
                for (var s = 0; s < right.Length; s++) right[s] *= tarR;
            return;
        }

        var stepL = (tarL - curL) / PanFadeSamples;
        var stepR = (tarR - curR) / PanFadeSamples;
        for (var s = 0; s < left.Length; s++)
        {
            curL += stepL;
            if ((stepL > 0f && curL > tarL) || (stepL < 0f && curL < tarL)) curL = tarL;
            curR += stepR;
            if ((stepR > 0f && curR > tarR) || (stepR < 0f && curR < tarR)) curR = tarR;
            left[s]  *= curL;
            right[s] *= curR;
        }
        currentL[index] = curL;
        currentR[index] = curR;
    }

    /// <summary>
    /// <see cref="MixBlock(int, MixerState, int, int, float[][], float[][], float[][], float[][], float[], float[], float, float)"/>
    /// over every block in the supplied arrays.
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
        => MixBlock(
            blockFrames, state, inputBlocksL.Length, outputBlocksL.Length,
            inputBlocksL, inputBlocksR, outputBlocksL, outputBlocksR,
            inputCurrentGain, outputCurrentGain, rampCoefficient, rampSnapEpsilon);

    /// <summary>
    /// Pure block-mix in stereo. Reads <paramref name="state"/>, applies the
    /// routing matrix and per-channel gain/mute/solo (with one-pole ramping)
    /// over <paramref name="blockFrames"/> samples for the first
    /// <paramref name="inputCount"/> inputs and <paramref name="outputCount"/>
    /// outputs, and writes the per-output L/R accumulators into
    /// <paramref name="outputBlocksL"/> / <paramref name="outputBlocksR"/>.
    /// Smoother state advances by exactly one block. No allocations.
    ///
    /// Pulled out of <see cref="Tick"/> so unit tests can exercise it
    /// directly without WASAPI / NAudio in the loop.
    /// </summary>
    public static void MixBlock(
        int blockFrames,
        MixerState state,
        int inputCount,
        int outputCount,
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

        for (var o = 0; o < outputCount; o++)
        {
            Array.Clear(outputBlocksL[o]);
            Array.Clear(outputBlocksR[o]);
        }

        var anyInputSoloed = SoloLogic.HasAnySolo(inputs);

        var matIn  = Math.Min(matrix.Inputs,  inputCount);
        var matOut = Math.Min(matrix.Outputs, outputCount);

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

        for (var o = 0; o < outputCount; o++)
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
