// Port of the Dashboard's own formatting: monthLabel and shiftMonth in
// src/app/(app)/overview/page.tsx, longDate and the month chart's day lines in Dashboard.tsx,
// and the day table's cell formats in DayTable.tsx.

using System.Globalization;
using Roster.Engine.Core;
using Roster.Engine.Overview;

namespace Roster.Web.Features.Overview;

public static class DashboardText
{
    private static readonly string[] LongDay = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] LongMonth = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>An ISO date ("2026-11-01"), or null when it isn't one.</summary>
    public static DateOnly? ParseIsoDate(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"November 2026" (en-AU, month long, year numeric).</summary>
    public static string MonthLabel(DateOnly d) => $"{LongMonth[d.Month - 1]} {d.Year}";

    /// <summary>The first of the month <paramref name="delta"/> months away.</summary>
    public static DateOnly ShiftMonth(DateOnly d, int delta) => new DateOnly(d.Year, d.Month, 1).AddMonths(delta);

    /// <summary>"Sunday 15 November"</summary>
    public static string LongDate(DateOnly d) => $"{LongDay[(int)d.DayOfWeek]} {d.Day} {LongMonth[d.Month - 1]}";

    /// <summary>The month chart's lines for one day: the screen-reader label and the tooltip.</summary>
    public static List<string> BarLines(DayBar d)
    {
        var name = FortnightCard.DayLabel(d.Date);
        var gap = d.ActualSales > 0 && d.ForecastSales > 0 ? Js.ToFixed((d.ActualSales / d.ForecastSales - 1) * 100, 1) : null;
        var lines = new List<string>
        {
            name + (d.Today ? " · today" : ""),
            d.ActualSales > 0 ? $"Actual {Money.Money0(d.ActualSales)}" : "Not traded yet",
            $"Forecast {(d.ForecastSales > 0 ? Money.Money0(d.ForecastSales) : "-")}",
        };
        if (gap is not null) lines.Add($"{(Js.Number(gap) > 0 ? "+" : "")}{gap}% against forecast");
        return lines;
    }

    // ── the day table's cells ──

    public static string Money0OrDash(double n) => Js.Truthy(n) ? Money.Money0(n) : "-";

    public static string Oe(double? n) => n is { } v ? Js.ToFixed(v, 1) + "%" : "-";

    public static string Iplh(double? n) => n is { } v ? Js.ToFixed(v, 0) : "-";

    public static string Hrs(double n) => Js.Truthy(n) ? Js.ToFixed(n, 1) : "-";

    /// <summary>Customer counts: whole numbers with separators.</summary>
    public static string Customers(double n) => Js.Truthy(n) ? Money.Count0(n) : "-";

    public static string AvgSpend(double? n) => n is { } v ? "$" + Js.ToFixed(v, 2) : "-";

    /// <summary>
    /// Hours to OE Target is signed on purpose: POSITIVE means the day spent that many hours
    /// more than the target allows (red), NEGATIVE means it had hours to spare (green).
    /// </summary>
    public static (string Text, string Tone) HoursToTarget(double? v) =>
        v is not { } h
            ? ("-", "")
            : ((h > 0 ? "+" : "") + Js.ToFixed(h, 1), h > 0.05 ? "text-[var(--brand-red)]" : h < -0.05 ? "text-green-700" : "");

    /// <summary>"OE month to date" in Your stores: one decimal and a percent, or "-".</summary>
    public static string OeMtd(double? n) => n is { } v ? Js.ToFixed(v, 1) + "%" : "-";
}
