// Port of the timestamp on src/app/(app)/notifications/page.tsx:
// new Date(n.created_at).toLocaleString("en-AU").

using System.Globalization;

namespace Roster.Web.Features.Notifications;

public static class NotificationFormat
{
    /// <summary>
    /// "25/11/2026, 6:17:05 am": en-AU's toLocaleString. The TypeScript page renders on the
    /// server with no time zone given, so the server's own zone applies; so it does here.
    /// </summary>
    public static string LocaleString(DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        var t = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
        var h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return string.Create(CultureInfo.InvariantCulture,
            $"{t.Day:00}/{t.Month:00}/{t.Year:0000}, {h}:{t.Minute:00}:{t.Second:00} {(t.Hour < 12 ? "am" : "pm")}");
    }
}
