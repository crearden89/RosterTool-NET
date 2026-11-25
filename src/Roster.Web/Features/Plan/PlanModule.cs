// New: registers the Plan page's services (CONVENTIONS.md, Pages).

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Plan;

public sealed class PlanModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new PlanPartKey(sp.GetRequiredService<IConfiguration>()));
        // FAKE until the data services the real PlanService reads are on main.
        services.TryAddSingleton<IPlanService, FakePlanService>();
    }
}
