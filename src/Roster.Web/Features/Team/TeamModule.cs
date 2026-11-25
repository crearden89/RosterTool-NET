// New: registers the Team Members page's service (Features/FeatureModules.cs finds it).

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Roster.Web.Features.Team;

public sealed class TeamModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.TryAddScoped<ITeamService, TeamService>();
}
