using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace VoicemeterAlt.Host.Web;

public static class HealthEndpoint
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Json(new
        {
            ok = true,
            version = Version,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
        }));

        return app;
    }
}
