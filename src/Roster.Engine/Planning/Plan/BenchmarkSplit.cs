// Port of src/lib/plan/benchmarkSplit.ts.
// ─── The day's benchmark, split by labour group: one split for every page ────
// The owner (15/11/2026), on Analysis's groups adding up to 621.69 h beside a
// 622.06 h benchmark: the benchmark split should add up exactly.
//
// The day's benchmark is the hours check's (owner decision R2): the benchmark
// file, booked non-op hours and any public-holiday move. Stage 1 shares it out
// by group - checkout keeps its rounded-up till hours and the other groups give
// up the rounding (decision 10) - but it funds only the rounding above the
// tills' own demand: the file's checkout hours spread over the day's trading
// quarter-hours by the weekday's trade pattern, scaled by any sales override.
// Where that demand is not the file's checkout figure, Stage 1's groups add up
// to more or less than the day's benchmark:
//  - a sales override scales the tills' demand but not the file (a +5% day
//    with 11.50 h of checkout: 0.58 h over);
//  - the pattern's share outside the day's trading hours is shed (a Saturday
//    closing at 19:00 on a pattern that runs to 21:45: 0.21 h under);
//  - rounding no other group could fund (Stage 1's checkoutShortfall).
// So every group keeps Stage 1's share and Checkout's share is what is left of
// the day's benchmark: the file's checkout hours and the rounding the other
// groups gave up for it. The tills planned above or below that share show on
// the Checkout row, where the difference comes from. (On a public-holiday eve
// the quarter-hour rounding of the Load Running moved in, under 0.13 h, is
// left there too.) Nothing here changes a plan. Pure.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Planning.Plan;

/// <summary>The parts of a Stage 1 result the split reads (older held days lack some).</summary>
public sealed record BenchmarkParts
{
    public double TotalBenchmark { get; init; }
    public IReadOnlyList<GroupAllocation>? Allocations { get; init; }
    public JsMap<string, double>? BenchmarkFile { get; init; }
    public double? PhMovedHours { get; init; }

    public static BenchmarkParts Of(Stage1Computation s1) => new()
    {
        TotalBenchmark = s1.TotalBenchmark,
        Allocations = s1.Allocations,
        BenchmarkFile = s1.BenchmarkFile,
        PhMovedHours = s1.PhMovedHours,
    };
}

public static class BenchmarkSplit
{
    /// <summary>
    /// The day's benchmark the hours check holds it to: the benchmark file, booked
    /// non-op hours and any public-holiday move. A day held before Stage 1 kept the
    /// file has Stage 1's own total.
    /// </summary>
    public static double DayBenchmark(BenchmarkParts s1)
    {
        var nonOp = s1.Allocations?.FirstOrDefault(a => a.Group == "non_op")?.BenchmarkHours ?? 0;
        if (s1.BenchmarkFile is null) return s1.TotalBenchmark;
        double sum = 0;
        foreach (var v in s1.BenchmarkFile.Values()) sum += v;
        return sum + nonOp + (s1.PhMovedHours ?? 0);
    }

    /// <summary>
    /// Each group's share of the day's benchmark, non-op included: Stage 1's, with
    /// Checkout's the rest of <paramref name="total"/> (the day's benchmark unless
    /// given), so the shares add up to it.
    /// </summary>
    public static JsMap<string, double> Split(BenchmarkParts s1, double? total = null)
    {
        var t = total ?? DayBenchmark(s1);
        var out_ = new JsMap<string, double>();
        foreach (var a in s1.Allocations ?? [])
            if (a.Group != "checkout") out_.Set(a.Group, out_.GetOr(a.Group, 0) + a.BenchmarkHours);
        double sum = 0;
        foreach (var v in out_.Values()) sum += v;
        out_.Set("checkout", t - sum);
        return out_;
    }

    /// <summary>Whole hundredths of an hour, as a page shows hours: 41.884 -> 4188.</summary>
    public static double Hundredths(double hours) => Js.Round(Js.Number(Js.ToFixed(hours, 2)) * 100);

    /// <summary>
    /// Figures to show at two decimals that add up to the total shown: each one
    /// floored to whole hundredths, and the hundredths left over handed to the
    /// largest remainders (ties to the larger figure, then the order given). The
    /// parts must add up to <paramref name="total"/>, as a Split's do; each then
    /// moves by at most a hundredth. <paramref name="keys"/> puts rows with no
    /// figure in, at 0. Returns hundredths: show them as (n / 100).toFixed(2).
    /// </summary>
    public static JsMap<string, double> ToHundredths(JsMap<string, double> parts, double total, IReadOnlyList<string>? keys = null)
    {
        var ks = new JsSet<string>((keys ?? []).Concat(parts.Keys())).ToList();
        // Float dust off first: 0.29 * 100 is 28.999999999999996.
        var exact = ks.Select(k => Js.Round(parts.GetOr(k, 0) * 1e8) / 1e6).ToList();
        var out_ = exact.Select(Math.Floor).ToList();
        var left = Hundredths(total) - out_.Aggregate(0.0, (a, b) => a + b);
        var order = Js.Sorted(Enumerable.Range(0, ks.Count),
            (a, b) => (exact[b] - out_[b] - (exact[a] - out_[a])).Or(exact[b] - exact[a]).Or(a - b));
        for (var n = 0; left != 0 && n < 10 * ks.Count; n++)
        {
            var i = left > 0 ? order[n % ks.Count] : order[ks.Count - 1 - n % ks.Count];
            out_[i] += Js.Sign(left);
            left -= Js.Sign(left);
        }
        var result = new JsMap<string, double>();
        for (var i = 0; i < ks.Count; i++) result.Set(ks[i], out_[i]);
        return result;
    }

    /// <summary>
    /// The Plan page's Benchmark Hours table, in hundredths: the day's benchmark
    /// and each group's share of it, the rows adding up to the total as shown.
    /// </summary>
    public static (double Total, JsMap<string, double> Rows) BenchmarkRows(BenchmarkParts s1, double? total = null, IReadOnlyList<string>? keys = null)
    {
        var t = total ?? DayBenchmark(s1);
        return (Hundredths(t), ToHundredths(Split(s1, t), t, keys));
    }

    /// <summary>Hundredths of an hour as a page writes them: 4188 -> "41.88".</summary>
    public static string HoursText(double h) => Js.ToFixed(h / 100, 2);

    /// <summary>A signed difference in hundredths: 57 -> "+0.57", -21 -> "-0.21".</summary>
    public static string SignedText(double h) => (h >= 0 ? "+" : "-") + Js.ToFixed(Math.Abs(h) / 100, 2);
}
