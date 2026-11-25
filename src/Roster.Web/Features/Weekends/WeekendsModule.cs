// New: registers the Weekends page's service (Features/FeatureModules.cs finds it).

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Weekends;

public sealed class WeekendsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.TryAddScoped<IWeekendsService, WeekendsService>();
}
