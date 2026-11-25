// Port of src/lib/payroll/breaks.ts.
// ─── Break entitlements (spec Section 8.2, per the rostering interview) ──────
// A break has two figures that must not be conflated:
//   - totalMinutes: time off the floor (drives coverage + the grid break tiles)
//   - unpaidMinutes: time deducted from PAID hours (drives cost + the Hrs column)
// and a break may come as 1 or 2 separate segments.
//
// Rules:
//   flat_30_unpaid (SM / ASM / DSM): one 30-min UNPAID break, any shift length.
//   tiered (RSM / SA / JSA):
//     ≤ 4h        → 15 min, fully PAID
//     > 4h, ≤ 6h  → 30 min = 10 unpaid + 20 paid
//     > 6h        → two 30-min breaks = one unpaid (30) + one paid (30)
//   No further breaks are awarded beyond these, at any length.

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public static class BreakTypes
{
    public const string Flat30Unpaid = "flat_30_unpaid";
    public const string Tiered = "tiered";
}

public sealed record BreakResult(double TotalMinutes, double UnpaidMinutes, int Segments);

public static class Breaks
{
    private static readonly BreakResult Flat = new(30, 30, 1);
    private static readonly BreakResult Short = new(15, 0, 1);
    private static readonly BreakResult Mid = new(30, 10, 1);
    private static readonly BreakResult Long = new(60, 30, 2);

    // Managers on a salary/flat-break footing vs the tiered hourly roles. RSM shares
    // the SA break structure (tiered) even though it's a manager-tier role.
    public static string BreakTypeForRole(string role) =>
        role is Roles.SM or Roles.ASM or Roles.DSM ? BreakTypes.Flat30Unpaid : BreakTypes.Tiered;

    public static BreakResult BreakForShift(string breakType, double lengthHours)
    {
        if (breakType == BreakTypes.Flat30Unpaid) return Flat;
        // tiered
        if (lengthHours <= 4) return Short;
        if (lengthHours <= 6) return Mid;
        return Long;
    }

    // Convenience: total + unpaid break minutes for a role + shift length.
    public static BreakResult BreakForRole(string role, double lengthHours) => BreakForShift(BreakTypeForRole(role), lengthHours);

    // ─── Shift maximum: PAID hours, not clock ────────────────────────────────
    // `max_shift_hours` in break_rules caps PAID hours - the award limit that also
    // triggers daily overtime. Clock time and work time both derive from it:
    //     clock = paid  + unpaid break     (the unpaid break sits outside pay)
    //     work  = clock - total  break     (time actually on tasks)
    // A Store Assistant capped at 9 PAID hours therefore works a 9.5h clock shift
    // carrying a 60-minute entitlement (30 of it unpaid) and delivers 8.5h of
    // tasks. Reading the cap as clock time instead costs half an hour of pay per
    // shift, which is what held the managers permanently under contract.

    // Longest clock span a shift may occupy for a given PAID cap.
    public static double MaxClockMinutes(string breakType, double maxPaidHours)
    {
        // The break tier is chosen by clock length, which itself depends on the
        // break - so iterate to a fixpoint (converges in two passes).
        var clockHours = maxPaidHours;
        for (var i = 0; i < 4; i++)
        {
            var next = maxPaidHours + BreakForShift(breakType, clockHours).UnpaidMinutes / 60;
            if (Math.Abs(next - clockHours) < 1e-9) break;
            clockHours = next;
        }
        return Js.Round(clockHours * 60);
    }

    // Most WORK (task tiles) a shift can deliver for a given PAID cap.
    public static double MaxWorkMinutes(string breakType, double maxPaidHours)
    {
        var clock = MaxClockMinutes(breakType, maxPaidHours);
        return clock - BreakForShift(breakType, clock / 60).TotalMinutes;
    }

    // The segment structure an entitlement of `totalMinutes` must take on the
    // grid: a 60-min entitlement is TWO separate 30-min breaks, anything shorter
    // is one contiguous run.
    public static List<double> BreakSegmentsFor(double totalMinutes)
    {
        if (totalMinutes <= 0) return [];
        return totalMinutes >= 60 ? [30, totalMinutes - 30] : [totalMinutes];
    }

    // Validate manually-placed break tiles against a shift's entitlement AND the
    // timing rules: exactly the entitled minutes in the entitled segment
    // structure, nothing in the first hour of the shift, at least one hour of
    // tasks AFTER the last break, and the meal (any ≥30-min segment) starting no
    // later than the 5th hour. `times` are the break tiles' start minutes on the
    // 15-min grid; `shiftStart`/`shiftEnd` bound the shift. Returns a
    // human-readable problem, or null when every requirement is met.
    public static string? BreakTileError(IEnumerable<double> times, double entitledMinutes, double shiftStart, double shiftEnd)
    {
        var sorted = Js.Sorted(times, (a, b) => a - b);
        var runs = new List<(double Start, double Len)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0 && sorted[i] == sorted[i - 1] + 15) runs[^1] = (runs[^1].Start, runs[^1].Len + 15);
            else runs.Add((sorted[i], 15));
        }
        double total = sorted.Count * 15;
        if (total != entitledMinutes)
            return $"has {Js.NumberToString(total)}m of break tiles but is entitled to {Js.NumberToString(entitledMinutes)}m";
        if (total == 0) return null;
        var expected = BreakSegmentsFor(entitledMinutes);
        var got = Js.Sorted(runs.Select(r => r.Len), (a, b) => a - b);
        var want = Js.Sorted(expected, (a, b) => a - b);
        if (got.Count != want.Count || got.Where((r, i) => r != want[i]).Any())
            return $"break must be {string.Join(" + ", expected.Select(m => $"{Js.NumberToString(m)}m"))} (separate blocks), not {string.Join(" + ", runs.Select(r => $"{Js.NumberToString(r.Len)}m"))}";
        if (sorted[0] < shiftStart + 60) return "can't break within the first hour of the shift";
        if (sorted[^1] + 15 > shiftEnd - 60) return "needs at least 1 hour of tasks after the last break";
        var meal = runs.FindIndex(r => r.Len >= 30);
        if (meal >= 0 && runs[meal].Start > shiftStart + 5 * 60) return "the meal break must start by the 5th hour of the shift";
        // Two breaks (a 60-minute entitlement): at least an hour apart, and one in
        // each half of the shift (B3, confirmed as a rule by the owner 13/11/2026).
        // The halves split at the quarter-hour at or before the shift's midpoint -
        // where the engine places them. Every later move, the scoreboard and a
        // manual edit all read this one check, so none of them can undo the spacing.
        if (runs.Count == 2)
        {
            var first = runs[0];
            var second = runs[1];
            if (second.Start - (first.Start + first.Len) < 60) return "the two breaks must be at least an hour apart";
            var mid = shiftStart + Math.Floor((shiftEnd - shiftStart) / 2 / 15) * 15;
            if (first.Start + first.Len > mid || second.Start < mid) return "the two breaks must sit one in each half of the shift";
        }
        return null;
    }
}
