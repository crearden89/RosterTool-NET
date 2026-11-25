// New: registers the scheduled checks' data work (the jobs themselves are hosted
// services in src/Roster.Web/Features/Jobs).
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Data.Jobs;

public sealed class JobsDataModule : IDataModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<ICronHeartbeat, CronHeartbeat>();
        services.TryAddScoped<IEmsoRosterCheck, EmsoRosterCheck>();
        services.TryAddScoped<IKeepAlive, KeepAlive>();
    }
}
