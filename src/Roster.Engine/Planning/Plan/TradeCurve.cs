// Port of src/lib/plan/tradeCurve.ts.
// ─── Trade curve: the quarter-hour pattern, bucketed for reading ─────────────
// Pure module (plan page + tests).
//
// The trade curve is a STANDING WEEKLY pattern - one allocation curve
// per store per weekday, summing to 1.0 - not a per-date record. So "Monday's
// curve" IS the store's average Monday; there is nothing to average here. It
// also means this can never show what happened on one specific date: hourly
// actuals don't exist in the system (actuals_days carries daily totals only).
//
// Buckets to HOURS deliberately. At 15-minute granularity a single block reads
// as a peak that is really sampling noise - G242's Monday argmax is a 3.71%
// block sitting between neighbours of 2.64% and 2.79%. Hourly buckets put the
// peak where a person standing in the store would say it is.

using Roster.Engine.Core;

namespace Roster.Engine.Planning.Plan;

/// <param name="Hour">0-23.</param>
/// <param name="Share">Fraction of the day's trade in this hour.</param>
public sealed record CurvePoint(double Hour, double Share);

/// <param name="BlockTime">"HH:MM:SS".</param>
public sealed record QuarterHourPoint(string BlockTime, double Allocation);

public static class TradeCurve
{
    /// <summary>
    /// A weekday's 15-minute shares, renormalised to the day, blocks with no trade
    /// left out (QuarterShare in the TypeScript: Placement's CurveShare here). The
    /// plan grid heats its own quarter-hour columns with these - at that grain the
    /// columns line up with the tiles, which is the point - while the chart below
    /// keeps the hourly reading.
    /// </summary>
    public static List<CurveShare> QuarterHourShares(IEnumerable<QuarterHourPoint> rows)
    {
        var out_ = new List<CurveShare>();
        double total = 0;
        foreach (var r in rows)
        {
            var a = r.Allocation;
            if (!double.IsFinite(a) || a <= 0) continue;
            out_.Add(new CurveShare(Grid.TimeToMin(r.BlockTime), a));
            total += a;
        }
        if (total <= 0) return [];
        return Js.Sort(out_.Select(p => new CurveShare(p.Time, p.Share / total)).ToList(), (a, b) => a.Time - b.Time);
    }

    /// <summary>
    /// Sum 15-minute allocations into whole hours, keeping only hours that carry
    /// trade. Shares are renormalised to the rows supplied, so a curve clipped to
    /// trading hours still reads as a share of the trading day.
    /// </summary>
    public static List<CurvePoint> BucketHourly(IEnumerable<QuarterHourPoint> rows)
    {
        var byHour = new JsMap<double, double>();
        double total = 0;
        foreach (var r in rows)
        {
            var a = r.Allocation;
            if (!double.IsFinite(a) || a <= 0) continue;
            var hour = Math.Floor(Grid.TimeToMin(r.BlockTime) / 60);
            byHour.Set(hour, byHour.GetOr(hour, 0) + a);
            total += a;
        }
        if (total <= 0) return [];
        return Js.Sort(byHour.Select(e => new CurvePoint(e.Key, e.Value / total)).ToList(), (a, b) => a.Hour - b.Hour);
    }

    /// <summary>The busiest hour, or null when the day carries no trade.</summary>
    public static double? PeakHour(IReadOnlyList<CurvePoint> curve)
    {
        if (curve.Count == 0) return null;
        return curve.Aggregate((best, p) => p.Share > best.Share ? p : best).Hour;
    }

    /// <summary>
    /// Every hour at or above <paramref name="thresholdPct"/> of the busiest hour -
    /// the "busy period" rather than the single peak. On a plateau day this is
    /// correctly wide: G242 runs within 85% of its peak for seven straight hours,
    /// which is the honest answer, not a defect.
    /// </summary>
    public static List<double> BusyHours(IReadOnlyList<CurvePoint> curve, double thresholdPct = 85)
    {
        if (curve.Count == 0) return [];
        var max = Js.Max(curve.Select(p => p.Share));
        if (!(max > 0)) return [];
        return curve.Where(p => p.Share / max * 100 >= thresholdPct).Select(p => p.Hour).ToList();
    }

    /// <summary>
    /// The busy period as ONE contiguous span [firstHour, lastHour], or null.
    /// BusyHours is the precise per-hour test and is what placement logic should
    /// use. For DISPLAY - and for a rule phrased as "before and after the busy
    /// period" - the span is the honest shape: on a plateau day the strict test
    /// skips hours that are a single point quieter (G242's Monday admits 12, 14, 16
    /// and 17 but not 13 or 15), and shading that as stripes tells the reader 13:00
    /// is a lull when it is nothing of the sort.
    /// </summary>
    public static (double First, double Last)? BusySpan(IReadOnlyList<CurvePoint> curve, double thresholdPct = 85)
    {
        var hs = BusyHours(curve, thresholdPct);
        if (hs.Count == 0) return null;
        return (hs[0], hs[^1]);
    }

    /// <summary>Share of the day's trade from <paramref name="fromHour"/> onward - the night-trading tell.</summary>
    public static double LateShare(IReadOnlyList<CurvePoint> curve, double fromHour)
    {
        var total = curve.Aggregate(0.0, (s, p) => s + p.Share);
        if (total <= 0) return 0;
        return curve.Where(p => p.Hour >= fromHour).Aggregate(0.0, (s, p) => s + p.Share) / total;
    }

    // Traditional green→red heat ramp for the heatmap. A multi-hue ramp is the
    // documented "semantic heat" exception to one-hue-for-magnitude, and it needs
    // its scale legend (which the card carries).
    //
    // The steps are chosen so LIGHTNESS still falls monotonically from green to
    // red, verified against a white surface. That matters: red/green is the
    // classic colour-blind pair, so the ordering has to survive on lightness
    // alone. It is why the middle steps are a deep mustard and amber rather than a
    // bright yellow - a true yellow is LIGHTER than the green either side of it,
    // which would leave the ramp unreadable in greyscale.
    //
    // Zero-trade cells are NOT a ramp step: absence is drawn as empty surface, not
    // as "a little bit of trade".
    public static readonly IReadOnlyList<string> HeatRamp = ["#84bd6e", "#9c9c30", "#ae7526", "#a04620", "#8e2119"];

    // The line/area hue for the curve itself. A muted slate blue, deliberately
    // quieter than the heatmap: the curve is context for the plan above it, not
    // the loudest thing on the page.
    public const string CurveHue = "#5a86ad";

    /// <summary>
    /// Bin edges for the heatmap, as QUANTILES of the week's trading hours.
    /// Splitting 0→max into equal bands looks reasonable and reads terribly: a
    /// store's trading hours cluster in the top half of their own range (G242 runs
    /// 9-12% for seven hours straight), so nearly every cell lands in the last two
    /// bands and the map turns into a wall of red that discriminates nothing.
    /// Quantiles spread the ramp across the values that actually occur. The edges
    /// are computed once for the whole week and applied to every day, so days
    /// still compare like for like - a quiet Sunday hour and a busy Friday hour
    /// with the same share get the same colour.
    /// </summary>
    public static List<double> HeatThresholds(IEnumerable<double> shares, int? bins = null)
    {
        var n = bins ?? HeatRamp.Count;
        var v = Js.Sort(shares.Where(s => s > 0).ToList(), (a, b) => a - b);
        if (v.Count == 0) return [];
        return Enumerable.Range(0, n)
            .Select(i => v[(int)Math.Min(v.Count - 1, Math.Ceiling((double)(i + 1) / n * v.Count) - 1)])
            .ToList();
    }

    /// <summary>Ramp index for a share against HeatThresholds. -1 means no trade.</summary>
    public static int HeatStep(double share, IReadOnlyList<double> thresholds)
    {
        if (share <= 0 || thresholds.Count == 0) return -1;
        for (var i = 0; i < thresholds.Count; i++)
            if (share <= thresholds[i] + 1e-12) return i;
        return thresholds.Count - 1;
    }

    /// <summary>
    /// Axis ticks on clean numbers. A raw max/4 split gives 3% / 7% / 10% / 13%,
    /// which reads as though the gridlines mean something; stepping on 1/2/2.5/5
    /// (x10^n) keeps them round and the top tick at or above the data.
    /// (V8 computes 10 ** -4 and 10 ** -5 one bit off the true powers; for a
    /// maximum under 0.004 the ticks can differ in their last bit - no trade
    /// curve's busiest share is that small.)
    /// </summary>
    public static List<double> NiceTicks(double max, int count = 4)
    {
        if (max <= 0) return [0];
        var rough = max / count;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).Cast<double?>().FirstOrDefault(s => s >= rough) ?? 10 * mag;
        // Run PAST the data, never short of it: the caller scales the plot to the top
        // tick, so stopping below the maximum would draw the curve above the axis.
        var ticks = new List<double>();
        for (double v = 0; ; v += step)
        {
            ticks.Add(Js.Number(Js.ToFixed(v, 10)));
            if (v >= max - 1e-12) break;
        }
        return ticks;
    }

    public static readonly IReadOnlyList<string> DowLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    // Full names for prose. The short labels are for the heatmap's row headers;
    // using them in a sentence produced "Trade curve - Mons" and "typical Monshape".
    public static readonly IReadOnlyList<string> DowFull = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    // Day-of-week with Monday=0 (matching the quarter_hour_allocation rows) is
    // Core's Period.DowMon0 - the one shared definition.
}
