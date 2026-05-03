using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace VoicemeterAlt.Host.Web;

public sealed record HostBindingOptions(int? FixedPort);

public static class HttpServer
{
    /// <summary>
    /// Pin Kestrel to 127.0.0.1. Either a fixed port (--port) or a random
    /// free port assigned by the OS (port 0). Never bind to 0.0.0.0 — this
    /// app must not be reachable from the LAN.
    /// </summary>
    public static void ConfigureKestrel(KestrelServerOptions kestrel, HostBindingOptions opts)
    {
        var port = opts.FixedPort ?? 0;
        kestrel.Listen(IPAddress.Loopback, port);
    }

    /// <summary>
    /// After Start(), Kestrel exposes the resolved address through
    /// IServerAddressesFeature. Returns the first http://127.0.0.1:port URL.
    /// </summary>
    public static string ResolveBoundUrl(IServer server)
    {
        var feature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        var url = feature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel started without binding any address.");
        return url;
    }
}
