// New: registers the Dashboard's page service (CONVENTIONS: one module per page area).

namespace Roster.Web.Features.Overview;

public sealed class OverviewModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IOverviewService, OverviewService>();
}
