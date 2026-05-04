using System.Collections.Concurrent;
using Mixion.Host.Ipc;

namespace Mixion.Host.Web;

/// <summary>
/// Registry of currently-open RPC connections. The WebSocket pump registers
/// each connection on accept and unregisters on close; the telemetry
/// broadcaster iterates the snapshot and emits binary frames to those that
/// have opted into the meter stream via <c>subscribe</c>.
///
/// Concurrent set semantics — <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// gives us O(1) add/remove and a safe enumerator.
/// </summary>
public sealed class TelemetryHub
{
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
