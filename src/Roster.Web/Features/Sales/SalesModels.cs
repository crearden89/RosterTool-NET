// Port of the props src/app/(app)/plan/sales/page.tsx hands SalesClient.tsx, and of
// SalesClient.tsx's own figure helpers (money0, moneyK, hours2, adjustedFor,
// adjustmentPill) and its per-day rows.

using System.Globalization;
using Roster.Engine.Core;

namespace Roster.Web.Features.Sales;

/// <summary>A store in the Sales page's store picker.</summary>
public sealed record SalesStore(Guid Id, string Code, string Name);

/// <summary>A day's sales forecast from benchmark_days.</summary>
public sealed record SalesForecastDay(DateOnly Date, double SalesForecast);

/// <summary>A day's sales_overrides row (the client's <c>Override</c>).</summary>
public sealed record SalesOverrideRow(
    DateOnly Date,
    bool Closed,
    string? Method,
    double? Pct,
    double? Value,
    string? Reason,
    string? Notes) : IAdjustment;

/// <summary>The same weekday's actual sales averaged over the 12 weeks before (null: none known).</summary>
public sealed record AvgSalesDay(DateOnly Date, double? Avg);

/// <summary>The fortnight's other planning inputs, in brief (the client's <c>SalesSummary</c>).</summary>
/// <param name="Benchmark">The held plan's benchmark hours; null hours until the fortnight's plan has been generated.</param>
public sealed record SalesSummary(
    NonOpBrief NonOp,
    IReadOnlyList<LeaveBrief> Leave,
    WeekendsBrief Weekends,
    BenchmarkBrief Benchmark);

/// <param name="Categories">Category names, the largest share first.</param>
public sealed record NonOpBrief(double Hours, IReadOnlyList<string> Categories);

public sealed record LeaveBrief(string Name, DateOnly Start, DateOnly End);

/// <param name="Switches">Weekend-off switches set (a Saturday and a Sunday count one each).</param>
/// <param name="People">Team members with at least one switch set.</param>
public sealed record WeekendsBrief(IReadOnlyList<DateOnly> Saturdays, int Switches, int People);

public sealed record BenchmarkBrief(double? Hours, DateTimeOffset? GeneratedAt);

/// <summary>Everything the Sales page shows (page.tsx's props for SalesClient).</summary>
/// <param name="StoreId">Null when the viewer has no store in scope.</param>
/// <param name="Stores">The store picker's stores; empty for a store login.</param>
public sealed record SalesPageData(
    Guid? StoreId,
    IReadOnlyList<SalesStore> Stores,
    DateOnly StartDate,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<SalesForecastDay> Benchmark,
    IReadOnlyList<SalesOverrideRow> Overrides,
    IReadOnlyList<AvgSalesDay> AvgSales,
    SalesSummary? Summary,
    bool ReadOnly);

/// <summary>saveOverride's input. Method is "pct", "value" or null (a closed day).</summary>
public sealed record SaveOverrideInput(
    Guid StoreId,
    DateOnly Date,
    bool Closed,
    string? Method,
    double? Pct,
    double? Value,
    string Note);

/// <summary>A Sales action's answer: ok, or the reason it was refused.</summary>
public sealed record SalesResult(bool Ok, string? Error = null)
{
    public static readonly SalesResult Success = new(true);
}

/// <summary>One day of the fortnight as the chart and the table read it (SalesClient's <c>days</c>).</summary>
/// <param name="Override">The day's adjustment, when it is one (an empty adjustment is none).</param>
/// <param name="Adjusted">The forecast after the adjustment; null when there is none.</param>
/// <param name="Shown">What the bar draws: the adjusted figure, else the forecast.</param>
/// <param name="Pill">"+5%", "−10%", "Closed", "Set", "±0%" or "Note"; null with no adjustment.</param>
public sealed record SalesDay(
    DateOnly Date,
    SalesOverrideRow? Override,
    double Forecast,
    double? Avg,
    double? Adjusted,
    double Shown,
    string? Pill,
    string? Note,
    bool Weekend);

public static class SalesFigures
{
    public static readonly IReadOnlyList<string> Dow = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>"$12,345": whole dollars with thousands separators.</summary>
    public static string Money0(double n) => "$" + Js.ToLocaleString(Js.Round(n));

    /// <summary>"$98k" on top of a bar.</summary>
    public static string MoneyK(double n) => n >= 1000 ? $"${Js.NumberToString(Js.Round(n / 1000))}k" : Money0(n);

    public static string Hours2(double n) => Js.ToFixed(n, 2);

    /// <summary>
    /// The effective forecast after applying an override, so the original figure
    /// stays visible alongside what it was adjusted to.
    /// </summary>
    public static double? AdjustedFor(double forecast, IAdjustment? o)
    {
        if (o is null) return null;
        if (o.Closed) return 0;
        if (o.Method == "pct" && o.Pct is { } pct) return forecast * (pct / 100);
        if (o.Method == "value" && o.Value is { } value) return value;
        return null;
    }

    /// <summary>
    /// The pill over an adjusted bar: "+5%", "−10%", "Closed", or "Note" for a
    /// day that only carries a note.
    /// </summary>
    public static string? AdjustmentPill(double forecast, IAdjustment? o)
    {
        if (o is null) return null;
        if (o.Closed) return "Closed";
        var adjusted = AdjustedFor(forecast, o);
        if (adjusted is null || forecast <= 0) return adjusted is { } a && a != forecast ? "Set" : "Note";
        var pct = Js.Round((adjusted.Value - forecast) / forecast * 100);
        if (pct == 0) return adjusted.Value == forecast ? "Note" : "±0%";
        return $"{(pct > 0 ? "+" : "−")}{Js.NumberToString(Math.Abs(pct))}%";
    }

    /// <summary>The fortnight's days as the chart and the table read them.</summary>
    public static List<SalesDay> Days(SalesPageData data)
    {
        var days = new List<SalesDay>(data.Dates.Count);
        foreach (var d in data.Dates)
        {
            var o = OverrideFor(data, d);
            var forecast = ForecastFor(data, d);
            var adjusted = AdjustedFor(forecast, o);
            var note = o is null ? null : !string.IsNullOrEmpty(o.Notes) ? o.Notes : !string.IsNullOrEmpty(o.Reason) ? o.Reason : null;
            days.Add(new SalesDay(
                Date: d,
                Override: o,
                Forecast: forecast,
                Avg: data.AvgSales.FirstOrDefault(a => a.Date == d)?.Avg,
                Adjusted: adjusted,
                Shown: adjusted ?? forecast,
                Pill: AdjustmentPill(forecast, o),
                Note: note,
                Weekend: d.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Saturday));
        }
        return days;
    }

    public static double ForecastFor(SalesPageData data, DateOnly d) =>
        data.Benchmark.FirstOrDefault(b => b.Date == d)?.SalesForecast ?? 0;

    /// <summary>The day's saved row, empty or not (the dialog gets it, so saving clears an empty one).</summary>
    public static SalesOverrideRow? SavedOverrideFor(SalesPageData data, DateOnly d) =>
        data.Overrides.FirstOrDefault(o => o.Date == d);

    /// <summary>An empty adjustment (100%, no reason) is no adjustment: no pill, nothing in the table.</summary>
    public static SalesOverrideRow? OverrideFor(SalesPageData data, DateOnly d) =>
        SavedOverrideFor(data, d) is { } o && !Adjustment.IsEmptyAdjustment(o) ? o : null;

    /// <summary>The fortnight's days in ISO weeks, each a section of the chart.</summary>
    public static List<(int Week, List<SalesDay> Days)> Weeks(IReadOnlyList<SalesDay> days)
    {
        var weeks = new List<(int Week, List<SalesDay> Days)>();
        foreach (var x in days)
        {
            var wk = ISOWeek.GetWeekOfYear(x.Date.ToDateTime(TimeOnly.MinValue));
            if (weeks.Count > 0 && weeks[^1].Week == wk) weeks[^1].Days.Add(x);
            else weeks.Add((wk, [x]));
        }
        return weeks;
    }

    /// <summary>Growth of the forecast over the 12-week average, in percent; null without an average.</summary>
    public static double? Growth(SalesDay x) => x.Avg is { } avg && avg > 0 ? (x.Forecast - avg) / avg * 100 : null;
}
