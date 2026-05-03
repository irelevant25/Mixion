using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.Audio.Dsp;
using VoicemeterAlt.Host.Ipc;

namespace VoicemeterAlt.Host.Web;

/// <summary>
/// BE-081: drains the engine's spectrum tap ring at ~20 Hz, runs an FFT,
/// and pushes a binary <see cref="SpectrumFrame"/> to every connection
/// that has issued <c>subscribeSpectrum</c>.
///
/// Backpressure model mirrors <see cref="TelemetryBroadcaster"/>: the
/// per-connection send mutex is acquired with zero timeout so a slow
/// client never stalls the loop. Allocation is restricted to the FFT
/// scratch buffers (allocated once on first frame).
/// </summary>
public sealed class SpectrumBroadcaster : BackgroundService
{
    /// <summary>~50 ms cadence; ~20 Hz refresh feels responsive without burning CPU.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    private readonly TelemetryHub _hub;
    private readonly EngineHost   _engine;
    private readonly ILogger<SpectrumBroadcaster> _logger;

    public SpectrumBroadcaster(TelemetryHub hub, EngineHost engine, ILogger<SpectrumBroadcaster> logger)
    {
        _hub    = hub;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var samples      = new float[SpectrumAnalyzer.FftSize];
        var realScratch  = new float[SpectrumAnalyzer.FftSize];
        var imagScratch  = new float[SpectrumAnalyzer.FftSize];
        var magnitudeDb  = new float[SpectrumAnalyzer.BinCount];
        var frameBuf     = new byte[SpectrumFrame.SizeFor(SpectrumAnalyzer.BinCount)];
        uint frameId     = 0;

        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var engine = _engine.Current;
                if (engine is null) continue;

                var tap = engine.SpectrumTap;
                if (tap is null) continue;
                if (!AnySpectrumSubscriber()) continue;

                var ring = engine.SpectrumBuffer;
                if (ring.Available < SpectrumAnalyzer.FftSize) continue;

                // Drain enough samples for one window. We want the *most
                // recent* FftSize, so consume any backlog older than that
                // and take the freshest window.
                while (ring.Available > SpectrumAnalyzer.FftSize)
                {
                    // Discard the oldest sample one at a time would be
                    // expensive; pull a chunk and ignore it.
                    var skip = Math.Min(ring.Available - SpectrumAnalyzer.FftSize, samples.Length);
                    ring.Read(samples.AsSpan(0, skip));
                }

                var got = ring.Read(samples);
                if (got < SpectrumAnalyzer.FftSize) continue;

                SpectrumAnalyzer.ComputeMagnitudeDb(samples, magnitudeDb, realScratch, imagScratch);

                frameId++;
                SpectrumFrame.Pack(
                    frameBuf,
                    frameId,
                    tap.Bus,
                    (byte)Math.Clamp(tap.Channel, 0, 255),
                    SpectrumAnalyzer.BinCount,
                    engine.SampleRate,
                    magnitudeDb);

                foreach (var conn in _hub.Connections)
                {
                    if (!conn.IsSpectrumSubscribed) continue;
                    var socket = conn.Socket;
                    if (socket is null || socket.State != WebSocketState.Open) continue;

                    if (!conn.SendLock.Wait(0, stoppingToken)) continue;

                    var copy = frameBuf.AsSpan(0, frameBuf.Length).ToArray();
                    _ = SendAsync(conn, socket, copy, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spectrum broadcaster crashed");
        }
    }

    private bool AnySpectrumSubscriber()
    {
        foreach (var c in _hub.Connections) if (c.IsSpectrumSubscribed) return true;
        return false;
    }

    private async Task SendAsync(RpcConnection conn, WebSocket socket, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown / connection abort */ }
        catch (WebSocketException)          { /* socket dying */ }
        catch (ObjectDisposedException)     { /* socket disposed under us */ }
        finally
        {
            conn.SendLock.Release();
        }
    }
}
