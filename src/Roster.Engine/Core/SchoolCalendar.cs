// Port of the pure part of isSchoolDayFor and getSchoolCalendar
// (src/lib/refdata.ts); the reads belong to the data layer.

using System.Text.Json.Serialization;

namespace Roster.Engine.Core;

public sealed record SchoolTerm(
    [property: JsonPropertyName("start_date")] DateOnly StartDate,
    [property: JsonPropertyName("end_date")] DateOnly EndDate);

/// <summary>A state's school terms and its exception (pupil-free) days.</summary>
public sealed record SchoolCalendar
{
    public IReadOnlyList<SchoolTerm> Terms { get; init; } = [];
    public IReadOnlyList<DateOnly> ExceptionDates { get; init; } = [];

    public static readonly SchoolCalendar None = new();

    // School day = weekday within a school term for the store's state that isn't an
    // exception (pupil-free) day. Same rule everywhere Stage 2 sizes JSA windows.
    public bool IsSchoolDay(DateOnly date)
    {
        var dow = Period.DowMon0(date);
        if (dow == 5 || dow == 6) return false;
        var inTerm = Terms.Any(t => t.StartDate <= date && date <= t.EndDate);
        return inTerm && !ExceptionDates.Contains(date);
    }
}
