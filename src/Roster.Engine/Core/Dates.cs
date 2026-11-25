// Port of src/lib/dates.ts.
// Australian date formatting helpers, shared across every screen so date
// display stays consistent (dd/mm, not the US mm/dd default).

using System.Globalization;

namespace Roster.Engine.Core;

public static class Dates
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    // Today's date IN AUSTRALIA. Never derive "today" from the UTC clock - that's
    // the UTC date, which on a UTC-hosted server is YESTERDAY from midnight until
    // ~10-11am Sydney time, exactly when managers do their morning checks.
    // Store-precise surfaces (kiosks) use StoreTime's state-mapped clock; this
    // Sydney-anchored default is for the office-facing pages where a single
    // national "today" is the convention.
    public static DateOnly TodayAu(DateTimeOffset? at = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at ?? DateTimeOffset.UtcNow, Sydney).DateTime);

    // dd/mm from an ISO date - used in fortnight/roster-period tables where the
    // year is implied by context (a store's current cycle).
    public static string AuShort(DateOnly d) => $"{d.Day:00}/{d.Month:00}";

    // dd/mm/yyyy - used for absolute, year-spanning records (public holidays,
    // school terms) where the year isn't implied.
    public static string AuFull(DateOnly d) => $"{d.Day:00}/{d.Month:00}/{d.Year:0000}";

    // Full timestamp in SYDNEY time with an explicit GMT offset, e.g.
    // "30/07/2026, 1:06 pm (GMT+10)". Timestamps must never be formatted in the
    // host's time zone (UTC on a server), which showed a 1:06 pm upload as 3:06 am.
    public static string AuDateTime(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, Sydney);
        var stamp = $"{local.Day:00}/{local.Month:00}/{local.Year:0000}, {Clock12(local)}";
        return $"{stamp} ({GmtOffset(local.Offset)})";
    }

    // ─── Short labels for the page design (docs/design.md) ──────────────────────

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    // "28 Nov" from 2026-11-28.
    public static string AuDayMonth(DateOnly d) => $"{d.Day} {Months[d.Month - 1]}";

    // "Sat 28 Nov" from 2026-11-28.
    public static string AuDayLabel(DateOnly d) => $"{Weekdays[(int)d.DayOfWeek]} {AuDayMonth(d)}";

    // "23–27 Nov", "30 Nov – 2 Dec", or one day "28 Nov".
    public static string AuRange(DateOnly start, DateOnly end)
    {
        if (start == end) return AuDayMonth(start);
        if (start.Year == end.Year && start.Month == end.Month) return $"{start.Day}–{AuDayMonth(end)}";
        return $"{AuDayMonth(start)} – {AuDayMonth(end)}";
    }

    // "02/11, 9:14 am" in Sydney time - a timestamp where the year is implied.
    public static string AuStamp(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, Sydney);
        return $"{local.Day:00}/{local.Month:00}, {Clock12(local)}";
    }

    // "1:06 pm": en-AU's 12-hour clock, hour without padding.
    private static string Clock12(DateTimeOffset t)
    {
        var h = t.Hour % 12;
        if (h == 0) h = 12;
        return $"{h}:{t.Minute:00} {(t.Hour < 12 ? "am" : "pm")}";
    }

    // "GMT+10", "GMT+10:30", "GMT" - Intl's shortOffset.
    private static string GmtOffset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "GMT";
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"GMT{sign}{abs.Hours.ToString(CultureInfo.InvariantCulture)}"
            : $"GMT{sign}{abs.Hours.ToString(CultureInfo.InvariantCulture)}:{abs.Minutes:00}";
    }
}
