// Port of src/lib/overview/metrics.ts.
// ─── Dashboard performance ratios ────────────────────────────────────────────
// Pure, so the definitions are testable: the loading half of lib/overview/compute.ts
// lives in the page service and can't be exercised from a unit test.
//
// Two different hour figures meet in this file, and keeping them apart is the
// whole point of it:
//   ACTUAL HOURS @ 100% - the sum of the actuals file's per-category columns
//                         (Load Running, Cleaning, Specials, Reset, AdminMgmt,
//                         Checkout). What the day's work was worth. These are
//                         hours ON TASK, so PAID BREAK time is missing from them.
//   KRONOS HOURS        - what the store actually paid for, paid breaks included.
//
// Those two are not like for like until the paid breaks are added back, which
// is what WithPaidBreaks() exists to do.

namespace Roster.Engine.Overview;

public static class Metrics
{
    /// <summary>Rostering-rules key holding the paid-break factor override, as a percent.</summary>
    public const string PaidBreakFactorKey = "paidBreakFactorPct";

    /// <summary>Shipped default: paid breaks run ~4.5% on top of hours on task.</summary>
    public const double DefaultPaidBreakFactorPct = 4.5;

    /// <summary>
    /// Gross hours-on-task up to PAID hours by adding the paid-break allowance.
    ///
    ///   paidHours = workedHours × (1 + factor/100)
    ///
    /// Someone on a paid break is not on a task, so their break never lands in the
    /// actuals file's category columns - but it is still paid, and so still sits
    /// inside the Kronos figure. Dividing one by the other without this correction
    /// understates OE by about the break factor.
    ///
    /// Configurable per network under Rules → Labour Group Rules → Breaks: the award's paid
    /// break entitlement is not ours to hard-code.
    /// </summary>
    public static double WithPaidBreaks(double workedHours, double breakFactorPct = DefaultPaidBreakFactorPct)
    {
        if (!(workedHours > 0)) return 0;
        // A negative or non-finite factor would quietly SHRINK the hours, which is
        // never what a break allowance means - treat it as no allowance at all.
        var pct = double.IsFinite(breakFactorPct) && breakFactorPct > 0 ? breakFactorPct : 0;
        return workedHours * (1 + pct / 100);
    }

    /// <summary>
    /// Operational Efficiency: actual hours @100% over the hours that were actually
    /// available for operational work.
    ///
    ///   OE = actualHours100 / (kronosHours − nonOpHours) × 100
    ///
    /// Non-op comes off the denominator because those hours were never available to
    /// do operational work. Leaving them in would charge the store for a stocktake
    /// or a training block as though it were inefficiency.
    ///
    /// There is no ÷0.9 here. The 90% planning factor governs how a plan is BUILT;
    /// it has no place in measuring a finished day against its own 100% standard.
    ///
    /// Returns null when either side is missing, so an un-uploaded day reads as "-"
    /// rather than a confident 0%.
    /// </summary>
    public static double? OperationalEfficiencyPct(double actualHours100, double kronosHours, double nonOpHours = 0)
    {
        var operational = kronosHours - nonOpHours;
        if (operational <= 0 || actualHours100 <= 0) return null;
        return actualHours100 / operational * 100;
    }

    /// <summary>
    /// Items per labour hour, measured against the full Kronos figure - non-op
    /// included. Unlike OE this is a productivity-per-dollar-paid measure, so every
    /// paid hour belongs in the denominator.
    /// </summary>
    public static double? ItemsPerLabourHour(double items, double kronosHours)
    {
        if (kronosHours <= 0 || items <= 0) return null;
        return items / kronosHours;
    }

    /// <summary>
    /// Average spend: sales ÷ customers, both from the ACTUALS file.
    ///
    /// Ratio of sums when applied to a window - every day is pooled before the
    /// single division, so a busy Saturday moves it more than a quiet Tuesday.
    /// </summary>
    public static double? AverageSpend(double sales, double customers)
    {
        if (sales <= 0 || customers <= 0) return null;
        return sales / customers;
    }

    /// <summary>
    /// Hours used above (or below) the hours the OE target allows.
    ///
    /// OE = actualHours100 / (kronos − nonOp), so the Kronos hours that land exactly
    /// on target are (actualHours100 / target) + nonOp. The answer is the gap
    /// between that and the hours actually paid for:
    ///
    ///   POSITIVE = hours spent BEYOND target - the store is under its OE
    ///   NEGATIVE = hours to spare - the store is ahead of target
    ///
    /// Example (G999, 04/08): 47.88h actual@100%, 53.80h Kronos, 0.30h non-op,
    /// 90% target → 47.88/0.9 + 0.30 = 53.50h allowed, so +0.30h too many.
    /// </summary>
    public static double? HoursToOeTarget(double actualHours100, double kronosHours, double oeTargetPct, double nonOpHours = 0)
    {
        if (actualHours100 <= 0 || kronosHours <= 0 || oeTargetPct <= 0) return null;
        var allowed = actualHours100 / (oeTargetPct / 100) + nonOpHours;
        return kronosHours - allowed;
    }
}
