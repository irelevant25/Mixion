using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Mixion.Host.Audio;

namespace Mixion.Host.Web;

/// <summary>
/// <c>GET /api/process-icon?id=process:&lt;name&gt;</c> — serves the PNG-encoded
/// icon for a per-process loopback channel. The FE renders this as an
/// <c>&lt;img&gt;</c> on each strip header so Chrome / Spotify / OBS / a game
/// shows up with its real Windows icon next to the channel name.
///
/// <para>
/// The endpoint is only meaningful for channel ids of the form
/// <c>process:&lt;name&gt;</c> — anything else returns 404 and the FE falls
/// back to the generic SVG glyph. The lookup re-resolves the running
/// process by name (PIDs change across restarts, names don't) and asks
/// <see cref="IconExtractor"/> for the cached PNG bytes.
/// </para>
///
/// <para>
/// Response carries <c>Cache-Control: public, max-age=3600</c> so the
/// browser doesn't re-fetch on every refresh — icon resources change
/// roughly never, and the BE-side cache is keyed by executable path
/// anyway. We don't need the WS auth token here: the data is purely a
/// snapshot of an installed binary's icon, no mixer state, and the host
/// is bound to <c>127.0.0.1</c> only.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessIconEndpoint
{
    private const string ProcessIdPrefix = "process:";

    public static IEndpointRouteBuilder MapProcessIcon(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/process-icon", (HttpContext ctx, string? id) =>
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith(ProcessIdPrefix, StringComparison.Ordinal))
                return Results.NotFound();

            var name = id[ProcessIdPrefix.Length..];
            if (string.IsNullOrWhiteSpace(name)) return Results.NotFound();

            var path = ResolveExecutablePath(name);
            if (path is null) return Results.NotFound();

            var png = IconExtractor.ExtractPng(path);
            if (png is null) return Results.NotFound();

            // Long-ish cache: icon resources are stable for the life of an
            // installed binary, and even when the user upgrades the app the
            // browser will re-fetch on the next host restart (different
            // ETag would be ideal but the cost-benefit isn't worth a
            // hash-of-bytes round-trip per request right now).
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Bytes(png, "image/png");
        });

        return app;
    }

    /// <summary>
    /// Find a currently-running process whose <see cref="Process.ProcessName"/>
    /// matches <paramref name="name"/> (the value the channel id encoded), then
    /// read its <see cref="ProcessModule.FileName"/>. Multiple matches are
    /// fine — Chrome with 12 sub-processes all share an icon, so the first
    /// reachable path wins. Protected processes (anti-cheat, system
    /// services) refuse <see cref="Process.MainModule"/> access; we skip
    /// those and try the next candidate.
    /// </summary>
    private static string? ResolveExecutablePath(string name)
    {
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(name); }
        catch { return null; }

        try
        {
            foreach (var p in candidates)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                catch
                {
                    // Access denied / 32-vs-64-bit cross-bitness — try the next.
                }
            }
            return null;
        }
        finally
        {
            foreach (var p in candidates)
            {
                try { p.Dispose(); } catch { /* best-effort */ }
            }
        }
    }
}
