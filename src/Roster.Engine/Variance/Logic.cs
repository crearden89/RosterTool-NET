// Port of src/lib/variance/logic.ts.
// ─── Daily variance rules (pure) ─────────────────────────────────────────────
// The comparison semantics of the SPi OE variance script, ported: variance is
// (actual − benchmark) / benchmark; a zero benchmark against a non-zero actual
// is "Undefined" (flagged, no percentage); both-zero and blank sides are
// skipped. Thresholds come from variance_settings.

using Roster.Engine.Core;

namespace Roster.Engine.Variance;

/// <summary>One figure outside its threshold (VarianceFlag).</summary>
/// <param name="VariancePct">null = Undefined (zero benchmark).</param>
public sealed record VarianceFlag(string Metric, double? Actual, double? Benchmark, double? VariancePct, string Reason);

/// <summary>
/// A traffic light: the dashboard's Data light (<see cref="VarianceLogic.DataLight"/>)
/// and, with the same four values, the overview's <c>Light</c> type
/// ("red" | "amber" | "green" | "none").
/// </summary>
public enum Light
{
    Red,
    Amber,
    Green,
    None,
}

public static class Lights
{
    /// <summary>The TypeScript string: "red", "amber", "green" or "none".</summary>
    public static string Key(this Light light) => light switch
    {
        Light.Red => "red",
        Light.Amber => "amber",
        Light.Green => "green",
        Light.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(light), light, null),
    };
}

/// <summary>One trade metric on both files (actual against benchmark); null = blank.</summary>
public readonly record struct MetricPair(double? Actual, double? Benchmark);

public static class VarianceLogic
{
    /// <summary>
    /// compareMetric: null when the figure is within <paramref name="thresholdPct"/>
    /// of its benchmark, when either side is blank, or when both are zero.
    /// </summary>
    public static VarianceFlag? CompareMetric(string metric, double? actual, double? benchmark, double thresholdPct)
    {
        if (actual is not { } a || benchmark is not { } b) return null; // blank side - skipped
        if (b == 0)
        {
            if (a == 0) return null;
            return new VarianceFlag(metric, a, b, null, "Actual is non-zero while benchmark is zero.");
        }
        var variance = (a - b) / b;
        if (Math.Abs(variance) * 100 <= thresholdPct) return null;
        var dir = variance > 0 ? "above" : "below";
        return new VarianceFlag(
            metric,
            a,
            b,
            variance * 100,
            $"Actual is {Js.ToFixed(Math.Abs(variance * 100), 1)}% {dir} benchmark.");
    }

    // The dashboard Data light. Metrics = the TRADE figures present on both files
    // at day level: Sales, Items and Customer Count. Hours are deliberately not
    // among them - an hours overrun is a performance result, not a sign the day's
    // data is wrong, and the Daily Variance Report already reports it.
    //  green - actuals loaded, every metric present and within the threshold
    //  amber - actuals loaded but a metric is missing or outside the threshold
    //  red   - benchmark loaded, no actuals
    //  none  - no benchmark
    public static Light DataLight(bool hasBenchmark, bool hasActuals, IEnumerable<MetricPair> pairs, double thresholdPct)
    {
        if (!hasBenchmark) return Light.None;
        if (!hasActuals) return Light.Red;
        foreach (var p in pairs)
        {
            if (p.Actual is not { } a || a == 0) return Light.Amber; // metric missing
            if (p.Benchmark is not { } b || b == 0) return Light.Amber;
            if (Math.Abs(a - b) / b * 100 > thresholdPct) return Light.Amber;
        }
        return Light.Green;
    }
}
