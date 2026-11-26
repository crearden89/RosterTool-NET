// New: registers the Leave Liability page's service (see Features/FeatureModules.cs).

namespace Roster.Web.Features.LeaveLiability;

public sealed class LeaveLiabilityModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ILeaveLiabilityService, LeaveLiabilityService>();
}
