// New: registers the Sales page's service (see Features/FeatureModules.cs).

namespace Roster.Web.Features.Sales;

public sealed class SalesModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ISalesService, SalesService>();
}
