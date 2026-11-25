// New: registers the Plan page's services (CONVENTIONS.md, Pages).

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Plan;

public sealed class PlanModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new PlanPartKey(sp.GetRequiredService<IConfiguration>()));
        // Plan:UseFake = true shows the page on invented data (FakePlanService), for a look
        // at the page with no plan data in the database.
        if (configuration.GetValue<bool>("Plan:UseFake"))
        {
            services.TryAddSingleton<IPlanService, FakePlanService>();
            return;
        }
        // PENDING until the data layer's roster loader is on main (PendingRosterRebuild).
        services.TryAddScoped<IRosterRebuild, PendingRosterRebuild>();
        services.TryAddScoped<IPlanService, PlanService>();
    }
}
