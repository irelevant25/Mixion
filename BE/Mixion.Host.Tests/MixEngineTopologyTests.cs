using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mixion.Host.Audio;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Runs the real mix thread against in-memory sources and sinks: a stereo
/// image survives the whole engine, a source that stops delivering can't stall
/// the others or leave latency behind, and sources can be swapped or attached
/// while audio flows.
/// </summary>
public class MixEngineTopologyTests
{
    // 128-frame blocks are exactly 10 ms at this rate, so the synthetic delivery
    // schedules in the Tick_* tests read in milliseconds.
    private const int Rate  = 12_800;
    private const int Block = 128;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly record struct Frame(float Left, float Right);

    [Fact]
    public void LeftOnlyInput_StaysOnTheLeft()
    {
        var input  = new FakeCapture("in0");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { input }, new[] { output }, routed: true);
        engine.Start();

        var frame = PumpUntil(new[] { input }, output, 0.5f, 0f, f => f.Left > 0f);

        Assert.Equal(0.5f, frame.Left, 4);
        Assert.Equal(0f, frame.Right);
    }

    [Fact]
    public void Meters_ReportEachSideSeparately()
    {
        var input  = new FakeCapture("in0");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { input }, new[] { output }, routed: true);
        engine.Start();

        var pairs    = new float[engine.MeterSideCapacity * 2];
        var sides    = 0;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < Timeout)
        {
            PumpOnce(new[] { input }, output, 0f, 0.5f);
            sides = engine.SnapshotMeters(pairs, out var frameId);
            if (frameId > 1 && pairs[6] > 0f) break;
            Thread.Sleep(1);
        }

        Assert.Equal(4, sides);                 // input L/R, output L/R
        Assert.Equal(0f, pairs[0]);             // input L peak
        Assert.Equal(0.5f, pairs[2], 4);        // input R peak
        Assert.Equal(0f, pairs[4]);             // output L peak
        Assert.Equal(0.5f, pairs[6], 4);        // output R peak
    }

    [Fact]
    public void SourceThatNeverDelivers_DoesNotStallTheOthers()
    {
        var live   = new FakeCapture("live");
        var silent = new FakeCapture("silent");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { live, silent }, new[] { output }, routed: true);
        engine.Start();

        var frame = PumpUntil(new[] { live }, output, 0.5f, 0.5f, f => f.Left > 0f);

        Assert.Equal(0.5f, frame.Left, 4);
    }

    [Fact]
    public void SourceThatStopsDelivering_IsSkippedAfterItsTimeout()
    {
        var a      = new FakeCapture("a");
        var b      = new FakeCapture("b");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { a, b }, new[] { output }, routed: true);
        engine.Start();

        // Both deliver: the output carries their sum.
        PumpUntil(new[] { a, b }, output, 0.25f, 0.25f, f => f.Left > 0.4f);

        // b goes quiet (an app closed, a device unplugged); a keeps delivering.
        var frame = PumpUntil(new[] { a }, output, 0.25f, 0.25f, f => f.Left is > 0.2f and < 0.3f);

        Assert.Equal(0.25f, frame.Left, 4);
    }

    [Fact]
    public void LongHoldBack_DropsTheBacklogInsteadOfMixingItLate()
    {
        var a      = new FakeCapture("a");
        var b      = new FakeCapture("b");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { a, b }, new[] { output }, routed: true);
        engine.Start();
        PumpUntil(new[] { a, b }, output, 0.25f, 0.25f, f => f.Left > 0.4f);
        WaitUntil(() => a.Ring.Available < Block * 2 && b.Ring.Available < Block * 2);
        DrainOutput(output);

        // b delivers one more block and goes quiet while a delivers twenty at
        // once. The engine holds a back until b counts as starved; a's backlog
        // must then be dropped, not mixed as twenty blocks of late audio.
        b.Push(Block, 0.25f, 0.25f);
        a.Push(Block * 20, 0.25f, 0.25f);
        WaitUntil(() => a.Ring.Available < Block * 2);
        Thread.Sleep(MixEngine.StarvedSourceTimeoutMs);

        var rendered = output.Ring.Available / (Block * 2);
        Assert.InRange(rendered, 1, 3);
    }

    [Fact]
    public void Tick_WhenALiveSourceStops_DropsTheBacklogBuiltWhileWaiting()
    {
        var a      = new FakeCapture("a");
        var b      = new FakeCapture("b");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { a, b }, new[] { output }, routed: true);

        // Both deliver a block every 10 ms.
        var rendered = Drive(engine, output, 0, 200, ms =>
        {
            if (ms % 10 != 0) return;
            a.Push(Block, 0.25f, 0.25f);
            b.Push(Block, 0.25f, 0.25f);
        });
        Assert.Equal(20, rendered);

        // b stops; a keeps delivering. The mix waits for b until 50 ms after its
        // last block (ms 240), then drops the four blocks a buffered meanwhile.
        rendered = Drive(engine, output, 200, 400, ms =>
        {
            if (ms % 10 == 0) a.Push(Block, 0.25f, 0.25f);
        });
        Assert.Equal(16, rendered);
        Assert.Equal(0, a.Ring.Available);
    }

    [Fact]
    public void Tick_ASteadySourceThatCatchesUpLate_StillDropsTheBacklog()
    {
        var a      = new FakeCapture("a");
        var b      = new FakeCapture("b");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { a, b }, new[] { output }, routed: true);

        // Both deliver a block every 10 ms.
        var rendered = Drive(engine, output, 0, 200, ms =>
        {
            if (ms % 10 != 0) return;
            a.Push(Block, 0.25f, 0.25f);
            b.Push(Block, 0.25f, 0.25f);
        });
        Assert.Equal(20, rendered);

        // b misses three deliveries, then catches up 35 ms late with four blocks at
        // once — late, but inside its timeout. Its usual 10 ms rhythm doesn't explain
        // that wait, so both backlogs are cut to a block instead of becoming latency.
        rendered = Drive(engine, output, 200, 400, ms =>
        {
            if (ms % 10 == 0) a.Push(Block, 0.25f, 0.25f);
            if (ms == 200 || (ms >= 250 && ms % 10 == 0)) b.Push(Block, 0.25f, 0.25f);
            if (ms == 245) b.Push(Block * 4, 0.25f, 0.25f);
        });
        Assert.Equal(17, rendered);
        Assert.Equal(0, a.Ring.Available);
        Assert.Equal(0, b.Ring.Available);
    }

    [Fact]
    public void Tick_AnAppThatPausesBetweenSounds_KeepsItsShortTimeout()
    {
        var steady = new FakeCapture("steady");
        var app    = new FakeCapture("app");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { steady, app }, new[] { output }, routed: true);

        // The app plays 60 ms sounds (a block every 10 ms) with 100 ms pauses. Each
        // pause holds every output back for the 50 ms timeout, never for the whole
        // pause: the delivery that ends a pause is not learned as the app's rhythm.
        void Deliver(int ms)
        {
            if (ms % 10 != 0) return;
            steady.Push(Block, 0.25f, 0.25f);
            if (ms % 160 < 60) app.Push(Block, 0.25f, 0.25f);
        }

        Assert.Equal(24, Drive(engine, output, 0, 320, Deliver));    // two sound-and-pause cycles
        Assert.Equal(6,  Drive(engine, output, 320, 380, Deliver));  // a sound
        Assert.Equal(0,  Drive(engine, output, 380, 420, Deliver));  // waiting 50 ms for the app
        Assert.Equal(6,  Drive(engine, output, 420, 480, Deliver));  // skipped: backlog trimmed, steady mixes on
    }

    [Fact]
    public void Tick_ANewSourceLateOnItsSecondDelivery_StillDropsTheBacklog()
    {
        var a      = new FakeCapture("a");
        var b      = new FakeCapture("b");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { a, b }, new[] { output }, routed: true);

        // Both start together, but b's second delivery comes 45 ms after its first,
        // carrying the four blocks it owes. With no gap measured yet, that late
        // delivery must not become b's rhythm and excuse the wait: the backlog is cut
        // to a block, so 7 blocks come out of the first 100 ms instead of 10.
        var rendered = Drive(engine, output, 0, 100, ms =>
        {
            if (ms % 10 == 0) a.Push(Block, 0.25f, 0.25f);
            if (ms == 0 || (ms >= 50 && ms % 10 == 0)) b.Push(Block, 0.25f, 0.25f);
            if (ms == 45) b.Push(Block * 4, 0.25f, 0.25f);
        });

        Assert.Equal(7, rendered);
        Assert.Equal(0, a.Ring.Available);
        Assert.Equal(0, b.Ring.Available);
    }

    [Fact]
    public void Tick_TwoBurstySourcesAndNothingSteady_KeepTheirAudio()
    {
        var x      = new FakeCapture("x");
        var y      = new FakeCapture("y");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { x, y }, new[] { output }, routed: true);

        // Each delivers 50 ms of audio at a time, 2 ms apart. Every burst is mixed as
        // soon as its partner arrives, so the next one always comes a full 50 ms after
        // the mix last took a block — only the size of a first burst can teach the
        // rhythm before the default 50 ms timeout gives up on it.
        var rendered = Drive(engine, output, 0, 1000, ms =>
        {
            if (ms % 50 == 0) x.Push(Block * 5, 0.25f, 0.25f);
            if (ms % 50 == 2) y.Push(Block * 5, 0.25f, 0.25f);
        });

        Assert.Equal(100, rendered);                       // all of x's audio
        Assert.Equal(0, x.Ring.Available);
        Assert.Equal(Block * 2 * 5, y.Ring.Available);     // y's last burst waits for x's next
    }

    [Fact]
    public void Tick_SparsePacketsFromAnApp_DontStretchItsTimeout()
    {
        var steady = new FakeCapture("steady");
        var app    = new FakeCapture("app");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { steady, app }, new[] { output }, routed: true);

        // The app sends single 10 ms packets, first 45 ms apart, then 85 ms apart. A
        // packet carrying much less audio than the time since the last one is not a
        // rhythm, so the app keeps its 50 ms timeout: once its packets are 85 ms
        // apart, the steady source is held back 50 ms, not the whole gap.
        void Deliver(int ms)
        {
            if (ms % 10 == 0) steady.Push(Block, 0.25f, 0.25f);
            if (ms is 0 or 45 or 90 or 135 or 180 or 265) app.Push(Block, 0.25f, 0.25f);
        }

        Assert.Equal(5, Drive(engine, output, 0, 190, Deliver));
        Assert.Equal(4, Drive(engine, output, 190, 265, Deliver));  // held back until ms 230, then mixing again
    }

    [Fact]
    public void Tick_ASourceThatDeliversInBursts_KeepsItsAudio()
    {
        var steady = new FakeCapture("steady");
        var bursty = new FakeCapture("bursty");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { steady, bursty }, new[] { output }, routed: true);

        // steady: a block every 10 ms. bursty: six blocks at a time, 95 then 25 ms
        // apart — the same rate, but the mix regularly waits 35 ms for it, longer
        // than a stall for a source with a 10 ms rhythm. Nothing may be dropped.
        var rendered = Drive(engine, output, 0, 1200, ms =>
        {
            if (ms % 10 == 0) steady.Push(Block, 0.25f, 0.25f);
            if (ms % 120 is 0 or 95) bursty.Push(Block * 6, 0.25f, 0.25f);
        });

        Assert.Equal(120, rendered);
        Assert.Equal(0, steady.Ring.Available);
        Assert.Equal(0, bursty.Ring.Available);
    }

    [Fact]
    public void Tick_ALateBurst_IsWaitedForWhenThatSourceUsuallyDeliversSlowly()
    {
        var steady = new FakeCapture("steady");
        var bursty = new FakeCapture("bursty");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { steady, bursty }, new[] { output }, routed: true);
        void Steady(int ms)
        {
            if (ms % 10 == 0) steady.Push(Block, 0.25f, 0.25f);
        }

        // bursty delivers six blocks every 60 ms; the engine learns that rhythm.
        var rendered = Drive(engine, output, 0, 240, ms =>
        {
            Steady(ms);
            if (ms % 60 == 0) bursty.Push(Block * 6, 0.25f, 0.25f);
        });
        Assert.Equal(24, rendered);

        // Its next burst is 55 ms late. For a 10 ms source that would be a stop
        // (skipped after 50 ms, backlog trimmed); for this one the mix just waits.
        rendered = Drive(engine, output, 240, 295, Steady);
        Assert.Equal(0, rendered);

        rendered = Drive(engine, output, 295, 300, ms =>
        {
            Steady(ms);
            if (ms == 295) bursty.Push(Block * 6, 0.25f, 0.25f);
        });
        Assert.Equal(6, rendered);
        Assert.Equal(0, steady.Ring.Available);
        Assert.Equal(0, bursty.Ring.Available);
    }

    [Fact]
    public void TrimBacklog_KeepsTheNewestOneToTwoBlocks()
    {
        var need = Block * 2;
        var ring = new RingBuffer(Block * 64);
        var data = new float[need * 5 + 2];                // five blocks and one extra frame
        for (var i = 0; i < data.Length; i++) data[i] = i;
        ring.Write(data);

        MixEngine.TrimBacklog(ring, need);

        Assert.Equal(need + 2, ring.Available);            // one block plus the extra frame
        var rest = new float[ring.Available];
        ring.Read(rest);
        Assert.Equal(data[^rest.Length..], rest);          // the newest samples, frames still aligned
    }

    [Theory]
    [InlineData(0.0,  1)]   // no rhythm yet: one block
    [InlineData(10.0, 1)]   // exactly a block
    [InlineData(10.8, 1)]   // a jittery 10 ms source: no extra block of latency
    [InlineData(14.5, 1)]
    [InlineData(15.5, 2)]
    [InlineData(57.8, 6)]   // a source that delivers ~60 ms at a time keeps its burst
    public void KeepBlocks_RoundsTheRhythmToTheNearestBlock(double rhythmMs, int expected)
    {
        var block  = Stopwatch.Frequency / 100;                            // 10 ms
        var rhythm = (long)(Stopwatch.Frequency * rhythmMs / 1000);

        Assert.Equal(expected, MixEngine.KeepBlocks(rhythm, block));
    }

    [Fact]
    public void TrimBacklog_KeepsTheRequestedNumberOfBlocks()
    {
        var need = Block * 2;
        var ring = new RingBuffer(Block * 64);
        ring.Write(new float[need * 10 + 2]);              // ten blocks and one extra frame

        var dropped = MixEngine.TrimBacklog(ring, need, keepBlocks: 3);

        Assert.Equal(need * 7, dropped);
        Assert.Equal(need * 3 + 2, ring.Available);
    }

    [Fact]
    public void TrimBacklog_LeavesShortRingsAlone()
    {
        var need = Block * 2;
        var ring = new RingBuffer(Block * 64);
        ring.Write(new float[need * 2 - 2]);

        MixEngine.TrimBacklog(ring, need);

        Assert.Equal(need * 2 - 2, ring.Available);
    }

    [Fact]
    public void ReplaceCapture_SwapsTheSourceWhileRunning()
    {
        var first  = new FakeCapture("in0");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { first }, new[] { output }, routed: true);
        engine.Start();
        PumpUntil(new[] { first }, output, 0.5f, 0f, f => f.Left > 0.4f);

        var second   = new FakeCapture("in0");
        var previous = engine.ReplaceCapture(0, second);

        Assert.Same(first, previous);
        Assert.True(second.Started);
        var frame = PumpUntil(new[] { second }, output, 0f, 0.5f, f => f.Right > 0.4f && f.Left == 0f);
        Assert.Equal(0.5f, frame.Right, 4);
    }

    [Fact]
    public void AppendedChannel_IsMixedOnceTheStateRoutesIt()
    {
        var first  = new FakeCapture("in0");
        var output = new FakeRender("out0");
        using var engine = CreateEngine(new[] { first }, new[] { output }, routed: false);
        engine.Start();

        var added   = new FakeCapture("in1");
        var channel = Channel("in1");
        var index   = engine.TryAppendCapture(added, channel);
        engine.UpdateState(s => new MixerState(
            s.Inputs.Add(channel),
            s.Outputs,
            s.Matrix.Resize(s.Inputs.Length + 1, s.Outputs.Length).With(index, 0, true)));

        Assert.Equal(1, index);
        Assert.True(added.Started);
        var frame = PumpUntil(new[] { first, added }, output, 0.5f, 0.5f, f => f.Left > 0.4f);
        Assert.Equal(0.5f, frame.Left, 4);
    }

    [Fact]
    public void TryAppendCapture_StopsWhenSpareSlotsRunOut()
    {
        using var engine = new MixEngine(
            new[] { new FakeCapture("in0") },
            new[] { new FakeRender("out0") },
            State(new[] { "in0" }, new[] { "out0" }, routed: false),
            NullLogger.Instance,
            spareInputSlots: 2,
            spareOutputSlots: 0);

        Assert.Equal(1,  engine.TryAppendCapture(new FakeCapture("in1"), Channel("in1")));
        Assert.Equal(2,  engine.TryAppendCapture(null, Channel("in2")));
        Assert.Equal(-1, engine.TryAppendCapture(new FakeCapture("in3"), Channel("in3")));
        Assert.Equal(-1, engine.TryAppendRender(new FakeRender("out1"), Channel("out1")));
        Assert.Equal(3, engine.InputCount);
        Assert.Equal(1, engine.OutputCount);
    }

    [Fact]
    public void UpdateState_RejectsChannelsWithoutSlots()
    {
        using var engine = new MixEngine(
            new[] { new FakeCapture("in0") },
            new[] { new FakeRender("out0") },
            State(new[] { "in0" }, new[] { "out0" }, routed: false),
            NullLogger.Instance);

        Assert.Throws<InvalidOperationException>(() =>
            engine.UpdateState(s => s with { Inputs = s.Inputs.Add(Channel("ghost")) }));
    }

    [Fact]
    public void Dispose_ReleasesAttachedSourcesAndDevices()
    {
        var input  = new FakeCapture("in0");
        var added  = new FakeCapture("in1");
        var output = new FakeRender("out0");
        var engine = CreateEngine(new[] { input }, new[] { output }, routed: false);
        engine.Start();
        engine.TryAppendCapture(added, Channel("in1"));

        engine.Dispose();

        Assert.True(input.Disposed);
        Assert.True(added.Disposed);
        Assert.True(output.Disposed);
    }

    // ---------------------------------------------------------------- helpers

    private static Channel Channel(string id) => new(id, id, GainDb: 0f, Muted: false, Soloed: false);

    private static MixerState State(string[] inputs, string[] outputs, bool routed)
    {
        var matrix = new RoutingMatrix(inputs.Length, outputs.Length);
        if (routed)
        {
            for (var i = 0; i < inputs.Length; i++)
                for (var o = 0; o < outputs.Length; o++)
                    matrix = matrix.With(i, o, true);
        }
        return new MixerState(
            inputs.Select(Channel).ToImmutableArray(),
            outputs.Select(Channel).ToImmutableArray(),
            matrix);
    }

    private static MixEngine CreateEngine(FakeCapture[] captures, FakeRender[] renders, bool routed)
        => new(
            captures,
            renders,
            State(captures.Select(c => c.Id).ToArray(), renders.Select(r => r.Id).ToArray(), routed),
            NullLogger.Instance);

    /// <summary>Feed every source one block (when it has room) and drain rendered blocks, returning the last one.</summary>
    private static Frame? PumpOnce(FakeCapture[] sources, FakeRender output, float left, float right)
    {
        foreach (var s in sources)
            if (s.Ring.FreeSpace >= Block * 2) s.Push(Block, left, right);

        Frame? last = null;
        var buffer = new float[Block * 2];
        while (output.Ring.Available >= buffer.Length)
        {
            output.Ring.Read(buffer);
            last = new Frame(buffer[^2], buffer[^1]);
        }
        return last;
    }

    private static Frame PumpUntil(FakeCapture[] sources, FakeRender output, float left, float right, Func<Frame, bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < Timeout)
        {
            if (PumpOnce(sources, output, left, right) is { } frame && done(frame)) return frame;
            Thread.Sleep(1);
        }
        throw new TimeoutException("The engine never rendered the expected block.");
    }

    /// <summary>
    /// Runs the tick by hand on a synthetic clock, one millisecond at a time from
    /// <paramref name="fromMs"/> to <paramref name="toMs"/> (exclusive):
    /// <paramref name="deliver"/> pushes what the sources deliver at that
    /// millisecond, then the engine mixes everything it can. Returns the number
    /// of blocks rendered. The engine must not be started — its own mix thread
    /// would race the manual ticks.
    /// </summary>
    private static int Drive(MixEngine engine, FakeRender output, int fromMs, int toMs, Action<int> deliver)
    {
        var rendered = 0;
        var buffer   = new float[Block * 2];
        for (var ms = fromMs; ms < toMs; ms++)
        {
            deliver(ms);
            var now = Stopwatch.Frequency * (1000 + ms) / 1000;
            while (engine.Tick(now)) { }
            while (output.Ring.Available >= buffer.Length)
            {
                output.Ring.Read(buffer);
                rendered++;
            }
        }
        return rendered;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > Timeout) throw new TimeoutException("The engine never reached the expected state.");
            Thread.Sleep(1);
        }
    }

    private static void DrainOutput(FakeRender output)
    {
        var buffer = new float[Block * 2];
        while (output.Ring.Available >= buffer.Length) output.Ring.Read(buffer);
    }

    private sealed class FakeCapture : IAudioCaptureSource
    {
        public FakeCapture(string id) => Id = id;

        public string     Id                 { get; }
        public string     FriendlyName       => Id;
        public int        SampleRate         => Rate;
        public int        SourceChannels     => 2;
        public int        BitsPerSample      => 32;
        public RingBuffer Ring               { get; } = new(Block * 64);
        public int        BufferMilliseconds => 3;
        public int        BufferFrames       => Block;
        public bool       IsFaulted          => false;
        public bool       Started            { get; private set; }
        public bool       Disposed           { get; private set; }
        public event EventHandler? DataReady;

        public void Push(int frames, float left, float right)
        {
            var interleaved = new float[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                interleaved[i * 2]     = left;
                interleaved[i * 2 + 1] = right;
            }
            Ring.Write(interleaved);
            DataReady?.Invoke(this, EventArgs.Empty);
        }

        public void Start()   => Started = true;
        public void Stop()    => Started = false;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeRender : IRenderDevice
    {
        public FakeRender(string id) => Id = id;

        public string     Id            { get; }
        public string     FriendlyName  => Id;
        public int        SampleRate    => Rate;
        public int        DestChannels  => 2;
        public int        BitsPerSample => 32;
        public RingBuffer Ring          { get; } = new(Block * 64);
        public int        LatencyMs     => 3;
        public int        BufferFrames  => Block;
        public RenderMode Mode          => RenderMode.Shared;
        public string?    ExclusiveFallbackReason { get; set; }
        public bool       IsFaulted     => false;
        public bool       Disposed      { get; private set; }

        public void Start() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
