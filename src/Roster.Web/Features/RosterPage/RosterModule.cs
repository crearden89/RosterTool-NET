// New: registers the Roster page's service and maps its endpoint (CONVENTIONS: one module per area).

namespace Roster.Web.Features.RosterPage;

public sealed class RosterModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // FAKE until the held roster's loaders land (D2/E2/E3): invented data.
        services.AddScoped<IRosterService, FakeRosterService>();
    }

    public void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(HeldRosterEndpoint.Path, HeldRosterEndpoint.HandleAsync);
}
