// Port of src/lib/fixedCycle.ts.
// ─── Fixed-roster cycle resolution (pure) ────────────────────────────────────
// A fixed roster is a cycle of 1, 2 or 4 weekly templates. The cycle's
// start_date anchors which calendar week is Week 1; every other week's index
// follows deterministically, so any date's template is knowable without state.

using System.Text.Json.Serialization;

namespace Roster.Engine.Core;

public sealed record CycleRow(
    [property: JsonPropertyName("employee_id")] string EmployeeId,
    [property: JsonPropertyName("cycle_weeks")] int CycleWeeks,
    [property: JsonPropertyName("start_date")] DateOnly StartDate);

public static class FixedCycle
{
    /// <summary>The Monday of the week containing <paramref name="date"/> (ISO weeks: Mon–Sun).</summary>
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-Period.DowMon0(date));

    /// <summary>
    /// Zero-based week index within the cycle for <paramref name="date"/>.
    ///
    /// Whole weeks between the Monday of the start date and the Monday of the
    /// date, modulo the cycle length. The modulo is positive, so a date BEFORE the
    /// start still resolves - the cycle is treated as extending backwards in time,
    /// which keeps historical rosters stable when a manager anchors a cycle
    /// mid-stream.
    ///
    /// A 1-week cycle always resolves to 0, reproducing the pre-cycle behaviour.
    /// </summary>
    public static int FixedWeekIndex(int cycleWeeks, DateOnly startDate, DateOnly date)
    {
        if (cycleWeeks <= 1) return 0;
        var weeks = (int)Math.Floor((MondayOf(date).DayNumber - MondayOf(startDate).DayNumber) / 7.0);
        return ((weeks % cycleWeeks) + cycleWeeks) % cycleWeeks;
    }

    /// <summary>
    /// Per-employee resolver over the fixed_roster_cycles rows. Employees without a
    /// cycle row are on a 1-week cycle (index always 0) - exactly the legacy shape.
    /// </summary>
    public static Func<string, DateOnly, int> MakeCycleResolver(IEnumerable<CycleRow>? rows)
    {
        var byEmp = new JsMap<string, CycleRow>();
        foreach (var r in rows ?? []) byEmp.Set(r.EmployeeId, r);
        return (employeeId, date) =>
            byEmp.TryGet(employeeId, out var c) ? FixedWeekIndex(c.CycleWeeks, c.StartDate, date) : 0;
    }
}
