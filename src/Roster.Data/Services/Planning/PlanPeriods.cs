// Port of the reads of src/lib/stage1/planPeriod.ts; the grid and the choice of
// fortnight are the engine's PlanPeriod.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.Planning;

public sealed class PlanPeriods(IRosterDbContextFactory contexts, TimeProvider clock) : IPlanPeriods
{
    private DateOnly Today => Dates.TodayAu(clock.GetUtcNow());

    // The 14 dates of a store's planning fortnight: the canonical national grid,
    // the planning fortnight (the one after today's) when none is given.
    public IReadOnlyList<DateOnly> GetPlanPeriodDates(DateOnly? periodStart = null) => PlanPeriod.GetPlanPeriodDates(periodStart, Today);

    // Which fortnight the Plan/Generate screen opens on (spec §5.2): the window's
    // benchmarked dates and approved roster days, read for the engine to decide.
    public async Task<PlanningPeriod> ResolvePlanningPeriodAsync(Guid storeId, bool asPeer = false, CancellationToken ct = default)
    {
        var window = PlanPeriod.PlanningWindowFor(Today);
        await using var db = asPeer
            ? contexts.CreateSystem("resolvePlanningPeriod as a peer: only picks which fortnight to show")
            : contexts.Create();
        var benchDates = (await db.BenchmarkDays.AsNoTracking()
            .Where(b => b.StoreId == storeId && b.Date >= window.WindowStart && b.Date <= window.WindowEnd)
            .Select(b => b.Date).ToListAsync(ct)).ToHashSet();
        var approvedDates = (await db.DayPlans.AsNoTracking()
            .Where(d => d.StoreId == storeId && d.Date >= window.WindowStart && d.Date <= window.WindowEnd && d.RosterStatus == DayStatuses.Approved)
            .Select(d => d.Date).ToListAsync(ct)).ToHashSet();
        var resolved = PlanPeriod.ResolvePlanningPeriod(window, benchDates, approvedDates);
        return new PlanningPeriod(resolved.PeriodStart, resolved.OverduePeriods);
    }
}
