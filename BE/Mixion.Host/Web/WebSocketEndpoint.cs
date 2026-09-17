using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mixion.Host.Ipc;

namespace Mixion.Host.Web;

public static class WebSocketEndpoint
{
    /// <summary>
    /// Close description sent with status 1001 (Going Away) when the host exits.
    /// The SPA treats it — or the <c>hostShutdown</c> notification sent just
    /// before — as "Mixion was closed" and closes its tab.
    /// </summary>
    public const string ShutdownCloseReason = "host-shutdown";

    /// <summary>How long the shutdown path waits for the client's close frame before aborting.</summary>
    private static readonly TimeSpan ShutdownCloseTimeout = TimeSpan.FromSeconds(2);

    private static readonly byte[] HostShutdownNotification =
        Encoding.UTF8.GetBytes(JsonRpcDispatcher.SerializeNotification("hostShutdown", null));

    public static IEndpointRouteBuilder MapControlSocket(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws", async (HttpContext ctx) =>
        {
            var logger = ctx.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("WebSocket");

            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Expected a WebSocket upgrade request.");
                return;
            }

            var token = ctx.Request.Query["token"].ToString();
            var store = ctx.RequestServices.GetRequiredService<SessionStore>();
            if (!store.TryRedeem(token))
            {
                // Unknown, expired or already used. Refuse the upgrade entirely.
                // We must NOT accept the socket and close it with 1008 — RFC
                // says authn failures should happen as part of the upgrade, so
                // the client sees a 401.
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("Invalid or missing token.");
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var dispatcher = ctx.RequestServices.GetRequiredService<JsonRpcDispatcher>();
            var hub        = ctx.RequestServices.GetRequiredService<TelemetryHub>();
            var lifetime   = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
            var conn       = new RpcConnection { Socket = socket };
            using var registration = hub.Register(conn);

            // When the host exits, tell the UI before the socket goes away so it
            // closes its tab instead of retrying a dead port forever. Starting
            // the close handshake ourselves also lets Kestrel's graceful
            // shutdown finish at once rather than waiting out its timeout on
            // an open WebSocket. Off the stopping thread — that's the tray's UI
            // thread on a tray Exit.
            using var stopping = lifetime.ApplicationStopping.Register(
                () => _ = Task.Run(() => CloseForShutdownAsync(conn, socket, logger)));

            logger.LogInformation("WS open  remote={Remote}", ctx.Connection.RemoteIpAddress);

            await PumpAsync(socket, dispatcher, conn, logger, ctx.RequestAborted);
            logger.LogInformation("WS close");
        });

        return app;
    }

    /// <summary>
    /// Reader pump: assemble UTF-8 text frames (possibly fragmented), hand
    /// them to <see cref="JsonRpcDispatcher"/>, and write the response back.
    /// Inbound binary frames are dropped — the WS is text-only on the way in;
    /// binary is reserved for outbound telemetry (M5).
    /// </summary>
    private static async Task PumpAsync(
        WebSocket socket,
        JsonRpcDispatcher dispatcher,
        RpcConnection conn,
        ILogger logger,
        CancellationToken ct)
    {
        var buffer  = new byte[16 * 1024];
        // Per-connection buffer; the audio path doesn't allocate, but the
        // control path does — fine, it runs on a Kestrel thread, not the mix
        // thread.
        var assembler = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                assembler.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Answer a client-initiated close. When the host started
                        // the handshake (shutdown) the socket is already closed.
                        if (socket.State == WebSocketState.CloseReceived)
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        assembler.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                // Skip binary inbound frames; they have no meaning in M3.
                if (result.MessageType != WebSocketMessageType.Text) continue;
                if (assembler.Length == 0) continue;

                var text = Encoding.UTF8.GetString(assembler.GetBuffer(), 0, (int)assembler.Length);
                var response = await dispatcher.DispatchAsync(text, conn, ct);
                if (response is null) continue;

                await conn.SendLock.WaitAsync(ct);
                try
                {
                    if (socket.State != WebSocketState.Open) return;
                    var bytes = Encoding.UTF8.GetBytes(response);
                    await socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken: ct);
                }
                finally
                {
                    conn.SendLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "WS terminated");
        }
    }

    /// <summary>
    /// Host is exiting: send <c>hostShutdown</c>, then start the close
    /// handshake with 1001 / <see cref="ShutdownCloseReason"/>. The pump sees
    /// the client's reply and ends; a client that never replies is aborted.
    /// </summary>
    private static async Task CloseForShutdownAsync(RpcConnection conn, WebSocket socket, ILogger logger)
    {
        try
        {
            if (!await conn.SendLock.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false))
            {
                Abort(socket);
                return;
            }

            try
            {
                if (socket.State != WebSocketState.Open) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.SendAsync(
                    new ArraySegment<byte>(HostShutdownNotification),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: cts.Token).ConfigureAwait(false);
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.EndpointUnavailable,
                    ShutdownCloseReason,
                    cts.Token).ConfigureAwait(false);
            }
            finally
            {
                conn.SendLock.Release();
            }

            await Task.Delay(ShutdownCloseTimeout).ConfigureAwait(false);
            if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted)) Abort(socket);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "WS shutdown close did not complete cleanly");
            Abort(socket);
        }
    }

    private static void Abort(WebSocket socket)
    {
        try { socket.Abort(); } catch { /* already gone */ }
    }
}
