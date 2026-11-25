// Port of the reads of src/app/(app)/plan/roster/page.tsx and src/app/api/roster/held/route.ts.
// The page's actions (generate, edit, approve) are the Plan area's (IPlanService), as the
// TypeScript Roster page imports them from plan/generate/actions.ts.

namespace Roster.Web.Features.RosterPage;

/// <summary>The Roster page's reads.</summary>
public interface IRosterService
{
    /// <summary>
    /// Everything /plan/roster shows for the signed-in user and the page's query. NO solving
    /// on render: the page reads the HELD snapshot written by the explicit Generate/Regenerate
    /// action (shared with the Plan page - one solve produces both the plan and roster
    /// snapshots).
    /// </summary>
    Task<RosterPageModel> LoadPageAsync(RosterPageQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// When the fortnight's held roster was last saved, or null when there is none (GET
    /// /api/roster/held). The caller has already checked that the viewer may build the store's
    /// roster.
    /// </summary>
    Task<DateTimeOffset?> HeldRosterGeneratedAtAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);
}
