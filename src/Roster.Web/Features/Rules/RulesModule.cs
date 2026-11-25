// New: registers the Rules pages' services (CONVENTIONS.md: each area registers its own),
// and keeps the paths the rules pages had before they moved into the Rules tabs
// (src/app/(app)/admin/break-rules, labour-rules and rostering-rules: permanentRedirect).

using Roster.Web.Features.Rules.Breaks;
using Roster.Web.Features.Rules.Rostering;

namespace Roster.Web.Features.Rules;

public sealed class RulesModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IBreakRulesService, BreakRulesService>();
        services.AddScoped<IRosteringRulesService, RosteringRulesService>();
    }

    public void Map(IEndpointRouteBuilder endpoints)
    {
        // Moved into a tabbed group (Components/Layout/NavConfig.RulesTabs). The old paths
        // are kept so bookmarks, and anything that linked here before the move, still
        // land on the right screen.
        MovedPage(endpoints, "/admin/break-rules", "/admin/rules/breaks");
        MovedPage(endpoints, "/admin/labour-rules", "/admin/rules/labour");
        MovedPage(endpoints, "/admin/rostering-rules", "/admin/rules/rostering");
    }

    /// <summary>A 308 Permanent Redirect from an old page path to its new one (Next.js permanentRedirect).</summary>
    internal static void MovedPage(IEndpointRouteBuilder endpoints, string from, string to) =>
        endpoints.MapMethods(from, [HttpMethods.Get, HttpMethods.Head],
            () => Results.LocalRedirect($"~{to}", permanent: true, preserveMethod: true));
}
