using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Mixion.Host.Web;

/// <summary>
/// Refuses requests that come from web pages other than Mixion's own UI.
/// Binding to 127.0.0.1 keeps the LAN out, but any page the user has open can
/// still aim requests at a local port:
///
/// <list type="bullet">
/// <item>WebSockets aren't bound by the same-origin policy, so a foreign page
/// can open /ws. The browser always sends its <c>Origin</c> with the upgrade;
/// only loopback origins are accepted (the packaged UI, or <c>ng serve</c> on
/// localhost:4200 in development).</item>
/// <item>A DNS-rebinding page (attacker.example re-resolved to 127.0.0.1) is
/// same-origin with the host, so it could read /api/session and take a token.
/// Its requests carry <c>Host: attacker.example</c>; only loopback host names
/// are accepted.</item>
/// </list>
///
/// A request without an <c>Origin</c> header didn't come from a cross-origin
/// page script (browsers send it on every WebSocket upgrade and cross-origin
/// fetch), so it passes. Local processes are not kept out by this — nor by
/// anything else here; they run as the user already.
/// </summary>
public static class LoopbackRequestGuard
{
    public static IApplicationBuilder UseLoopbackRequestGuard(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            var host   = ctx.Request.Host;
            var origin = ctx.Request.Headers[HeaderNames.Origin].ToString();

            if (IsLoopbackHost(host) && IsAllowedOrigin(origin))
            {
                await next(ctx);
                return;
            }

            ctx.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Web")
                .LogWarning("Refused {Method} {Path} from host={Host} origin={Origin}",
                    ctx.Request.Method, ctx.Request.Path, host.Value, origin);

            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Mixion only accepts requests from its own UI.");
        });

    /// <summary>True for the host names that can only mean this machine: <c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>.</summary>
    internal static bool IsLoopbackHost(HostString host)
        => host.HasValue && IsLoopbackName(host.Host);

    /// <summary>True when there is no <c>Origin</c>, or it's an http(s) origin on a loopback host (any port).</summary>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && IsLoopbackName(uri.Host);
    }

    private static bool IsLoopbackName(string name)
        => name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || name == "127.0.0.1"
        || name == "[::1]";
}
