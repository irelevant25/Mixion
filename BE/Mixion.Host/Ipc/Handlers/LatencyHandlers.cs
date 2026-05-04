using Microsoft.Extensions.Logging;
using Mixion.Host.Audio;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// <c>getLatencyEstimate</c> — static (no audio injection) round-trip
/// latency components for the signal-flow page. The FE adds them up per
/// active matrix cell to produce a per-route ms label.
///
/// <c>measureRouteLatency</c> — impulse-based round-trip measurement for a
/// single (input, output) pair. Briefly mutes the chosen output, injects a
/// 30 ms 1 kHz tone burst, watches the chosen input for the burst to come
/// back, returns the measured ms. This is the only honest way to see what
/// VB-CABLE's driver bridge contributes — buffer-summing math is blind to
/// it.
/// </summary>
public static class LatencyHandlers
{
    /// <summary>
    /// One entry per channel — capture or render — that the engine has open.
    /// <see cref="Channels"/> / <see cref="BitsPerSample"/> / <see cref="SampleRate"/>
    /// reflect the device's mix format (display-only — the engine resamples
    /// nothing and works in 32-bit float mono internally for inputs, stereo
    /// for outputs).
    /// </summary>
    public sealed record ChannelLatencyDto(
        int    Index,
        string Id,
        string Name,
        int    BufferMs,
        int    Channels,
        int    BitsPerSample,
        int    SampleRate,
        bool   Available);

    /// <summary>Wire shape for the whole estimate. Engine block ms is shared by every route.</summary>
    public sealed record LatencyEstimateDto(
        int                    SampleRate,
        int                    BlockFrames,
        double                 EngineBlockMs,
        ChannelLatencyDto[]    Inputs,
        ChannelLatencyDto[]    Outputs);

    public sealed record MeasureRouteParams(int Input, int Output);

    /// <summary>
    /// Wire shape for one impulse measurement. <see cref="LatencyMs"/> is
    /// only meaningful when <see cref="Detected"/>; otherwise the burst
    /// never arrived (wrong route, ambient floor too low, device muted) —
    /// FE shows a "—" or an error.
    /// </summary>
    public sealed record MeasureRouteResult(
        bool   Detected,
        double LatencyMs,
        int    SampleRate,
        int    DetectionSample,
        float  DetectionAmplitude,
        int    WatchSamples,
        int    BurstSamples,
        float  Threshold);

    /// <summary>Burst duration in ms. Long enough that even noisy SRC paths land it above threshold; short enough not to startle.</summary>
    private const int BurstDurationMs = 30;
    /// <summary>Burst tone frequency. 1 kHz is well clear of mains hum, voice fundamentals, and HVAC rumble.</summary>
    private const float BurstFrequencyHz = 1000f;
    /// <summary>Burst peak amplitude — -6 dBFS ish. Headroom against any per-output makeup gain in the DSP chain.</summary>
    private const float BurstAmplitude = 0.5f;
    /// <summary>Max round-trip we'll wait for. 500 ms covers basically any reasonable path including a CABLE round trip on a busy box.</summary>
    private const int WatchDurationMs = 500;
    /// <summary>Detection threshold. 5% of full scale = -26 dBFS — well above ambient mic floor, well below the 0.5 burst.</summary>
    private const float DetectionThreshold = 0.05f;
    /// <summary>
    /// Skip the first N ms of watch buffer when looking for the burst.
    /// Anything in this prefix was captured before the burst could
    /// physically traverse the loop (capture buffer + WASAPI engine on the
    /// way in alone is &gt; 5 ms), so it's only useful as ambient. Avoids
    /// a false-positive on whatever the mic was hearing at t=0.
    /// </summary>
    private const int IgnorePrefixMs = 5;

    public static void Register(JsonRpcDispatcher dispatcher, EngineHost host, ILogger logger)
    {
        dispatcher.Register("getLatencyEstimate", (_, _, _) =>
        {
            var engine = host.Current
                ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");

            var state    = engine.SnapshotState();
            var captures = engine.Captures;
            var renders  = engine.Renders;

            var inputs = new ChannelLatencyDto[captures.Count];
            for (var i = 0; i < captures.Count; i++)
            {
                var src = captures[i];
                var ch  = i < state.Inputs.Length ? state.Inputs[i] : null;
                inputs[i] = new ChannelLatencyDto(
                    Index:         i,
                    Id:            ch?.Id        ?? src?.Id           ?? $"input-{i}",
                    Name:          ch?.Name      ?? src?.FriendlyName ?? $"Input {i}",
                    BufferMs:      src?.BufferMilliseconds ?? 0,
                    Channels:      src?.SourceChannels     ?? 0,
                    BitsPerSample: src?.BitsPerSample      ?? 0,
                    SampleRate:    src?.SampleRate         ?? 0,
                    Available:     src is not null && (ch?.Available ?? true));
            }

            var outputs = new ChannelLatencyDto[renders.Count];
            for (var o = 0; o < renders.Count; o++)
            {
                var dev = renders[o];
                var ch  = o < state.Outputs.Length ? state.Outputs[o] : null;
                outputs[o] = new ChannelLatencyDto(
                    Index:         o,
                    Id:            ch?.Id        ?? dev?.Id           ?? $"output-{o}",
                    Name:          ch?.Name      ?? dev?.FriendlyName ?? $"Output {o}",
                    BufferMs:      dev?.LatencyMs    ?? 0,
                    Channels:      dev?.DestChannels ?? 0,
                    BitsPerSample: dev?.BitsPerSample ?? 0,
                    SampleRate:    dev?.SampleRate    ?? 0,
                    Available:     dev is not null && (ch?.Available ?? true));
            }

            var rate          = engine.SampleRate;
            var engineBlockMs = rate > 0 ? MixEngine.BlockFrames * 1000.0 / rate : 0.0;

            var dto = new LatencyEstimateDto(
                SampleRate:    rate,
                BlockFrames:   MixEngine.BlockFrames,
                EngineBlockMs: engineBlockMs,
                Inputs:        inputs,
                Outputs:       outputs);

            return Task.FromResult<object?>(dto);
        });

        dispatcher.Register("measureRouteLatency", async (paramsEl, _, ct) =>
        {
            var p      = JsonRpcDispatcher.RequireParams<MeasureRouteParams>(paramsEl);
            var engine = host.Current
                ?? throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Audio engine not running.");

            var captures = engine.Captures;
            var renders  = engine.Renders;
            if ((uint)p.Input  >= (uint)captures.Count)
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    $"input out of range: {p.Input} (have {captures.Count})");
            if ((uint)p.Output >= (uint)renders.Count)
                throw new JsonRpcException(JsonRpcErrorCode.InvalidParams,
                    $"output out of range: {p.Output} (have {renders.Count})");

            var rate = engine.SampleRate;
            if (rate <= 0)
                throw new JsonRpcException(JsonRpcErrorCode.EngineUnavailable, "Engine sample rate not available.");

            var burstSamples = Math.Max(1, rate * BurstDurationMs / 1000);
            var watchSamples = Math.Max(burstSamples + 1, rate * WatchDurationMs / 1000);

            var burst = new float[burstSamples];
            var twoPiF = 2f * MathF.PI * BurstFrequencyHz;
            for (var s = 0; s < burstSamples; s++)
            {
                // Apply a short cosine fade-in/out so the burst doesn't start
                // with a step transient that smears detection.
                var t      = (float)s / rate;
                var sample = BurstAmplitude * MathF.Sin(twoPiF * t);
                var fade   = ComputeFade(s, burstSamples, fadeSamples: rate / 1000);
                burst[s] = sample * fade;
            }

            var probe = new LatencyProbe
            {
                WatchInputIndex   = p.Input,
                InjectOutputIndex = p.Output,
                Burst             = burst,
                WatchBuffer       = new float[watchSamples],
            };

            engine.SetMeasurementProbe(probe);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await probe.Completion.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "measureRouteLatency: watch buffer never filled (input={In}, output={Out}); engine alive?",
                        p.Input, p.Output);
                }
            }
            finally
            {
                engine.SetMeasurementProbe(null);
            }

            // Detection: scan from the ignore-prefix offset onward, find the
            // first sample whose absolute amplitude crosses the threshold.
            var ignorePrefix = Math.Min(probe.WatchPosition, rate * IgnorePrefixMs / 1000);
            var detectIdx    = -1;
            var detectAmp    = 0f;
            for (var i = ignorePrefix; i < probe.WatchPosition; i++)
            {
                var v = MathF.Abs(probe.WatchBuffer[i]);
                if (v < DetectionThreshold) continue;
                detectIdx = i;
                detectAmp = v;
                break;
            }

            var detected  = detectIdx >= 0;
            var latencyMs = detected ? detectIdx * 1000.0 / rate : 0.0;

            return new MeasureRouteResult(
                Detected:           detected,
                LatencyMs:          latencyMs,
                SampleRate:         rate,
                DetectionSample:    detectIdx,
                DetectionAmplitude: detectAmp,
                WatchSamples:       probe.WatchPosition,
                BurstSamples:       burstSamples,
                Threshold:          DetectionThreshold);
        });
    }

    /// <summary>
    /// Linear fade envelope. Returns 1.0 in the body of the burst and ramps
    /// down to 0 over <paramref name="fadeSamples"/> at each end. Keeps the
    /// burst from starting/ending with a hard step that would smear detection.
    /// </summary>
    private static float ComputeFade(int sampleIndex, int totalSamples, int fadeSamples)
    {
        if (fadeSamples <= 0) return 1f;
        if (sampleIndex < fadeSamples)
            return (float)sampleIndex / fadeSamples;
        var tail = totalSamples - sampleIndex - 1;
        if (tail < fadeSamples)
            return (float)tail / fadeSamples;
        return 1f;
    }
}
