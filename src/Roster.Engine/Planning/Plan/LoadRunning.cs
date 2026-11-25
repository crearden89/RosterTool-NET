// Port of src/lib/plan/loadRunning.ts.
// ─── Load running periods, for the plan grid ─────────────────────────────────
// Pure (plan page + tests).
//
// The day's load running periods are Load Running's own windows from Stage 1 -
// the store's start rule (customers in the first hour), or 18:00 to close on a
// night-fill store - plus the evening before a public holiday when the
// holiday's load was moved into it: Stage 1 places that load after 18:00,
// outside Load Running's window, on purpose (see runStage1's PH reallocation).
// It is the only load Stage 1 ever puts outside the window, so load it placed
// there marks the evening as a period too - which also works on plans
// generated before this existed.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Planning.Plan;

public static class LoadRunning
{
    /// <summary>A load running period, in minutes from midnight. (Nested: Core has a Period class.)</summary>
    public sealed record Period(double Start, double End);

    public static List<Period> LoadRunningPeriods(Stage1Computation s1) =>
        LoadRunningPeriods(s1.Windows, s1.Blocks, s1.StaffOpen, s1.StaffClose);

    public static List<Period> LoadRunningPeriods(
        IReadOnlyList<WindowRule> windows, IReadOnlyList<PlannedBlock> blocks, double staffOpen, double staffClose)
    {
        var periods = windows
            .Where(w => w.Group == "load_running" && w.End > w.Start)
            .Select(w => new Period(w.Start, w.End))
            .ToList();
        var placedOutside = blocks.Any(b => b.Group == "load_running" && b.Headcount > 0 && OutsidePeriods(periods, b.Time));
        if (placedOutside && staffClose > Stage1Engine.PhEveningAfterMin)
            periods.Add(new Period(Math.Max(Stage1Engine.PhEveningAfterMin, staffOpen), staffClose));
        return Js.Sort(periods, (a, b) => a.Start - b.Start);
    }

    /// <summary>True when the quarter-hour starting at <paramref name="time"/> lies outside every period.</summary>
    public static bool OutsidePeriods(IReadOnlyList<Period> periods, double time) =>
        !periods.Any(p => time >= p.Start && time < p.End);
}
