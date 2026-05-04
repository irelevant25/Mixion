using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mixion.Host.Ipc;
using Mixion.Host.State;

namespace Mixion.Host.Web;

public static class SessionEndpoint
{
    public static IEndpointRouteBuilder MapSession(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/session", (HttpContext ctx) =>
        {
            var store         = ctx.RequestServices.GetRequiredService<SessionStore>();
            var engineHost    = ctx.RequestServices.GetRequiredService<EngineHost>();
            var currentPreset = ctx.RequestServices.GetRequiredService<CurrentPresetState>();

            var token = store.Issue();
            var state = engineHost.Current?.SnapshotState().ToDto() ?? StateDto.Empty;

            // mixerStateInit lets the SPA hydrate without a getState() round-
            // trip. The current preset + resolved slot layout (if any) are
            // also shipped so a fresh page load can rebuild the user's strip
            // rows without an extra RPC.
            return Results.Json(new
            {
                token,
                mixerStateInit = state,
                currentPreset  = currentPreset.Name,
                inputSlots     = currentPreset.InputSlots,
                outputSlots    = currentPreset.OutputSlots,
            });
        });

        return app;
    }
}
