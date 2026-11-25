// New: registers the scheduled jobs (the cron routes of vercel.json) and the
// development seeder's start-up switch, and maps /api/cron/*.
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Jobs;

public sealed class JobsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobsOptions>(configuration.GetSection(JobsOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<EmsoCheckJob>();
        services.TryAddScoped<KeepAliveJob>();

        // The seeder runs before the scheduler, and both before the app serves requests.
        services.AddHostedService<DevSeedStartup>();

        var options = configuration.GetSection(JobsOptions.Section).Get<JobsOptions>() ?? new JobsOptions();
        if (options.RunScheduler)
        {
            services.AddHostedService<CronJobHostedService<EmsoCheckJob>>();
            services.AddHostedService<CronJobHostedService<KeepAliveJob>>();
        }
    }

    public void Map(IEndpointRouteBuilder endpoints) => endpoints.MapCronEndpoints();
}
