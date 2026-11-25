// Port of the pure helpers of src/lib/review/server.ts (planKey, stamp,
// periodTitle, dueLabel and the en-AU date labels the review shows).

using Roster.Engine.Core;

namespace Roster.Data.Services.Review;

/// <summary>
/// The review's keys and labels. The labels are exactly what the TypeScript's
/// <c>toLocaleDateString("en-AU", ...)</c> prints, with its leading "Mon," made
/// "Mon": en-AU abbreviates a month next to a day as Jan, Feb, Mar, Apr, May,
/// June, July, Aug, Sept, Oct, Nov, Dec.
/// </summary>
public static class ReviewFormat
{
    // What a submission and an approval are recorded against: the held plan's
    // version, which every regenerate or edit of the plan bumps.
    public static string PlanKey(int? planVersion) => $"plan:{Js.NumberToString(planVersion ?? 0)}";

    private static readonly string[] ShortWeekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] LongWeekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] ShortMonths = ["Jan", "Feb", "Mar", "Apr", "May", "June", "July", "Aug", "Sept", "Oct", "Nov", "Dec"];
    private static readonly string[] LongMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>"Mon 16 Nov" (weekday short, day, month short).</summary>
    public static string WeekdayDayMonth(DateOnly d) => $"{ShortWeekdays[(int)d.DayOfWeek]} {d.Day} {ShortMonths[d.Month - 1]}";

    /// <summary>"19 Nov" (day, month short).</summary>
    public static string DayMonth(DateOnly d) => $"{d.Day} {ShortMonths[d.Month - 1]}";

    /// <summary>"Thursday 19 November" (weekday long, day, month long).</summary>
    public static string LongDay(DateOnly d) => $"{LongWeekdays[(int)d.DayOfWeek]} {d.Day} {LongMonths[d.Month - 1]}";

    private static readonly Lazy<TimeZoneInfo> Sydney = new(() => TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));

    /// <summary>A timestamp in the stores' time, e.g. "Tue 10 Nov, 9:42 am".</summary>
    public static string Stamp(DateTimeOffset ts)
    {
        var local = TimeZoneInfo.ConvertTime(ts, Sydney.Value);
        var h = local.Hour % 12;
        if (h == 0) h = 12;
        var clock = $"{h}:{local.Minute:00} {(local.Hour < 12 ? "am" : "pm")}";
        return $"{ShortWeekdays[(int)local.DayOfWeek]} {local.Day} {ShortMonths[local.Month - 1]}, {clock}";
    }

    /// <summary>"Mon 16 Nov – Sun 29 Nov 2026".</summary>
    public static string PeriodTitle(DateOnly start)
    {
        var end = Period.PeriodEndFrom(start);
        return $"{WeekdayDayMonth(start)} – {WeekdayDayMonth(end)} {end.ToIso()[..4]}";
    }

    // Rosters are due by Wednesday 6 pm of the week before the fortnight - the
    // same deadline the EMSO unapproved-roster check runs at.
    public static string DueLabel(DateOnly periodStart) => $"{WeekdayDayMonth(Period.AddDaysIso(periodStart, -5))}, 6:00 pm";
}
