// Port of src/lib/overview/compute.ts: its types, and the pure half of computeOverview
// (everything after the reads). The reads - variance_settings, the overview_rollup rows
// and the paid-break factor - are the Dashboard's page service.

using Roster.Engine.Core;
using Roster.Engine.Variance;

namespace Roster.Engine.Overview;

/// <summary>
/// One row per DATE from the overview_rollup database function - the aggregates are
/// summed across every store in scope before they leave the database, so the payload is
/// bounded by the date range (≤ ~400 rows), not by store count × days.
/// </summary>
public sealed record RollupRow(
    DateOnly Date,
    double ForecastSales,
    double CustomerCount,
    double BenchItems,
    double Hours90,
    double Hours100,
    double ActualSales,
    double ActualItems,
    double ActualCustomers,
    double ActualHours100,
    double KronosHours,
    double NonopMinutes,
    bool AnyApproved,
    bool AnyGenerated);

/// <summary>Aggregated metric bundle used by both day rows and summary rows.</summary>
public record Summary
{
    public double ForecastSales { get; set; }
    public double ActualSales { get; set; }
    /// <summary>Sum of the 90% labour-group hours.</summary>
    public double BenchmarkHours { get; set; }
    public double NonOpHours { get; set; }
    /// <summary>Benchmark + non-op.</summary>
    public double TargetHours { get; set; }
    /// <summary>
    /// What the store actually PAID FOR, from the actuals file's Kronos figure. Not the
    /// sum of the labour-category columns: those are what the day's work was WORTH (see
    /// ActualHours100). Shown as the "Actual Hrs" column.
    /// </summary>
    public double KronosHours { get; set; }
    /// <summary>FORECAST customers, from the benchmark file.</summary>
    public double CustomerCount { get; set; }
    /// <summary>
    /// ACTUAL customers, from the actuals file. Average Spend divides by this - dividing
    /// actual sales by forecast customers inflated the figure on a busier-than-planned day
    /// and deflated it on a quiet one.
    /// </summary>
    public double ActualCustomers { get; set; }
    public double Items { get; set; }
    public double BenchItems { get; set; }
    /// <summary>Raw 100% labour hours (cog column).</summary>
    public double Hours100 { get; set; }
    /// <summary>90% planning hours (cog column, == BenchmarkHours).</summary>
    public double Hours90 { get; set; }
    /// <summary>
    /// Actual Hours @ 100%: the sum of the actuals file's per-category columns (Load
    /// Running, Cleaning, Specials, Reset, AdminMgmt, Checkout). The numerator of OE.
    /// </summary>
    public double ActualHours100 { get; set; }
    public double? OeResult { get; set; }
    public double? Iplh { get; set; }
    public double? AvgSpend { get; set; }
    /// <summary>
    /// Hours spent beyond what the OE target allows. Positive = too many hours (under
    /// target); negative = hours to spare (ahead of target).
    /// </summary>
    public double? HoursToTarget { get; set; }

    /// <summary><c>{ ...s }</c>: a shallow copy of the summary's own figures.</summary>
    public Summary CopySummary() => new()
    {
        ForecastSales = ForecastSales,
        ActualSales = ActualSales,
        BenchmarkHours = BenchmarkHours,
        NonOpHours = NonOpHours,
        TargetHours = TargetHours,
        KronosHours = KronosHours,
        CustomerCount = CustomerCount,
        ActualCustomers = ActualCustomers,
        Items = Items,
        BenchItems = BenchItems,
        Hours100 = Hours100,
        Hours90 = Hours90,
        ActualHours100 = ActualHours100,
        OeResult = OeResult,
        Iplh = Iplh,
        AvgSpend = AvgSpend,
        HoursToTarget = HoursToTarget,
    };
}

public sealed record DayRow : Summary
{
    public DateOnly Date { get; init; }
    /// <summary>"Mon"</summary>
    public string Dow { get; init; } = "";
    public bool IsWeekend { get; init; }
    public bool IsToday { get; init; }
    public Light RosterStatus { get; init; } = Light.None;
    public Light DataStatus { get; init; } = Light.None;

    /// <summary><c>{ ...summary, date, dow, … }</c>.</summary>
    public static DayRow From(Summary s, DateOnly date, string dow, bool isWeekend, bool isToday, Light rosterStatus, Light dataStatus) =>
        new()
        {
            ForecastSales = s.ForecastSales,
            ActualSales = s.ActualSales,
            BenchmarkHours = s.BenchmarkHours,
            NonOpHours = s.NonOpHours,
            TargetHours = s.TargetHours,
            KronosHours = s.KronosHours,
            CustomerCount = s.CustomerCount,
            ActualCustomers = s.ActualCustomers,
            Items = s.Items,
            BenchItems = s.BenchItems,
            Hours100 = s.Hours100,
            Hours90 = s.Hours90,
            ActualHours100 = s.ActualHours100,
            OeResult = s.OeResult,
            Iplh = s.Iplh,
            AvgSpend = s.AvgSpend,
            HoursToTarget = s.HoursToTarget,
            Date = date,
            Dow = dow,
            IsWeekend = isWeekend,
            IsToday = isToday,
            RosterStatus = rosterStatus,
            DataStatus = dataStatus,
        };
}

/// <param name="Key">The week's Monday, ISO.</param>
/// <param name="Label">"9–15 Nov"</param>
/// <param name="PlanAllowed">The Plan week button opens the plan (forecast loaded, or within the horizon).</param>
public sealed record WeekGroup(string Key, string Label, DateOnly MondayDate, bool PlanAllowed, IReadOnlyList<DayRow> Days, Summary Wtd);

public sealed record PeriodKpis(double? Wtd, double? Mtd, double? Ytd);

/// <summary>The MTD row: sits just after <paramref name="AfterDate"/> (yesterday), summarising month start → yesterday.</summary>
public sealed record MtdRow(DateOnly AfterDate, Summary Summary);

public sealed record OverviewData(
    IReadOnlyList<WeekGroup> Weeks,
    DateOnly MonthStart,
    double OeTarget,
    PeriodKpis Oe,
    PeriodKpis AvgSpend,
    MtdRow? Mtd,
    DateOnly Today);

public static class OverviewCompute
{
    private static readonly string[] Dow = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] ShortMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static Summary EmptySummary() => new();

    private static Summary Finalize(Summary s, double oeTarget)
    {
        // Non-Op is additive to the hours that must be rostered (spec: benchmark +
        // non-op = target), not a deduction.
        s.TargetHours = s.BenchmarkHours + s.NonOpHours;
        // Non-op comes off the OE denominator: those hours were never available for
        // operational work, so the store is not charged for them as inefficiency.
        s.OeResult = Metrics.OperationalEfficiencyPct(s.ActualHours100, s.KronosHours, s.NonOpHours);
        s.Iplh = Metrics.ItemsPerLabourHour(s.Items, s.KronosHours);
        s.AvgSpend = Metrics.AverageSpend(s.ActualSales, s.ActualCustomers);
        s.HoursToTarget = Metrics.HoursToOeTarget(s.ActualHours100, s.KronosHours, oeTarget, s.NonOpHours);
        return s;
    }

    public static Summary Summarize(IEnumerable<Summary> rows, double oeTarget)
    {
        var o = EmptySummary();
        foreach (var r in rows)
        {
            o.ForecastSales += r.ForecastSales;
            o.ActualSales += r.ActualSales;
            o.BenchmarkHours += r.BenchmarkHours;
            o.NonOpHours += r.NonOpHours;
            o.KronosHours += r.KronosHours;
            o.CustomerCount += r.CustomerCount;
            o.ActualCustomers += r.ActualCustomers;
            o.Items += r.Items;
            o.BenchItems += r.BenchItems;
            o.Hours100 += r.Hours100;
            o.Hours90 += r.Hours90;
            o.ActualHours100 += r.ActualHours100;
        }
        return Finalize(o, oeTarget);
    }

    /// <summary>The month's last day.</summary>
    public static DateOnly MonthEnd(DateOnly monthStart) =>
        new(monthStart.Year, monthStart.Month, DateTime.DaysInMonth(monthStart.Year, monthStart.Month));

    /// <summary>
    /// The dates overview_rollup is asked for: 1 January of today's year (or the month, if
    /// earlier) to the later of the month's end and today, so the KPI windows
    /// (WTD/MTD/YTD, always relative to today) come from the same fetch.
    /// </summary>
    public static (DateOnly From, DateOnly To) RollupRange(DateOnly monthStart, DateOnly today)
    {
        var monthEnd = MonthEnd(monthStart);
        var yearStart = new DateOnly(today.Year, 1, 1);
        return (monthStart < yearStart ? monthStart : yearStart, monthEnd > today ? monthEnd : today);
    }

    /// <summary>
    /// computeOverview after its reads: the month's day rows in Monday-anchored weeks with
    /// their WTD rows, the MTD row and the OE and Average Spend windows. The TypeScript
    /// worked out the month's dates from local-midnight Dates printed in UTC, which is the
    /// calendar on the UTC server it runs on; DateOnly is that calendar.
    /// </summary>
    /// <param name="rollup">overview_rollup's rows for <see cref="RollupRange"/>, in date order.</param>
    /// <param name="breakFactorPct">The paid-break allowance (rostering rule paidBreakFactorPct).</param>
    /// <param name="dataLightPct">variance_settings.data_light_pct (15 when unset).</param>
    public static OverviewData BuildOverview(
        IEnumerable<RollupRow> rollup,
        DateOnly monthStart,
        double oeTarget,
        DateOnly today,
        double breakFactorPct,
        double dataLightPct)
    {
        var monthEnd = MonthEnd(monthStart);
        var yearStart = new DateOnly(today.Year, 1, 1);

        // Per-date aggregates across all in-scope stores, straight off the rollup.
        var byDate = new JsMap<DateOnly, Summary>();
        var rosterByDate = new JsMap<DateOnly, (bool AnyApproved, bool AnyGenerated)>();
        foreach (var r in rollup)
        {
            var s = EmptySummary();
            s.ForecastSales = r.ForecastSales;
            s.CustomerCount = r.CustomerCount;
            s.BenchItems = r.BenchItems;
            s.Hours90 = r.Hours90;
            s.Hours100 = r.Hours100;
            s.BenchmarkHours = s.Hours90;
            s.ActualSales = r.ActualSales;
            s.Items = r.ActualItems;
            s.ActualCustomers = r.ActualCustomers;
            // Kronos is the hours the store actually paid for; the per-category sum is
            // what the day's work was worth.
            s.KronosHours = r.KronosHours;
            // Grossed up by the paid-break allowance at the point of entry, so the OE
            // numerator, the Hours to OE Target gap and the displayed column can never
            // drift apart on whether breaks are counted.
            s.ActualHours100 = Metrics.WithPaidBreaks(r.ActualHours100, breakFactorPct);
            s.NonOpHours = r.NonopMinutes / 60;
            byDate.Set(r.Date, s);
            rosterByDate.Set(r.Date, (r.AnyApproved, r.AnyGenerated));
        }

        // ─── Month table rows ──────────────────────────────────────────────────────
        var dayRows = new List<DayRow>();
        for (var date = monthStart.AddDays(1 - monthStart.Day); date <= monthEnd; date = date.AddDays(1))
        {
            var s = Finalize((byDate.Get(date) ?? EmptySummary()).CopySummary(), oeTarget);
            var hasRoster = rosterByDate.TryGet(date, out var roster);
            var hasBenchmark = s.BenchmarkHours > 0 || s.ForecastSales > 0;
            // Both lights are gated on benchmarks: a day the files have not reached is
            // not "behind", it is simply not in play yet - no light at all.
            var rosterStatus = !hasBenchmark
                ? Light.None
                : hasRoster && roster.AnyApproved
                    ? Light.Green
                    : hasRoster && roster.AnyGenerated
                        ? Light.Amber
                        : Light.Red;
            var dataStatus = VarianceLogic.DataLight(
                hasBenchmark,
                s.KronosHours > 0 || s.ActualHours100 > 0 || s.ActualSales > 0,
                // Trade metrics ONLY. Hours variance is a performance result, not a data
                // -integrity signal: a store that legitimately overran its benchmark
                // hours was turning the light amber as though its files were suspect.
                // The hours comparison still appears in the Daily Variance Report.
                [
                    new MetricPair(s.ActualSales, s.ForecastSales),
                    new MetricPair(s.Items, s.BenchItems),
                    new MetricPair(s.ActualCustomers, s.CustomerCount),
                ],
                dataLightPct);
            var w = Period.DowMon0(date);
            dayRows.Add(DayRow.From(s, date, Dow[(int)date.DayOfWeek], w >= 5, date == today, rosterStatus, dataStatus));
        }

        // Group into Monday-anchored weeks.
        var weekMap = new JsMap<DateOnly, List<DayRow>>();
        foreach (var r in dayRows)
        {
            var monday = r.Date.AddDays(-Period.DowMon0(r.Date));
            if (!weekMap.TryGet(monday, out var arr)) weekMap.Set(monday, arr = []);
            arr.Add(r);
        }
        var planHorizon = today.AddDays(14);
        var weeks = weekMap.Entries()
            .OrderBy(e => e.Key)
            .Select(e =>
            {
                var (monday, days) = (e.Key, e.Value);
                var last = days[^1].Date;
                return new WeekGroup(
                    monday.ToIso(),
                    $"{days[0].Date.Day}–{last.Day} {ShortMonths[last.Month - 1]}",
                    monday,
                    // Plannable when forecast data is LOADED for the week - the horizon is
                    // only the fallback for past/imminent weeks with no data yet. A hard
                    // today+14 cutoff wrongly greyed out week 2 of a freshly-uploaded
                    // fortnight even while its figures were showing in the rows below.
                    monday <= planHorizon || days.Any(d => d.BenchmarkHours > 0),
                    days,
                    Summarize(days, oeTarget));
            })
            .ToList();

        // MTD row: sits just before today's row (summarising month-start → yesterday).
        MtdRow? mtd = null;
        if (today >= monthStart && today <= monthEnd)
        {
            var yesterday = today.AddDays(-1);
            if (yesterday >= monthStart)
            {
                var priorRows = dayRows.Where(r => r.Date < today).ToList();
                if (priorRows.Count > 0) mtd = new MtdRow(yesterday, Summarize(priorRows, oeTarget));
            }
        }

        // ─── KPI windows (relative to today) ───────────────────────────────────────
        double? OeFor(DateOnly from, DateOnly to)
        {
            // Raw summaries only - Summarize() finalizes the aggregate itself, and
            // per-row ratios would be discarded anyway. (This OE - break-factored
            // actual hours over Kronos minus non-op - is deliberately different from
            // the Analysis page's oePct, which divides by PLAN-allocated hours.)
            var rows = byDate.Entries().Where(e => e.Key >= from && e.Key <= to).Select(e => e.Value);
            return Summarize(rows, oeTarget).OeResult;
        }
        double? SpendFor(DateOnly from, DateOnly to)
        {
            var rows = byDate.Entries().Where(e => e.Key >= from && e.Key <= to).Select(e => e.Value).ToList();
            var sales = rows.Aggregate(0.0, (a, s) => a + s.ActualSales);
            // ACTUAL customers on both sides of the ratio. Rows ingested before the
            // actuals feed carried Customer_Count contribute 0, so they drop out of
            // the KPI rather than being silently topped up with a forecast.
            var cust = rows.Aggregate(0.0, (a, s) => a + s.ActualCustomers);
            return cust > 0 && sales > 0 ? sales / cust : null;
        }
        var monS = new DateOnly(today.Year, today.Month, 1);
        var weekMon = today.AddDays(-Period.DowMon0(today));
        var oe = new PeriodKpis(OeFor(weekMon, today), OeFor(monS, today), OeFor(yearStart, today));
        var avgSpend = new PeriodKpis(SpendFor(weekMon, today), SpendFor(monS, today), SpendFor(yearStart, today));

        return new OverviewData(weeks, monthStart, oeTarget, oe, avgSpend, mtd, today);
    }
}
