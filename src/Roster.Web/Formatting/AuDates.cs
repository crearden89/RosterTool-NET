using System.Globalization;

namespace Roster.Web.Formatting;

/// <summary>
/// Australian date labels, shared by every screen so dates read the same
/// everywhere (dd/mm, never mm/dd): the TypeScript app's <c>src/lib/dates.ts</c>.
/// Days are <see cref="DateOnly"/>, so no clock or time zone can shift them;
/// timestamps are shown in Sydney time.
/// </summary>
public static class AuDates
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>The office-facing pages' single national "today": Sydney's date.</summary>
    public static TimeZoneInfo Sydney { get; } = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    /// <summary>Today's date in Sydney (<c>todayAu</c>). Never the server's UTC date.</summary>
    public static DateOnly TodayAu(TimeProvider? clock = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), Sydney).DateTime);

    /// <summary>"28/11": a day where the year is implied.</summary>
    public static string AuShort(DateOnly d) => $"{d.Day:00}/{d.Month:00}";

    /// <summary>"28/11/2026": a day in a year-spanning record.</summary>
    public static string AuFull(DateOnly d) => $"{d.Day:00}/{d.Month:00}/{d.Year}";

    /// <summary>"28 Nov".</summary>
    public static string AuDayMonth(DateOnly d) => $"{d.Day} {Months[d.Month - 1]}";

    /// <summary>"Sat 28 Nov".</summary>
    public static string AuDayLabel(DateOnly d) => $"{Weekdays[(int)d.DayOfWeek]} {AuDayMonth(d)}";

    /// <summary>"23–27 Nov", "30 Nov – 2 Dec", or one day "28 Nov".</summary>
    public static string AuRange(DateOnly start, DateOnly end)
    {
        if (start == end) return AuDayMonth(start);
        if (start.Year == end.Year && start.Month == end.Month) return $"{start.Day}–{AuDayMonth(end)}";
        return $"{AuDayMonth(start)} – {AuDayMonth(end)}";
    }

    /// <summary>"30/07/2026, 1:06 pm (GMT+10)": a full timestamp in Sydney time with its offset.</summary>
    public static string AuDateTime(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Sydney);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var gmt = offset.Minutes == 0
            ? $"GMT{sign}{Math.Abs(offset.Hours)}"
            : $"GMT{sign}{Math.Abs(offset.Hours)}:{Math.Abs(offset.Minutes):00}";
        return $"{local.Day:00}/{local.Month:00}/{local.Year}, {Clock12(local)} ({gmt})";
    }

    /// <summary>"02/11, 9:14 am": a timestamp in Sydney time where the year is implied.</summary>
    public static string AuStamp(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Sydney);
        return $"{local.Day:00}/{local.Month:00}, {Clock12(local)}";
    }

    /// <summary>"9:14 am".</summary>
    public static string Clock12(DateTimeOffset local)
    {
        var h = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{h}:{local.Minute:00} {(local.Hour < 12 ? "am" : "pm")}";
    }

    /// <summary>An ISO day ("2026-11-28").</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Reads an ISO day; null when it is not one.</summary>
    public static DateOnly? ParseIso(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// The national fortnight grid the page headers step through: every store is on
/// the same 14-day cycle, anchored on Monday 20 July 2026 (the TypeScript app's
/// <c>src/lib/period.ts</c>).
/// </summary>
/// <remarks>
/// The shell needs only these few steps for its fortnight arrows. When the
/// engine's own period helpers land (src/Roster.Engine), this should call them
/// rather than keep its own anchor.
/// </remarks>
public static class Fortnight
{
    public static DateOnly Anchor { get; } = new(2026, 7, 20);

    public const int Days = 14;

    /// <summary>Which fortnight a day is in (negative before the anchor).</summary>
    public static int IndexFor(DateOnly day) => (int)Math.Floor((day.DayNumber - Anchor.DayNumber) / (double)Days);

    /// <summary>The Monday that starts the fortnight a day is in.</summary>
    public static DateOnly StartFor(DateOnly day) => Anchor.AddDays(IndexFor(day) * Days);

    /// <summary>The Sunday that ends the fortnight starting on <paramref name="start"/>.</summary>
    public static DateOnly EndFrom(DateOnly start) => start.AddDays(Days - 1);

    /// <summary>The fourteen days of the fortnight starting on <paramref name="start"/>.</summary>
    public static IReadOnlyList<DateOnly> DatesFrom(DateOnly start) => Enumerable.Range(0, Days).Select(start.AddDays).ToList();

    /// <summary>Moves a fortnight start by whole fortnights.</summary>
    public static DateOnly AddPeriods(DateOnly start, int n) => start.AddDays(n * Days);

    /// <summary>True when the day starts a fortnight.</summary>
    public static bool IsStart(DateOnly day) => StartFor(day) == day;

    /// <summary>The fortnight being planned: the one after the fortnight containing today.</summary>
    public static DateOnly PlanningStart(DateOnly today) => StartFor(today).AddDays(Days);

    /// <summary>The fortnight containing today.</summary>
    public static DateOnly CurrentStart(DateOnly today) => StartFor(today);

    /// <summary>Monday = 0 ... Sunday = 6, the roster's convention everywhere.</summary>
    public static int DowMon0(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;

    /// <summary>"03/08 – 16/08".</summary>
    public static string Label(DateOnly start) => $"{AuDates.AuShort(start)} – {AuDates.AuShort(EndFrom(start))}";
}
