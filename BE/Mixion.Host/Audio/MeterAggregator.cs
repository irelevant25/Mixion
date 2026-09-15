using System.Runtime.CompilerServices;

namespace Mixion.Host.Audio;

/// <summary>
/// Per-channel peak (max-abs) and RMS (root-mean-square) aggregator over a
/// rolling ~33 ms window. Sized for a fixed channel count and sample rate
/// at construction; never allocates after that.
///
/// Threading:
///   • Producer (mix thread): <see cref="Accumulate"/> per block, then
///     <see cref="OnBlockComplete"/> once per tick. These touch private
///     audio-thread state only.
///   • Consumer (telemetry thread): <see cref="ReadPairs"/> /
///     <see cref="TrySnapshot"/> read the latest published pairs atomically.
///
/// The publish step packs a (peak, rms) pair into a single 64-bit slot per
/// channel and swaps it via <see cref="Interlocked.Exchange(ref long, long)"/>.
/// The consumer reads each slot with <see cref="Interlocked.Read(ref long)"/>.
/// That gives a torn-free view of one channel; channels are independent of
/// each other (acceptable for VU meters — at most one tick of skew between
/// channels in a single binary frame).
/// </summary>
public sealed class MeterAggregator
{
    /// <summary>
    /// Window in samples — set from sample rate × ~33 ms. The first window
    /// after startup may be slightly shorter as the audio thread fills it.
    /// </summary>
    private readonly int _windowSamples;

    // Audio-thread accumulators. No volatile / no interlocked: only the mix
    // thread touches these.
    private readonly float[] _peakAccum;
    private readonly float[] _sumSqAccum;
    private int              _samplesInWindow;

    // Published pairs, one per channel: (peak | rms<<32). Exchanged
    // atomically; consumers read with Interlocked.Read.
    private readonly long[] _publishedPairs;
    private long _frameId; // monotonic; advances once per published window.

    public int ChannelCount  { get; }
    public int WindowSamples => _windowSamples;

    /// <summary>Id of the most recently published window. Monotonic.</summary>
    public uint FrameId => (uint)Interlocked.Read(ref _frameId);

    public MeterAggregator(int channelCount, int sampleRate, int windowMs = 33)
    {
        if (channelCount < 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (sampleRate  <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (windowMs    <= 0) throw new ArgumentOutOfRangeException(nameof(windowMs));

        ChannelCount = channelCount;
        // Round up to at least 1 — ensures the very first OnBlockComplete
        // can publish even with absurdly small inputs.
        _windowSamples = Math.Max(1, sampleRate * windowMs / 1000);

        _peakAccum      = new float[channelCount];
        _sumSqAccum     = new float[channelCount];
        _publishedPairs = new long[channelCount];
    }

    /// <summary>
    /// Audio thread: fold one block of samples for <paramref name="channel"/>
    /// into the running peak / sum-of-squares accumulators.
    /// </summary>
    public void Accumulate(int channel, ReadOnlySpan<float> block)
    {
        if ((uint)channel >= (uint)ChannelCount) return;

        var peak  = _peakAccum[channel];
        var sumSq = _sumSqAccum[channel];

        for (var i = 0; i < block.Length; i++)
        {
            var v = block[i];
            var a = MathF.Abs(v);
            if (a > peak) peak = a;
            sumSq += v * v;
        }

        _peakAccum[channel]  = peak;
        _sumSqAccum[channel] = sumSq;
    }

    /// <summary>
    /// Audio thread: advance the window by <paramref name="blockFrames"/>
    /// samples. When the window is full, publish a fresh per-channel
    /// (peak, rms) pair and reset the accumulators. Idempotent if the window
    /// hasn't elapsed.
    /// </summary>
    public void OnBlockComplete(int blockFrames)
    {
        _samplesInWindow += blockFrames;
        if (_samplesInWindow < _windowSamples) return;

        var inv = 1f / _samplesInWindow;
        for (var c = 0; c < ChannelCount; c++)
        {
            var peak = _peakAccum[c];
            var rms  = MathF.Sqrt(_sumSqAccum[c] * inv);

            var packed = PackPair(peak, rms);
            Interlocked.Exchange(ref _publishedPairs[c], packed);

            _peakAccum[c]  = 0f;
            _sumSqAccum[c] = 0f;
        }

        _samplesInWindow = 0;
        Interlocked.Increment(ref _frameId);
    }

    /// <summary>
    /// Telemetry thread: copy the latest published pairs for
    /// <c>dst.Length / 2</c> consecutive channels, starting at
    /// <paramref name="firstChannel"/>, into <paramref name="dst"/> as
    /// <c>[peak, rms, peak, rms, …]</c>.
    /// </summary>
    public void ReadPairs(int firstChannel, Span<float> dst)
    {
        var count = dst.Length / 2;
        if (firstChannel < 0 || firstChannel + count > ChannelCount)
            throw new ArgumentOutOfRangeException(
                nameof(firstChannel),
                $"Channels [{firstChannel}, {firstChannel + count}) are outside the aggregator's {ChannelCount} channels.");

        for (var c = 0; c < count; c++)
        {
            var packed = Interlocked.Read(ref _publishedPairs[firstChannel + c]);
            UnpackPair(packed, out var peak, out var rms);
            dst[c * 2]     = peak;
            dst[c * 2 + 1] = rms;
        }
    }

    /// <summary>
    /// Telemetry thread: copy the latest published pairs for every channel
    /// into <paramref name="dst"/> as <c>[peak0, rms0, peak1, rms1, …]</c>.
    /// Returns the current frame id.
    /// </summary>
    public uint TrySnapshot(Span<float> dst)
    {
        if (dst.Length < ChannelCount * 2)
            throw new ArgumentException(
                $"Destination too small: need {ChannelCount * 2} floats, got {dst.Length}.",
                nameof(dst));

        ReadPairs(0, dst.Slice(0, ChannelCount * 2));

        // Frame id is monotonic; consumers care about ordering only, not
        // exact sync between channels and id, so a Volatile read is enough.
        return FrameId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackPair(float peak, float rms)
    {
        var p = (uint)BitConverter.SingleToInt32Bits(peak);
        var r = (uint)BitConverter.SingleToInt32Bits(rms);
        return (long)p | ((long)r << 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void UnpackPair(long packed, out float peak, out float rms)
    {
        peak = BitConverter.Int32BitsToSingle((int)(packed & 0xFFFFFFFF));
        rms  = BitConverter.Int32BitsToSingle((int)((ulong)packed >> 32));
    }
}
