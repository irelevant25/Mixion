using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mixion.Host.Ipc;
using Mixion.Host.Web;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// The control socket over real Kestrel: server-pushed notifications reach the
/// client, and a host shutdown tells the client before closing the socket —
/// the signal the SPA uses to close its tab.
/// </summary>
public class ControlSocketTests
{
    [Fact]
    public async Task HostShutdown_NotifiesTheClientThenClosesWithGoingAway()
    {
        await using var host = await TestHost.StartAsync();
        using var client = await host.ConnectAsync();

        var stopping = host.App.StopAsync();

        var message = await ReceiveTextAsync(client);
        Assert.Contains("\"method\":\"hostShutdown\"", message);

        var close = await client.ReceiveAsync(new byte[256], Timeout(5));
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, client.CloseStatus);
        Assert.Equal(WebSocketEndpoint.ShutdownCloseReason, client.CloseStatusDescription);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Timeout(5));

        // An open UI must not hold the host's shutdown hostage.
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Broadcast_ReachesConnectedClients()
    {
        await using var host = await TestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await WaitUntilAsync(() => host.Hub.Count == 1);

        host.Hub.Broadcast("stateChanged", new { inputs = Array.Empty<object>() });

        var message = await ReceiveTextAsync(client);
        Assert.Contains("\"method\":\"stateChanged\"", message);
        Assert.Contains("\"params\":{\"inputs\":[]}", message);
    }

    private static CancellationToken Timeout(int seconds)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static async Task<string> ReceiveTextAsync(ClientWebSocket client)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, Timeout(5));
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was never met.");
            await Task.Delay(10);
        }
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private TestHost(WebApplication app, string url)
        {
            App = app;
            Url = url;
        }

        public WebApplication App { get; }
        public string Url { get; }
        public TelemetryHub Hub => App.Services.GetRequiredService<TelemetryHub>();

        public static async Task<TestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(k => HttpServer.ConfigureKestrel(k, new HostBindingOptions(null)));
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<SessionStore>();
            builder.Services.AddSingleton<TelemetryHub>();
            builder.Services.AddSingleton(_ => new JsonRpcDispatcher(NullLogger.Instance));

            var app = builder.Build();
            app.UseWebSockets();
            app.MapControlSocket();
            await app.StartAsync();

            return new TestHost(app, HttpServer.ResolveBoundUrl(app.Services.GetRequiredService<IServer>()));
        }

        public async Task<ClientWebSocket> ConnectAsync()
        {
            var token  = App.Services.GetRequiredService<SessionStore>().Issue();
            var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"{Url.Replace("http://", "ws://")}/ws?token={token}"), Timeout(5));
            return client;
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
