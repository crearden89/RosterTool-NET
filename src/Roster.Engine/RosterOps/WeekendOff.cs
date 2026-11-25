// Port of src/lib/roster/weekendOff.ts.
// ─── Weekend-off markers, for the roster page ────────────────────────────────
// Pure (roster page + tests). A weekend marker (Setup -> Weekends) takes a
// staff member's Saturday and/or Sunday off: the roster fill treats them as
// unavailable that day (Rostering/Fortnight). The roster page shows them with
// black diagonal lines, so a manager can see who is off by the marker
// instead of wondering why they are not rostered.

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.RosterOps;

public sealed record WeekendMarker(
    [property: JsonPropertyName("employee_id")] string EmployeeId,
    [property: JsonPropertyName("weekend_start")] DateOnly WeekendStart, // the weekend's Saturday
    [property: JsonPropertyName("sat_off")] bool SatOff,
    [property: JsonPropertyName("sun_off")] bool SunOff);

public static class WeekendOff
{
    /// <summary>The Saturday that starts <paramref name="date"/>'s weekend, or null on a weekday.</summary>
    public static DateOnly? WeekendSaturday(DateOnly date)
    {
        var dow = Period.DowMon0(date);
        if (dow == 5) return date;
        if (dow == 6) return Period.AddDaysIso(date, -1);
        return null;
    }

    /// <summary>
    /// Staff whose weekend marker takes <paramref name="date"/> off: a Saturday by SatOff, a
    /// Sunday by SunOff - the rule the roster fill applies.
    /// </summary>
    public static JsSet<string> MarkedOffOn(DateOnly date, IEnumerable<WeekendMarker> markers)
    {
        var saturday = WeekendSaturday(date);
        if (saturday is null) return new JsSet<string>();
        var sunday = Period.DowMon0(date) == 6;
        return new JsSet<string>(markers
            .Where(m => m.WeekendStart == saturday && (sunday ? m.SunOff : m.SatOff))
            .Select(m => m.EmployeeId));
    }
}
