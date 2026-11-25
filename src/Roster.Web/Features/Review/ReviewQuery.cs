// Port of the query handling in src/app/(app)/plan/review/page.tsx: which fortnight
// ?period= opens, the header's "23 Nov – 6 Dec", and the links between fortnights.

using System.Globalization;
using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Web.Features.Review;

public static partial class ReviewQuery
{
    // /^\d{4}-\d{2}-\d{2}$/ (JavaScript's \d is ASCII only and its $ is the very end).
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex IsoShape();

    private static readonly string[] ShortMonths = ["Jan", "Feb", "Mar", "Apr", "May", "June", "July", "Aug", "Sept", "Oct", "Nov", "Dec"];

    /// <summary>
    /// The fortnight stores are preparing now, unless a link or the arrows chose one:
    /// <c>period &amp;&amp; /^\d{4}-\d{2}-\d{2}$/.test(period) ? periodStartFor(period) : planningPeriodStart()</c>.
    /// </summary>
    /// <param name="today">Today in Australia (todayAu).</param>
    public static DateOnly PeriodStartFrom(string? period, DateOnly today)
    {
        if (!string.IsNullOrEmpty(period) && IsoShape().IsMatch(period) && JsDate(period) is { } date)
            return Period.PeriodStartFor(date);
        // A shape-valid date JavaScript can't read (month 13, day 32) throws in the
        // TypeScript page; here it opens the fortnight being planned (see DIFFS.md).
        return Period.PlanningPeriodStart(today);
    }

    /// <summary>
    /// Date.parse("yyyy-mm-ddT00:00:00Z") as V8 reads it: a month 1-12 and a day 1-31,
    /// a day past the month's end running on into the next ("2026-02-30" is 2 March).
    /// </summary>
    internal static DateOnly? JsDate(string iso)
    {
        var y = int.Parse(iso.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var m = int.Parse(iso.AsSpan(5, 2), CultureInfo.InvariantCulture);
        var d = int.Parse(iso.AsSpan(8, 2), CultureInfo.InvariantCulture);
        if (y < 1 || m is < 1 or > 12 || d is < 1 or > 31) return null;
        return new DateOnly(y, m, 1).AddDays(d - 1);
    }

    /// <summary>A link straight to one day (?day=): an ISO date, or nothing.</summary>
    public static DateOnly? DayFrom(string? day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"23 Nov – 6 Dec": the fortnight between the header's arrows.</summary>
    public static string ShortSpan(DateOnly start)
    {
        static string D(DateOnly d) => $"{d.Day} {ShortMonths[d.Month - 1]}";
        return $"{D(start)} – {D(Period.PeriodEndFrom(start))}";
    }

    /// <summary>The Review page for a fortnight, on the same store when there is one.</summary>
    public static string Href(Guid? storeId, DateOnly periodStart) =>
        storeId is { } s ? $"/plan/review?store={s}&period={Iso(periodStart)}" : $"/plan/review?period={Iso(periodStart)}";

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
