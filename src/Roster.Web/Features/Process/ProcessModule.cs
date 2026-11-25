// New: registers the Process pages' services (CONVENTIONS.md: each area registers its own),
// and keeps the paths the two pages had before they moved into the Process tabs
// (src/app/(app)/admin/planning-order and roster-order: permanentRedirect).

using Roster.Web.Features.Process.Planning;
using Roster.Web.Features.Process.RosterPhases;
using Roster.Web.Features.Rules;

namespace Roster.Web.Features.Process;

public sealed class ProcessModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPlanningOrderService, PlanningOrderService>();
        services.AddScoped<IRosterOrderService, RosterOrderService>();
    }

    public void Map(IEndpointRouteBuilder endpoints)
    {
        // Moved into a tabbed group (Components/Layout/NavConfig.ProcessTabs). The old
        // paths are kept so bookmarks, and anything that linked here before the move,
        // still land on the right screen.
        RulesModule.MovedPage(endpoints, "/admin/planning-order", "/admin/process/planning");
        RulesModule.MovedPage(endpoints, "/admin/roster-order", "/admin/process/roster");
    }
}
