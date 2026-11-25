// Port of src/lib/payroll/dayType.ts (the holiday lookup is the caller's).

namespace Roster.Engine.Pay;

public static class DayTypes
{
    public const string Weekday = "weekday";
    public const string Saturday = "saturday";
    public const string Sunday = "sunday";
    public const string PublicHoliday = "public_holiday";

    // The pay-rate day-type for a store/date: public holiday (3-tier calendar)
    // takes precedence over the plain weekday/Saturday/Sunday split.
    public static string DayType(DateOnly date, bool publicHoliday)
    {
        if (publicHoliday) return PublicHoliday;
        return date.DayOfWeek switch
        {
            DayOfWeek.Saturday => Saturday,
            DayOfWeek.Sunday => Sunday,
            _ => Weekday,
        };
    }

    /// <summary><c>dayType(storeId, date)</c> over the store's holiday set.</summary>
    public static string DayType(DateOnly date, IEnumerable<DateOnly> holidays) => DayType(date, holidays.Contains(date));
}
