// New: registers the Non-Op page's service (see Features/FeatureModules.cs).

namespace Roster.Web.Features.NonOp;

public sealed class NonOpModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<INonOpService, NonOpService>();
}
