// New: registers the Analysis pages' service (CONVENTIONS: one module per page area).

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Analysis;

public sealed class AnalysisModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.TryAddScoped<IAnalysisRosters, PendingAnalysisRosters>();
    }
}
