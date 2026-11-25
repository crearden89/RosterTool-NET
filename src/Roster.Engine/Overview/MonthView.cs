// Port of src/lib/overview/monthView.ts.
// ─── The Dashboard's month figures and the month as bars ────────────────────
// Four cards (sales against forecast, efficiency against target, average spend
// and hours) and one bar per day of actual sales against forecast. The cards
// read the day table's MTD row and computeOverview's OE and Average Spend
// windows, so they agree with the table under them. Pure.

using Roster.Engine.Core;

namespace Roster.Engine.Overview;

public enum MonthCardKey { Sales, Oe, Spend, Hours }

public sealed record MonthCard(MonthCardKey Key, string Label, string Value, string Sub);

/// <param name="Actual">The actual's height as a fraction of the tallest value in the month, 0-1.</param>
/// <param name="Forecast">The forecast's height as a fraction of the tallest value in the month, 0-1.</param>
public sealed record DayBar(
    DateOnly Date,
    int Day,
    double Actual,
    double Forecast,
    double ActualSales,
    double ForecastSales,
    bool Weekend,
    bool Today);

public sealed record MonthTick(double At, string Label);

public static class MonthView
{
    private static string Pct1(double? n) => n is { } v ? Js.ToFixed(v, 1) + "%" : "-";
    private static string Spend(double? n) => n is { } v ? "$" + Js.ToFixed(v, 2) : "-";
    // One decimal, as the day table writes hours.
    private static string Hrs1(double n) => Js.Truthy(n) ? Js.ToFixed(n, 1) : "-";

    /// <summary>
    /// The days a month's cards add up: month to date (before today) for the current
    /// month, every day of a past month, none of a future one.
    /// </summary>
    private static Summary? Pooled(OverviewData data)
    {
        var days = data.Weeks.SelectMany(w => w.Days).Where(d => d.Date < data.Today).ToList();
        if (days.Count == 0) return null;
        double forecastSales = 0, actualSales = 0, actualCustomers = 0, kronosHours = 0, targetHours = 0, actualHours100 = 0, nonOpHours = 0;
        foreach (var d in days)
        {
            forecastSales += d.ForecastSales;
            actualSales += d.ActualSales;
            actualCustomers += d.ActualCustomers;
            kronosHours += d.KronosHours;
            targetHours += d.TargetHours;
            actualHours100 += d.ActualHours100;
            nonOpHours += d.NonOpHours;
        }
        return new Summary
        {
            ForecastSales = forecastSales,
            ActualSales = actualSales,
            ActualCustomers = actualCustomers,
            KronosHours = kronosHours,
            TargetHours = targetHours,
            ActualHours100 = actualHours100,
            NonOpHours = nonOpHours,
            OeResult = Metrics.OperationalEfficiencyPct(actualHours100, kronosHours, nonOpHours),
            Iplh = null,
            AvgSpend = Metrics.AverageSpend(actualSales, actualCustomers),
            HoursToTarget = null,
        };
    }

    /// <summary>
    /// The four cards. For the current month they read "month to date": the MTD row of
    /// the day table (month start to yesterday) for sales and hours, and
    /// computeOverview's OE and Average Spend month-to-date figures. For any other month
    /// they total that month's days so far.
    /// </summary>
    public static List<MonthCard> MonthCards(OverviewData data, string monthName)
    {
        var current = data.Today.Year == data.MonthStart.Year && data.Today.Month == data.MonthStart.Month;
        var s = current ? data.Mtd?.Summary : Pooled(data);
        var when = current ? "month to date" : monthName;
        var oe = current ? data.Oe.Mtd : s?.OeResult;
        var avg = current ? data.AvgSpend.Mtd : s?.AvgSpend;
        var salesGap = s is not null && s.ForecastSales > 0 && s.ActualSales > 0
            ? Js.ToFixed((s.ActualSales / s.ForecastSales - 1) * 100, 1)
            : null;
        return
        [
            new(
                MonthCardKey.Sales,
                $"Sales · {when}",
                s is not null && Js.Truthy(s.ActualSales) ? Money.Money0(s.ActualSales) : "-",
                salesGap is not null
                    ? $"{(Js.Number(salesGap) > 0 ? "+" : "")}{salesGap}% against forecast ({Money.Money0(s!.ForecastSales)})"
                    : s is not null && Js.Truthy(s.ForecastSales)
                        ? $"Forecast {Money.Money0(s.ForecastSales)}"
                        : "No sales yet"),
            new(
                MonthCardKey.Oe,
                $"Operational efficiency · {(current ? "MTD" : monthName)}",
                Pct1(oe),
                current
                    ? $"Target {Js.ToFixed(data.OeTarget, 0)}% · WTD {Pct1(data.Oe.Wtd)}"
                    : $"Target {Js.ToFixed(data.OeTarget, 0)}%"),
            new(
                MonthCardKey.Spend,
                $"Average spend · {(current ? "MTD" : monthName)}",
                Spend(avg),
                current
                    ? $"WTD {Spend(data.AvgSpend.Wtd)} · YTD {Spend(data.AvgSpend.Ytd)}"
                    : $"YTD {Spend(data.AvgSpend.Ytd)}"),
            new(
                MonthCardKey.Hours,
                $"Hours · {(current ? "MTD" : monthName)}",
                s is not null && Js.Truthy(s.KronosHours) ? $"{Hrs1(s.KronosHours)} h" : "-",
                s is not null && Js.Truthy(s.TargetHours) ? $"against {Hrs1(s.TargetHours)} h target" : "No hours yet"),
        ];
    }

    // ─── The month as bars ───────────────────────────────────────────────────────

    /// <summary>
    /// A tidy top for the scale: the month's largest value rounded up to 1, 2, 2.5 or 5
    /// times a power of ten, so the gridlines fall on round figures.
    /// </summary>
    public static double NiceCeiling(double max)
    {
        if (!(max > 0)) return 0;
        var p = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * p >= max) return m * p;
        return 10 * p;
    }

    public static (double Top, List<DayBar> Days) MonthBars(OverviewData data)
    {
        var days = data.Weeks.SelectMany(w => w.Days).ToList();
        var top = NiceCeiling(Js.Max(days.Select(d => Math.Max(d.ForecastSales, d.ActualSales)).Prepend(0)));
        double Frac(double v) => top > 0 && v > 0 ? Math.Min(1, v / top) : 0;
        return (top, days
            .Select(d => new DayBar(
                d.Date,
                d.Date.Day,
                Frac(d.ActualSales),
                Frac(d.ForecastSales),
                d.ActualSales,
                d.ForecastSales,
                d.IsWeekend,
                d.IsToday))
            .ToList());
    }

    /// <summary>"$100k", "$1.2m": the chart's gridline labels.</summary>
    public static string MoneyShort(double n)
    {
        if (n >= 1e6) return $"${StripPointZero(Js.ToFixed(n / 1e6, 1))}m";
        if (n >= 1e3) return $"${StripPointZero(Js.ToFixed(n / 1e3, Js.Truthy(n % 1e3) ? 1 : 0))}k";
        return $"${Js.NumberToString(Js.Round(n))}";
    }

    // .replace(/\.0$/, "")
    private static string StripPointZero(string s) => s.EndsWith(".0", StringComparison.Ordinal) ? s[..^2] : s;

    /// <summary>Gridlines at nothing, half the scale and the top.</summary>
    public static List<MonthTick> MonthTicks(double top) =>
        top > 0 ? new[] { 0, 0.5, 1 }.Select(at => new MonthTick(at, MoneyShort(top * at))).ToList() : [];
}
