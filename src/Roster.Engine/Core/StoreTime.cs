// Port of src/lib/storeTime.ts.
// ─── Store-local clock ───────────────────────────────────────────────────────
// The kiosk surfaces (Floor Display, Load Planner) run on whatever device
// hangs in the store, but the server renders them - so "now" and "today" must
// be computed in the STORE's timezone, mapped from its state. Pure module.

namespace Roster.Engine.Core;

public static class StoreTime
{
    public static readonly IReadOnlyDictionary<string, string> StateTimeZones = new Dictionary<string, string>
    {
        ["NSW"] = "Australia/Sydney",
        ["ACT"] = "Australia/Sydney",
        ["VIC"] = "Australia/Melbourne",
        ["QLD"] = "Australia/Brisbane",
        ["SA"] = "Australia/Adelaide",
        ["WA"] = "Australia/Perth",
        ["TAS"] = "Australia/Hobart",
        ["NT"] = "Australia/Darwin",
    };

    /// <summary>The time zone a store in <paramref name="state"/> keeps (Sydney when unknown).</summary>
    public static TimeZoneInfo ZoneFor(string? state) =>
        TimeZoneInfo.FindSystemTimeZoneById(StateTimeZones.GetValueOrDefault(state ?? "", "Australia/Sydney"));

    // The store's current local date and time (minutes from midnight).
    public static (DateOnly Date, int Minutes) StoreNow(string? state, DateTimeOffset? at = null)
    {
        var local = TimeZoneInfo.ConvertTime(at ?? DateTimeOffset.UtcNow, ZoneFor(state));
        return (DateOnly.FromDateTime(local.DateTime), local.Hour * 60 + local.Minute);
    }
}
