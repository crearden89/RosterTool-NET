// Port of the Analysis page's own text and colours in src/app/(app)/analysis/page.tsx.

using Roster.Engine.Analysis;
using Roster.Engine.Core;

namespace Roster.Web.Features.Analysis;

public static class AnalysisText
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static string DayMonth(DateOnly d) => $"{d.Day} {Months[d.Month - 1]}";

    /// <summary>"23 Nov – 6 Dec 2026"; a range across a new year names both years.</summary>
    public static string RangeLabel(IReadOnlyList<DateOnly> days)
    {
        if (days.Count == 0) return "";
        var (a, b) = (days[0], days[^1]);
        if (a == b) return $"{DayMonth(a)} {a.Year:0000}";
        return a.Year == b.Year
            ? $"{DayMonth(a)} – {DayMonth(b)} {b.Year:0000}"
            : $"{DayMonth(a)} {a.Year:0000} – {DayMonth(b)} {b.Year:0000}";
    }

    public static string GroupLabel(string key) => key switch
    {
        "checkout" => "Checkout",
        "no_work" => "Time with no work",
        _ => LabourGroups.ByKey.TryGetValue(key, out var g) ? g.Label : key,
    };

    /// <summary>A group's swatch colour; null for the time with no work (a dashed outline).</summary>
    public static string? GroupColour(string key) =>
        key == "checkout" ? LabourGroups.CheckoutColorLeft : LabourGroups.ByKey.TryGetValue(key, out var g) ? g.Color : null;

    /// <summary>
    /// Where the money goes: worked payroll in the app's navy, leave in amber (the colour leave
    /// wears across the app) and make-up pay in violet. The amber is the light one, so its share
    /// is written in ink.
    /// </summary>
    public static (string Colour, bool InkText) PartColour(MoneyPartKey key) => key switch
    {
        MoneyPartKey.Worked => ("var(--ui-ink)", false),
        MoneyPartKey.Leave => ("#e3a12b", true),
        _ => ("var(--ui-violet)", false),
    };

    public static string Pct0(double share) => Js.NumberToString(Js.Round(share * 100)) + "%";
}
