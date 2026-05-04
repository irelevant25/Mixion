using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mixion.Host.Ipc;

namespace Mixion.Host.Web;

/// <summary>
/// Background service that snapshots the engine's <see cref="Audio.MeterAggregator"/>
/// every ~33 ms and pushes a binary <see cref="TelemetryFrame"/> to every
/// subscribed open WebSocket.
///
/// Backpressure (BE-053): each connection has a single send mutex that the
/// WS pump and this broadcaster share. We try to take it with
/// <see cref="SemaphoreSlim.Wait(int)"/> at zero timeout; on contention we
/// drop this frame for that socket. A slow client never stalls the loop —
/// and never stalls the audio path, which is on its own thread anyway.
/// </summary>
public sealed class TelemetryBroadcaster : BackgroundService
{
    /// <summary>Target cadence — 33 ms ≈ 30 Hz.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(33);

    private readonly TelemetryHub _hub;
    private readonly EngineHost   _engine;
    private readonly ILogger<TelemetryBroadcaster> _logger;

    public TelemetryBroadcaster(TelemetryHub hub, EngineHost engine, ILogger<TelemetryBroadcaster> logger)
    {
        _hub    = hub;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Both buffers grow on demand as channel count changes (e.g. when
        // the engine first comes online). Allocations happen off the audio
        // path; once we settle on a channel count, the buffer is reused.
        var pairs    = Array.Empty<float>();
        var frameBuf = Array.Empty<byte>();

        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var engine = _engine.Current;
                if (engine is null) continue;

                var meters = engine.Meters;
                var n      = meters.ChannelCount;
                if (n == 0) continue;

                if (_hub.Count == 0) continue;

                if (pairs.Length    < n * 2)              pairs    = new float[n * 2];
                var frameSize = TelemetryFrame.SizeFor(n);
                if (frameBuf.Length < frameSize)          frameBuf = new byte[frameSize];

                var frameId = meters.TrySnapshot(pairs.AsSpan(0, n * 2));
                TelemetryFrame.Pack(frameBuf.AsSpan(0, frameSize), frameId, n, pairs.AsSpan(0, n * 2));

                foreach (var conn in _hub.Connections)
                {
                    if (!conn.IsTelemetrySubscribed) continue;
                    var socket = conn.Socket;
                    if (socket is null || socket.State != WebSocketState.Open) continue;

                    if (!conn.SendLock.Wait(0, stoppingToken)) continue; // BE-053: drop

                    // Each conn must own its bytes for the duration of its
                    // SendAsync. Copy out of the shared frame buffer so the
                    // next loop iteration can rewrite it without racing the
                    // outstanding sends.
                    var copy = frameBuf.AsSpan(0, frameSize).ToArray();
                    _ = SendAsync(conn, socket, copy, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry broadcaster crashed");
        }
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
        catch (WebSocketException)          { /* socket dying — pump will close it */ }
        catch (ObjectDisposedException)     { /* socket disposed under us — same */ }
        finally
        {
            conn.SendLock.Release();
        }
    }
}
