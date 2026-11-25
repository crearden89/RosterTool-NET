// Port of src/app/api/floor/[token]/route.ts, and the Floor Display's services.

using Roster.Web.Auth;
using Roster.Web.Kiosk;

namespace Roster.Web.Features.Floor;

public sealed class FloorModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IFloorService, FloorService>();

    public void Map(IEndpointRouteBuilder endpoints) =>
        // GET /api/floor/{token} - public (no sign-in, unless Kiosk:RequireDeviceSignIn).
        // Phase 1 of the TypeScript app returns the store identity and an empty roster
        // payload; no approved-roster surface was ever added to it.
        endpoints.MapGet("/api/floor/{token}", async (string token, IKioskLinkCheck links, CancellationToken ct) =>
        {
            var link = await links.CheckAsync(token, ct);
            if (link is not { Status: KioskLinkStatus.Open, Store: { } store })
                return Results.Json(new { error = "Store not found" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(new
            {
                store = new { name = store.Name, code = store.Code, state = store.State },
                // Placeholder: no approved roster surface exists until Phase 5.
                roster = (object?)null,
            });
        }).RequireAuthorization(Policies.Kiosk);
}
