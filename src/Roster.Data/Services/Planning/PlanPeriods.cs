// Port of src/lib/stage1/planPeriod.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Engine.Core;

namespace Roster.Data.Services.Planning;

public sealed class PlanPeriods(IRosterDbContextFactory contexts, TimeProvider clock) : IPlanPeriods
{
    private DateOnly Today => Dates.TodayAu(clock.GetUtcNow());

    // The 14 dates of a store's planning fortnight. The fortnight is the canonical
    // national grid (Core Period), not a per-store span derived from whatever
    // benchmark dates happened to be uploaded; with no period given it is the one
    // stores should currently be planning (the one after today's).
    public IReadOnlyList<DateOnly> GetPlanPeriodDates(DateOnly? periodStart = null) =>
        Period.PeriodDatesFrom(periodStart ?? Period.PlanningPeriodStart(Today));

    // Decide which fortnight the Plan/Generate screen should open on for a store
    // (spec §5.2): the earliest grid period in the current→planning window that
    // still has un-approved rostered demand, so a store that's fallen behind lands
    // on the gap. Also returns the earlier periods that are behind, for the overdue
    // banner. A period with no benchmark data isn't actionable, so it's skipped.
    public async Task<PlanningPeriod> ResolvePlanningPeriodAsync(Guid storeId, bool asPeer = false, CancellationToken ct = default)
    {
        var today = Today;
        var target = Period.PlanningPeriodStart(today);
        var periods = new List<DateOnly>();
        for (var p = Period.CurrentPeriodStart(today); p <= target; p = Period.AddPeriods(p, 1)) periods.Add(p);
        var windowStart = periods[0];
        var windowEnd = Period.PeriodEndFrom(periods[^1]);

        await using var db = asPeer
            ? contexts.CreateSystem("resolvePlanningPeriod as a peer: only picks which fortnight to show")
            : contexts.Create();
        var benchDates = (await db.BenchmarkDays.AsNoTracking()
            .Where(b => b.StoreId == storeId && b.Date >= windowStart && b.Date <= windowEnd)
            .Select(b => b.Date).ToListAsync(ct)).ToHashSet();
        var approvedDates = (await db.DayPlans.AsNoTracking()
            .Where(d => d.StoreId == storeId && d.Date >= windowStart && d.Date <= windowEnd && d.RosterStatus == DayStatuses.Approved)
            .Select(d => d.Date).ToListAsync(ct)).ToHashSet();

        // A period needs attention if it has benchmark data whose roster isn't yet
        // fully approved.
        bool NeedsApproval(DateOnly ps)
        {
            var withData = Period.PeriodDatesFrom(ps).Where(benchDates.Contains).ToList();
            return withData.Count > 0 && withData.Any(d => !approvedDates.Contains(d));
        }

        var overdue = periods.Where(p => p < target && NeedsApproval(p)).ToList();
        var earliest = periods.Where(NeedsApproval).Cast<DateOnly?>().FirstOrDefault();
        return new PlanningPeriod(earliest ?? target, overdue);
    }
}
