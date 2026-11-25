// Port of src/lib/stage2/shiftCost.ts.
// (Named ShiftLengthCost so it never reads as Pay.ShiftCost, the roster's
// dollar cost, in a file that uses both.)
//
// ─── What a shift length costs per hour of work ──────────────────────────────
// A shift is paid for its clock time less any UNPAID break, and delivers its
// clock time less its WHOLE break. Those two differ, so the price of an hour of
// actual work depends on how long the shift is:
//
//     paid hours   = length − unpaid break
//     worked hours = length − total break
//     $ per worked hour = rate × paid / worked
//
// The hourly rate cancels out of the comparison, so the ranking is the same for
// every store and every pay grade - it is a property of the award's break
// tiers, not of the wage. That also means it stays correct on its own when the
// break rules are edited, which a hard-coded table of good lengths would not.
//
// Worked example at $43.23, matching the client's costing sheet:
//
//   length  paid  unpaid  work   cost     $/worked hour
//   3.00    0.25  0.00    2.75   129.69   47.16
//   6.00    0.33  0.17    5.50   252.03   45.82   ← cheapest short shift
//   6.25    0.50  0.50    5.25   248.57   47.35   ← worst on the board
//   9.50    0.50  0.50    8.50   389.07   45.77   ← cheapest overall
//
// Note 6.00 against 6.25: a quarter hour longer crosses the six-hour tier, the
// entitlement doubles from 30 minutes to an hour, and the shift delivers LESS
// work for MORE money. The tier edges are where the money is.

using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Engine.Rostering.Shaper;

public static class ShiftLengthCost
{
    /// <summary>Number.MAX_SAFE_INTEGER: the rank of a length that is not legal at all.</summary>
    public const double MaxSafeInteger = 9007199254740991;

    /// <summary>Minutes actually spent on tasks: clock less the whole break.</summary>
    public static double WorkMinutes(string type, double lenMin) =>
        lenMin - Breaks.BreakForShift(type, lenMin / 60).TotalMinutes;

    /// <summary>Minutes paid: clock less the unpaid portion of the break.</summary>
    public static double PaidMinutes(string type, double lenMin) =>
        lenMin - Breaks.BreakForShift(type, lenMin / 60).UnpaidMinutes;

    /// <summary>
    /// Paid minutes per worked minute. Lower is cheaper. Infinity for a length that
    /// delivers no work at all (its break is the whole shift), so those can never
    /// win a comparison.
    /// </summary>
    public static double CostRatio(string type, double lenMin)
    {
        var work = WorkMinutes(type, lenMin);
        return work <= 0 ? double.PositiveInfinity : PaidMinutes(type, lenMin) / work;
    }

    /// <summary>
    /// Every legal length for a role, cheapest first. Ties break toward the LONGER
    /// shift: identical cost per worked hour, one fewer person to find, one fewer
    /// set of edges for the plan to work around.
    /// </summary>
    public static List<double> PreferredLengths(string type, double minMin, double maxMin)
    {
        var out_ = new List<double>();
        for (var m = Math.Ceiling(minMin / Grid.BlockMin) * Grid.BlockMin; m <= maxMin; m += Grid.BlockMin) out_.Add(m);
        return Js.Sort(out_, (a, b) => (CostRatio(type, a) - CostRatio(type, b)).Or(b - a));
    }

    /// <summary>
    /// How bad a length is, as a rank among the legal lengths for its role: 0 is the
    /// cheapest hour of work available, and the number grows as the choice gets
    /// worse. A rank rather than the raw ratio because the ratios sit within a few
    /// percent of each other and a rank is what a tie-break wants.
    /// </summary>
    public static double LengthRank(string type, double lenMin, double minMin, double maxMin)
    {
        var idx = PreferredLengths(type, minMin, maxMin).IndexOf(lenMin);
        return idx == -1 ? MaxSafeInteger : idx;
    }
}
