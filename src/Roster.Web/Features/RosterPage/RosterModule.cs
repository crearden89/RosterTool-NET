// New: registers the Roster page's service and maps its endpoint (CONVENTIONS: one module per area).

namespace Roster.Web.Features.RosterPage;

public sealed class RosterModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRosterService, RosterService>();
    }

    public void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(HeldRosterEndpoint.Path, HeldRosterEndpoint.HandleAsync);
}
