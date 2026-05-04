using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Mixion.Host.Web;

/// <summary>
/// Wires up the embedded Angular bundle and the SPA fallback so that any
/// route the backend doesn't recognise (and that isn't an asset request)
/// returns index.html for the Angular router to handle.
/// </summary>
public static class StaticFiles
{
    public static WebApplication UseEmbeddedSpa(this WebApplication app)
    {
        var provider = new ManifestEmbeddedFileProvider(
            Assembly.GetExecutingAssembly(),
            root: "wwwroot");

        var fileOptions = new StaticFileOptions
        {
            FileProvider = provider,
        };

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(fileOptions);

        // SPA fallback. Anything that isn't /api/*, /ws, or a static asset
        // gets index.html so refreshing on /preset-manager doesn't 404.
        app.MapFallback(async ctx =>
        {
            var indexFile = provider.GetFileInfo("index.html");
            if (!indexFile.Exists)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(
                    "UI bundle is missing. During development run `ng serve` and visit :4200, "
                    + "or run build.ps1 to embed the production bundle.");
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await using var stream = indexFile.CreateReadStream();
            await stream.CopyToAsync(ctx.Response.Body);
        });

        return app;
    }
}
