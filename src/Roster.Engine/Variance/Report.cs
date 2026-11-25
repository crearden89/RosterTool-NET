// Port of src/app/(app)/admin/variance-report/page.tsx: the report it builds from
// one day's rows, as a pure function (the page itself is
// Roster.Web/Features/Admin/VarianceReport).
//
// The web port of the SPi "OE variance report" script: one date, every store,
// actuals compared with benchmarks. Covers Sales, Items, the six labour-hour
// categories, and the file-carried boolean flags (Is_Mhs / Has_Esl / Baler -
// columns inside the exports, nothing to do with the Store Attributes screen).
// Not ported: cases shipped, liquor, cash %, region averages and the OE pivot,
// whose SPi export columns this system does not ingest.

using Roster.Engine.Core;

namespace Roster.Engine.Variance;

/// <summary>A store as the report names it: its id and its code ("G025").</summary>
public sealed record VarianceStore(Guid Id, string Code);

/// <summary>The benchmark_days columns the report reads, for one store on the date.</summary>
public sealed record BenchmarkDayFigures
{
    public required Guid StoreId { get; init; }
    public double? SalesForecast { get; init; }
    public double? Items { get; init; }
    public double? CustomerCount { get; init; }
    public bool? IsMhs { get; init; }
    public bool? HasEsl { get; init; }
    public bool? HasBaler { get; init; }
    public double? LrHours100 { get; init; }
    public double? CleaningHours100 { get; init; }
    public double? SpecialsHours100 { get; init; }
    public double? ResetHours100 { get; init; }
    public double? AdminHours100 { get; init; }
    public double? CheckoutHours100 { get; init; }
}

/// <summary>The actuals_days columns the report reads, for one store on the date.</summary>
public sealed record ActualsDayFigures
{
    public required Guid StoreId { get; init; }
    public double? ActualSales { get; init; }
    public double? ActualItems { get; init; }
    public double? ActualCustomers { get; init; }
    public double? ActualHours100 { get; init; }
    public double? UkgActualHours { get; init; }
    public bool? IsMhs { get; init; }
    public bool? HasEsl { get; init; }
    public bool? HasBaler { get; init; }

    /// <summary>The per-category hours; null on rows ingested before the breakdown existed.</summary>
    public double? LrHours { get; init; }
    public double? CleaningHours { get; init; }
    public double? SpecialsHours { get; init; }
    public double? ResetHours { get; init; }
    public double? AdminHours { get; init; }
    public double? CheckoutHours { get; init; }
}

/// <summary>
/// One exception row: a <see cref="VarianceFlag"/> and its store. Boolean rows
/// carry TRUE/FALSE text (<see cref="ActualText"/>, <see cref="BenchText"/>)
/// instead of numbers.
/// </summary>
public sealed record VarianceRow(
    string Store,
    string Metric,
    double? Actual,
    double? Benchmark,
    double? VariancePct,
    string Reason,
    string? ActualText = null,
    string? BenchText = null)
{
    public static VarianceRow From(string store, VarianceFlag f) =>
        new(store, f.Metric, f.Actual, f.Benchmark, f.VariancePct, f.Reason);
}

/// <summary>A store present in one file but not the other, where sales are reported.</summary>
public sealed record MissingStore(string Store, string PresentIn, double? Sales, string Reason);

/// <summary>The day's report: the four figures across the top and the three exception tables.</summary>
public sealed record VarianceReport(
    int StoresWithBenchmarks,
    IReadOnlyList<VarianceRow> MetricFlags,
    IReadOnlyList<VarianceRow> HourFlags,
    IReadOnlyList<MissingStore> Missing);

public static class VarianceReports
{
    /// <summary>The script's thresholds when variance_settings has no row: metrics 70%, hours 20%, data light 15%.</summary>
    public const double DefaultMetricPct = 70, DefaultHoursPct = 20, DefaultDataLightPct = 15;

    private static readonly (string Label, Func<ActualsDayFigures, double?> Actual, Func<BenchmarkDayFigures, double?> Bench)[] HourPairs =
    [
        ("Load Running", a => a.LrHours, b => b.LrHours100),
        ("Cleaning", a => a.CleaningHours, b => b.CleaningHours100),
        ("Specials", a => a.SpecialsHours, b => b.SpecialsHours100),
        ("Load Reset", a => a.ResetHours, b => b.ResetHours100),
        ("AdminMgmt", a => a.AdminHours, b => b.AdminHours100),
        ("Checkout", a => a.CheckoutHours, b => b.CheckoutHours100),
    ];

    private static string? Tf(bool? v) => v is null ? null : v.Value ? "TRUE" : "FALSE";

    /// <summary>
    /// The report for one date. <paramref name="bench"/> and <paramref name="acts"/>
    /// are that date's rows, one per store.
    /// </summary>
    public static VarianceReport Build(
        IEnumerable<VarianceStore> stores,
        IEnumerable<BenchmarkDayFigures> bench,
        IEnumerable<ActualsDayFigures> acts,
        double metricPct,
        double hoursPct)
    {
        var codeOf = new Dictionary<Guid, string>();
        foreach (var s in stores) codeOf[s.Id] = s.Code;
        string StoreName(Guid id) => codeOf.TryGetValue(id, out var code) ? code : id.ToString();

        // The page reads both files ordered by store_id. Postgres compares uuids
        // byte by byte, which is the order of their text form; SQL Server orders
        // uniqueidentifier differently, so the order is set here. The missing-store
        // table keeps this order (the two flag tables are sorted below).
        var benchBy = new JsMap<Guid, BenchmarkDayFigures>();
        foreach (var b in bench.OrderBy(b => b.StoreId.ToString(), StringComparer.Ordinal)) benchBy.Set(b.StoreId, b);
        var actBy = new JsMap<Guid, ActualsDayFigures>();
        foreach (var a in acts.OrderBy(a => a.StoreId.ToString(), StringComparer.Ordinal)) actBy.Set(a.StoreId, a);

        var metricFlags = new List<VarianceRow>();
        var hourFlags = new List<VarianceRow>();
        var missing = new List<MissingStore>();

        foreach (var (storeId, b) in benchBy)
        {
            var store = StoreName(storeId);
            if (!actBy.TryGet(storeId, out var a))
            {
                // The script only lists a missing store when money moved on the other
                // side - a benchmark with no forecast is dormancy, not a gap.
                if ((b.SalesForecast ?? 0) > 0)
                    missing.Add(new MissingStore(
                        store,
                        "Benchmark only",
                        b.SalesForecast,
                        "Benchmark loaded but no actuals were uploaded for this date."));
                continue;
            }
            foreach (var (metric, actual, benchmark) in new (string, double?, double?)[]
                     {
                         ("Retail Sales", a.ActualSales, b.SalesForecast),
                         ("Total Items", a.ActualItems, b.Items),
                         ("Customer Count", a.ActualCustomers, b.CustomerCount),
                     })
            {
                var f = VarianceLogic.CompareMetric(metric, actual, benchmark, metricPct);
                if (f is not null) metricFlags.Add(VarianceRow.From(store, f));
            }
            // File-carried booleans (from the exports themselves, not Store
            // Attributes): a flag when the two files disagree, and the script's policy
            // rule - Is_Mhs / Has_Esl should not be FALSE on a store that traded.
            foreach (var (metric, av, bv) in new (string, bool?, bool?)[]
                     {
                         ("Is_Mhs", a.IsMhs, b.IsMhs),
                         ("Has_Esl", a.HasEsl, b.HasEsl),
                         ("Has_Baler", a.HasBaler, b.HasBaler),
                     })
            {
                if (av is null || bv is null) continue; // column absent - skipped
                if (av != bv)
                    metricFlags.Add(new VarianceRow(
                        store, metric, null, null, null,
                        "Boolean value differs between Actuals and Benchmark.",
                        ActualText: Tf(av), BenchText: Tf(bv)));
            }
            if ((a.ActualSales ?? 0) > 0)
            {
                foreach (var (metric, av) in new (string, bool?)[] { ("Is_Mhs", a.IsMhs), ("Has_Esl", a.HasEsl) })
                {
                    if (av == false)
                        metricFlags.Add(new VarianceRow(
                            store, metric, null, null, null,
                            $"{metric} is FALSE on a store that traded.",
                            ActualText: "FALSE", BenchText: "Expected TRUE"));
                }
            }

            // Hours at 100% both sides. Rows ingested before the breakdown existed
            // carry null categories - the total is still compared.
            var benchTotal = 0.0;
            foreach (var pair in HourPairs) benchTotal += pair.Bench(b) ?? 0;
            var totalFlag = VarianceLogic.CompareMetric("Total Hours", a.ActualHours100, benchTotal, hoursPct);
            if (totalFlag is not null) hourFlags.Add(VarianceRow.From(store, totalFlag));
            foreach (var (label, actual, benchmark) in HourPairs)
            {
                var f = VarianceLogic.CompareMetric(label, actual(a), benchmark(b), hoursPct);
                if (f is not null) hourFlags.Add(VarianceRow.From(store, f));
            }
        }
        foreach (var (storeId, a) in actBy)
        {
            if (!benchBy.Has(storeId) && (a.ActualSales ?? 0) > 0)
                missing.Add(new MissingStore(
                    StoreName(storeId),
                    "Actuals only",
                    a.ActualSales,
                    "Actuals report sales but no benchmark exists for this date."));
        }
        Js.Sort(metricFlags, ByStoreThenMetric);
        Js.Sort(hourFlags, ByStoreThenMetric);

        return new VarianceReport(benchBy.Size, metricFlags, hourFlags, missing);
    }

    private static double ByStoreThenMetric(VarianceRow x, VarianceRow y)
    {
        var c = Js.LocaleCompare(x.Store, y.Store);
        return c != 0 ? c : Js.LocaleCompare(x.Metric, y.Metric);
    }

    /// <summary>
    /// The Variance column: "—" on a boolean row, "Undefined" for a zero
    /// benchmark, else the signed percentage to one place ("+12.5%").
    /// </summary>
    public static string VarianceText(VarianceRow r) =>
        r.ActualText is not null && r.ActualText.Length > 0
            ? "—"
            : r.VariancePct is not { } v
                ? "Undefined"
                : $"{(v > 0 ? "+" : "")}{Js.ToFixed(v, 1)}%";

    /// <summary>The Variance column is red when Undefined or beyond ±100%, amber otherwise.</summary>
    public static bool VarianceIsSevere(VarianceRow r) => r.VariancePct is not { } v || Math.Abs(v) > 100;
}
