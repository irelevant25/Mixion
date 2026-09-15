using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Mixion.Host.Ipc;

namespace Mixion.Host.Web;

/// <summary>
/// Registry of currently-open RPC connections. The WebSocket pump registers
/// each connection on accept and unregisters on close; the telemetry
/// broadcaster iterates the snapshot and emits binary frames to those that
/// have opted into the meter stream via <c>subscribe</c>, and
/// <see cref="Broadcast"/> pushes server-initiated notifications to all of them.
///
/// Concurrent set semantics — <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// gives us O(1) add/remove and a safe enumerator.
/// </summary>
public sealed class TelemetryHub
{
    /// <summary>How long a notification waits for a connection's send lock (and for the send itself).</summary>
    private static readonly TimeSpan NotificationSendTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<RpcConnection, byte> _connections = new();

    public IDisposable Register(RpcConnection conn)
    {
        _connections[conn] = 0;
        return new Registration(this, conn);
    }

    /// <summary>
    /// Snapshot of currently-open connections. Iterating
    /// <see cref="ConcurrentDictionary{TKey,TValue}.Keys"/> directly would
    /// allocate a list each call; calling sites can iterate this property
    /// safely under concurrent register/unregister.
    /// </summary>
    public ICollection<RpcConnection> Connections => _connections.Keys;

    public int Count => _connections.Count;

    /// <summary>
    /// Push a JSON-RPC notification to every open connection. Fire-and-forget:
    /// each send takes the connection's send lock with a timeout, so a stalled
    /// client can't hold up the caller or the other clients.
    /// </summary>
    public void Broadcast(string method, object? parameters)
    {
        if (_connections.IsEmpty) return;

        var bytes = Encoding.UTF8.GetBytes(JsonRpcDispatcher.SerializeNotification(method, parameters));
        foreach (var conn in _connections.Keys)
            _ = SendTextAsync(conn, bytes);
    }

    private static async Task SendTextAsync(RpcConnection conn, byte[] bytes)
    {
        if (conn.Socket is not { State: WebSocketState.Open } socket) return;

        bool acquired;
        try
        {
            acquired = await conn.SendLock.WaitAsync(NotificationSendTimeout).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (!acquired) return;

        try
        {
            if (socket.State != WebSocketState.Open) return;
            using var cts = new CancellationTokenSource(NotificationSendTimeout);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Socket dying or stalled — the pump notices and closes it.
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    private void Unregister(RpcConnection conn) => _connections.TryRemove(conn, out _);

    private sealed class Registration : IDisposable
    {
        private TelemetryHub?  _hub;
        private RpcConnection? _conn;

        public Registration(TelemetryHub hub, RpcConnection conn)
        {
            _hub  = hub;
            _conn = conn;
        }

        public void Dispose()
        {
            var hub  = Interlocked.Exchange(ref _hub, null);
            var conn = Interlocked.Exchange(ref _conn, null);
            if (hub is not null && conn is not null) hub.Unregister(conn);
        }
    }
}
