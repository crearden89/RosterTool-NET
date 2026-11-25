// Port of src/lib/stage1/planPeriod.ts - the pure parts. The data layer
// (D2's Services) reads the window's benchmark dates and approved roster days
// and calls ResolvePlanningPeriod with them.

using Roster.Engine.Core;

namespace Roster.Engine.Planning.Stage1;

/// <summary>
/// The fortnights the Plan/Generate screen can open on: every grid period from
/// the current one to the one stores should be planning (Target), and the dates
/// they span (what to read: benchmark_days and day_plans in [WindowStart, WindowEnd]).
/// </summary>
public sealed record PlanningWindow(List<DateOnly> Periods, DateOnly Target, DateOnly WindowStart, DateOnly WindowEnd);

/// <param name="PeriodStart">The fortnight to open on.</param>
/// <param name="OverduePeriods">Earlier fortnights that are behind (the overdue banner).</param>
public sealed record ResolvedPlanningPeriod(DateOnly PeriodStart, List<DateOnly> OverduePeriods);

public static class PlanPeriod
{
    /// <summary>
    /// The 14 dates of a store's planning fortnight. The fortnight is the
    /// canonical national grid (Core's Period), not a per-store span derived from
    /// whatever benchmark dates happened to be uploaded. <paramref name="periodStart"/>
    /// targets a specific grid period; when omitted it defaults to the period
    /// stores should currently be planning (the one after today's). (The
    /// TypeScript's unused store id is dropped: the grid is national.)
    /// </summary>
    public static List<DateOnly> GetPlanPeriodDates(DateOnly? periodStart = null, DateOnly? today = null) =>
        Period.PeriodDatesFrom(periodStart ?? Period.PlanningPeriodStart(today));

    /// <summary>The current→planning window resolvePlanningPeriod reads, for <paramref name="today"/> (the store clock's today by default).</summary>
    public static PlanningWindow PlanningWindowFor(DateOnly? today = null)
    {
        var target = Period.PlanningPeriodStart(today);
        var periods = new List<DateOnly>();
        for (var p = Period.CurrentPeriodStart(today); p <= target; p = Period.AddPeriods(p, 1)) periods.Add(p);
        return new PlanningWindow(periods, target, periods[0], Period.PeriodEndFrom(periods[^1]));
    }

    /// <summary>
    /// Decide which fortnight the Plan/Generate screen should open on for a store
    /// (spec §5.2): the earliest grid period in the current→planning window that
    /// still has un-approved rostered demand, so a store that's fallen behind lands
    /// on the gap. Also returns the earlier periods that are behind, for the overdue
    /// banner. A period with no benchmark data isn't actionable, so it's skipped.
    /// </summary>
    /// <param name="benchDates">The store's benchmark_days dates in the window.</param>
    /// <param name="approvedDates">The window's day_plans dates whose roster_status is "approved".</param>
    public static ResolvedPlanningPeriod ResolvePlanningPeriod(
        PlanningWindow window, IReadOnlySet<DateOnly> benchDates, IReadOnlySet<DateOnly> approvedDates)
    {
        // A period needs attention if it has benchmark data whose roster isn't yet
        // fully approved.
        bool NeedsApproval(DateOnly ps)
        {
            var withData = Period.PeriodDatesFrom(ps).Where(benchDates.Contains).ToList();
            return withData.Count > 0 && withData.Any(d => !approvedDates.Contains(d));
        }

        var overduePeriods = window.Periods.Where(p => p < window.Target && NeedsApproval(p)).ToList();
        DateOnly? earliestUnapproved = window.Periods.Where(NeedsApproval).Select(p => (DateOnly?)p).FirstOrDefault();
        return new ResolvedPlanningPeriod(earliestUnapproved ?? window.Target, overduePeriods);
    }
}
