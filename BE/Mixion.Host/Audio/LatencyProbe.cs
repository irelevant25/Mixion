namespace Mixion.Host.Audio;

/// <summary>
/// One-shot round-trip latency measurement state. Set on the engine via
/// <see cref="MixEngine.SetMeasurementProbe"/>; the audio thread cooperates
/// by injecting <see cref="Burst"/> into the chosen output's render block
/// (replacing the normal mix for that output) and copying the chosen
/// input's raw capture block into <see cref="WatchBuffer"/>. The RPC
/// handler awaits <see cref="Completion"/>, then scans the watch buffer
/// for the first sample above a detection threshold — that index in
/// samples = round-trip delay.
///
/// This class is mutable and only safe for a single measurement at a time;
/// the engine reads it under <see cref="Volatile"/> reads, but
/// <see cref="BurstPosition"/> / <see cref="WatchPosition"/> are touched
/// only from the audio thread.
/// </summary>
public sealed class LatencyProbe
{
    public required int WatchInputIndex   { get; init; }
    public required int InjectOutputIndex { get; init; }

    /// <summary>Pre-generated test signal. Mono float, played on both stereo sides.</summary>
    public required float[] Burst { get; init; }

    /// <summary>
    /// Pre-allocated capture log. Holds the chosen input's frames (the
    /// louder of L/R per sample) from t=0 (probe set) onward. Sized for the
    /// maximum round-trip we expect to measure (e.g. 500 ms at engine sample
    /// rate).
    /// </summary>
    public required float[] WatchBuffer { get; init; }

    /// <summary>Audio-thread cursor into <see cref="Burst"/>.</summary>
    public int BurstPosition;

    /// <summary>Audio-thread cursor into <see cref="WatchBuffer"/>.</summary>
    public int WatchPosition;

    /// <summary>
    /// Awaitable that resolves when <see cref="WatchPosition"/> reaches the
    /// end of <see cref="WatchBuffer"/>. RunContinuationsAsynchronously so
    /// the audio thread doesn't block on whatever the awaiter does next
    /// (the RPC handler scans the buffer + serializes JSON).
    /// </summary>
    public TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
