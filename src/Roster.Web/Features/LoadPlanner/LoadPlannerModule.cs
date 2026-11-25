// New: the Load Planner's services.

namespace Roster.Web.Features.LoadPlanner;

public sealed class LoadPlannerModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ILoadPlannerService, LoadPlannerService>();
}
