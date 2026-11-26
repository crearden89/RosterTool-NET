// Port of the timestamp on src/app/(app)/notifications/page.tsx:
// new Date(n.created_at).toLocaleString("en-AU"), in Sydney time.

using System.Globalization;

namespace Roster.Web.Features.Notifications;

public static class NotificationFormat
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    /// <summary>
    /// "25/11/2026, 5:17:05 pm": en-AU's toLocaleString, in Sydney time. The TypeScript page
    /// gave no time zone, so its server's (UTC) applied; timestamps are never written in the
    /// host's zone (src/lib/dates.ts), so the owner had this one corrected.
    /// </summary>
    public static string LocaleString(DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        var t = TimeZoneInfo.ConvertTime(at, zone ?? Sydney);
        var h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return string.Create(CultureInfo.InvariantCulture,
            $"{t.Day:00}/{t.Month:00}/{t.Year:0000}, {h}:{t.Minute:00}:{t.Second:00} {(t.Hour < 12 ? "am" : "pm")}");
    }
}
