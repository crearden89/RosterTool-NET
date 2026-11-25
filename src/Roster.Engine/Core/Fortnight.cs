// Port of src/lib/fortnight.ts.
// A roster period is a 14-day fortnight cycle. In Phase 1 the period is derived
// from an uploaded file's own date range: the earliest date is the start, and a
// valid benchmark/quarter-hour file must span exactly 14 consecutive days.
// (Nothing here hardcodes a weekday, so this is safe to revisit.)

namespace Roster.Engine.Core;

public sealed record DerivedPeriod(DateOnly StartDate, DateOnly EndDate);

public static class Fortnight
{
    // Given a set of dates, return the min/max as a period. Does not enforce the
    // 14-day span (callers that need that check via ValidateFortnightSpan).
    public static DerivedPeriod? DerivePeriod(IReadOnlyCollection<DateOnly> dates)
    {
        if (dates.Count == 0) return null;
        var sorted = dates.Order().ToList();
        return new DerivedPeriod(sorted[0], sorted[^1]);
    }

    // Validate that a set of dates forms a contiguous 14-day fortnight. Returns an
    // error string, or null if valid.
    public static string? ValidateFortnightSpan(IEnumerable<DateOnly> dates)
    {
        var unique = dates.Distinct().Order().ToList();
        if (unique.Count == 0) return "No dates found in file";
        var start = unique[0];
        var end = unique[^1];
        var span = end.DayNumber - start.DayNumber;
        if (span != 13)
            return $"Expected a 14-day fortnight; file spans {span + 1} days ({start.ToIso()} to {end.ToIso()})";
        // Ensure every one of the 14 days is present at least once.
        var present = new HashSet<DateOnly>(unique);
        for (var i = 0; i < 14; i++)
        {
            var d = start.AddDays(i);
            if (!present.Contains(d)) return $"Missing data for date {d.ToIso()} within the fortnight";
        }
        return null;
    }
}
