// Port of src/lib/plan/plannedHours.ts.
// ─── Planned hours: one definition for every page ────────────────────────────
// The owner (15/11/2026), on Analysis showing 619.00 h planned where the Plan
// page and the Dashboard showed 621.00 h: "they should all align".
//
// A held plan day's PLANNED HOURS are its shift time less every break: the day
// checker's worked hours (owner decision R2), the figure the plan is held and
// reviewed on. They include the time a shift holds with no work in it - held
// for cover, by the shortest legal shift, by a manager's handover, by a fixed
// roster - so they can be above the work given to the labour groups. The Plan
// page, the Review, the Dashboard and Analysis all count them here.
//
// By group, the day's hours are its own tiles (registers and SCOs together as
// Checkout, as the Plan page's day table counts them), and what is left of the
// planned hours is the time with no work in it, so the parts always add up.
// The benchmark beside them is split the Plan page's way (BenchmarkSplit), so
// its groups add up to the day's benchmark too.
// Pure.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Planning.Plan;

/// <summary>
/// The parts of a held plan day these figures read: the held Stage 1 result
/// and the held plan (null when the day has none).
/// </summary>
public sealed record HeldPlanDay(BenchmarkParts Result, PlanShiftComputation? Plan);

public sealed record PlannedByGroup(
    // Hours of work by group, counted from the day's tiles.
    JsMap<string, double> Groups,
    // Planned hours with no work in them: what the groups don't account for.
    double NoWork,
    // The day's planned hours: the groups and the time with no work.
    double Total);

public sealed record HeldDayFigures(double Benchmark, double Planned, int Shifts);

public sealed class GroupHours
{
    public double Benchmark { get; set; }
    public double Planned { get; set; }
}

public sealed class RangeHours
{
    /// <summary>By group: its share of the benchmark and its planned hours.</summary>
    public JsMap<string, GroupHours> Groups { get; } = new();
    /// <summary>Planned hours with no work in them.</summary>
    public double NoWork { get; set; }
    public double Planned { get; set; }
    public double Benchmark { get; set; }
}

/// <summary>plannedHours.ts's functions (a class can't share PlannedHours' name with its method).</summary>
public static class HeldDayHours
{
    private const double Quarter = 0.25;
    private static readonly HashSet<string> NotWork = ["break", "flex"];

    /// <summary>Registers and SCOs are one group, Checkout, wherever hours are added up.</summary>
    public static string HoursGroup(string g) => g is "registers" or "scos" ? "checkout" : g;

    /// <summary>
    /// The day's planned hours: shift time less every break. A plan held before
    /// the day checker carried its own hours is read from its tiles the same way:
    /// every quarter-hour on a shift that isn't a break.
    /// </summary>
    public static double PlannedHours(HeldPlanDay day)
    {
        var plan = day.Plan;
        if (plan is null) return 0;
        if (plan.Hours is not null) return plan.Hours.Worked;
        return plan.Shifts.Aggregate(0.0, (t, s) => t + s.Blocks.Count(b => b.Group != "break") * Quarter);
    }

    /// <summary>
    /// The day's benchmark as the hours check reads it: the benchmark file, booked
    /// non-op hours and any public-holiday move (Stage 1's total before then). A
    /// day with no plan, or held before the checker, works it out the same way.
    /// </summary>
    public static double BenchmarkHours(HeldPlanDay day) =>
        day.Plan?.Hours?.Benchmark ?? BenchmarkSplit.DayBenchmark(day.Result);

    public static PlannedByGroup PlannedByGroup(HeldPlanDay day)
    {
        var groups = new JsMap<string, double>();
        foreach (var s in day.Plan?.Shifts ?? [])
        {
            foreach (var b in s.Blocks)
            {
                if (NotWork.Contains(b.Group)) continue;
                var g = HoursGroup(b.Group);
                groups.Set(g, groups.GetOr(g, 0) + Quarter);
            }
        }
        var total = PlannedHours(day);
        var work = groups.Values().Aggregate(0.0, (a, b) => a + b);
        // Quarter-hours and whole minutes: round off the float dust only.
        var noWork = Js.Round((total - work) * 1e6) / 1e6;
        return new PlannedByGroup(groups, noWork, total);
    }

    /// <summary>The figures the Review and the Dashboard show for a held day.</summary>
    public static HeldDayFigures DayHours(HeldPlanDay day) =>
        new(BenchmarkHours(day), PlannedHours(day), day.Plan?.Shifts.Count ?? 0);

    /// <summary>
    /// Days added up as Analysis shows them: each group against its share of the
    /// benchmark, the time with no work, and the totals. The groups' benchmark adds
    /// up to the days' benchmark and their planned hours, with the time with no
    /// work, to the days' planned hours.
    /// </summary>
    public static RangeHours RangeHours(IEnumerable<HeldPlanDay> days)
    {
        var out_ = new RangeHours();
        GroupHours Of(string g)
        {
            if (!out_.Groups.TryGet(g, out var h)) out_.Groups.Set(g, h = new GroupHours());
            return h;
        }
        foreach (var day in days)
        {
            var benchmark = BenchmarkHours(day);
            out_.Benchmark += benchmark;
            foreach (var (g, h) in BenchmarkSplit.Split(day.Result, benchmark)) Of(g).Benchmark += h;
            var planned = PlannedByGroup(day);
            out_.Planned += planned.Total;
            out_.NoWork += planned.NoWork;
            foreach (var (g, h) in planned.Groups) Of(g).Planned += h;
        }
        return out_;
    }
}
