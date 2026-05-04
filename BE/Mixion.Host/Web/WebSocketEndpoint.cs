using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mixion.Host.Ipc;

namespace Mixion.Host.Web;

public static class WebSocketEndpoint
{
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
            if (!store.IsValid(token))
            {
                // Refuse the upgrade entirely. We must NOT accept the socket
                // and close it with 1008 — RFC says authn failures should
                // happen as part of the upgrade, so the client sees a 401.
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("Invalid or missing token.");
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var dispatcher = ctx.RequestServices.GetRequiredService<JsonRpcDispatcher>();
            var hub        = ctx.RequestServices.GetRequiredService<TelemetryHub>();
            var conn       = new RpcConnection { Socket = socket };
            using var registration = hub.Register(conn);
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
}
