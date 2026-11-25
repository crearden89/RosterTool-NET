// Port of src/lib/period.ts.
// ─── Canonical fortnight-period calendar ─────────────────────────────────────
// The roster fortnight is a single national grid: every store, everywhere, is
// on the identical 14-day cycle. It is anchored to one known period start and
// every other period boundary is derived deterministically from it - so any
// date's period is knowable without consulting the database (roster_periods
// rows are still created lazily as FK targets, but they follow this grid, they
// don't define it).
//
// Pure module (no I/O) so it's unit-testable. Date math is done in epoch-day
// space to stay DST-proof and avoid the local-vs-UTC drift that bit the weekend
// date display earlier. In C# an ISO date is a DateOnly, which has no time zone
// to drift in at all.

namespace Roster.Engine.Core;

public static class Period
{
    public static readonly DateOnly PeriodAnchor = new(2026, 7, 20); // Monday - the national grid origin.
    public const int PeriodDays = 14;

    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    internal static int ToEpochDay(DateOnly d) => d.DayNumber - EpochDayNumber;

    internal static DateOnly FromEpochDay(int day) => DateOnly.FromDayNumber(day + EpochDayNumber);

    private static readonly int AnchorDay = ToEpochDay(PeriodAnchor);

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    // Zero-based index of the period containing `date` (can be negative for
    // dates before the anchor). Uses floor so it's correct on both sides.
    public static int PeriodIndexFor(DateOnly date) => FloorDiv(ToEpochDay(date) - AnchorDay, PeriodDays);

    // The period start (Monday) for a given index.
    public static DateOnly PeriodStartByIndex(int index) => FromEpochDay(AnchorDay + index * PeriodDays);

    // The period start (Monday) of the period that contains `date`.
    public static DateOnly PeriodStartFor(DateOnly date) => PeriodStartByIndex(PeriodIndexFor(date));

    // The last day (Sunday) of the period beginning at `start`.
    public static DateOnly PeriodEndFrom(DateOnly start) => FromEpochDay(ToEpochDay(start) + PeriodDays - 1);

    // All 14 dates of the period beginning at `start`.
    public static List<DateOnly> PeriodDatesFrom(DateOnly start)
    {
        var s = ToEpochDay(start);
        var out_ = new List<DateOnly>(PeriodDays);
        for (var i = 0; i < PeriodDays; i++) out_.Add(FromEpochDay(s + i));
        return out_;
    }

    // Move a period start forward (or back) by `n` whole periods.
    public static DateOnly AddPeriods(DateOnly start, int n) => FromEpochDay(ToEpochDay(start) + n * PeriodDays);

    // Move a single date forward (or back) by `n` whole days.
    public static DateOnly AddDaysIso(DateOnly date, int n) => FromEpochDay(ToEpochDay(date) + n);

    // Mon-first weekday labels, indexed by DowMon0 below. The ONE copy: these
    // were pasted into six client files, and the order is load-bearing - one
    // Sun-first reorder would shift every label by a day.
    public static readonly IReadOnlyList<string> DayLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    // Day-of-week with Monday=0 - the roster convention everywhere. The ONE copy:
    // this used to be pasted into ~16 files, which is exactly how a UTC-vs-local
    // variant sneaks into one of them. Epoch-day math (1970-01-01 was a Thursday,
    // so epoch day 0 is Mon0-dow 3), immune to server timezone and DST.
    public static int DowMon0(DateOnly date) => (((ToEpochDay(date) + 3) % 7) + 7) % 7;

    // True when `date` is itself a period boundary (a valid period start).
    public static bool IsPeriodStart(DateOnly date) => PeriodStartFor(date) == date;

    // The period stores should currently be planning: the period AFTER the one that
    // contains `today`. Rosters are prepared at least a fortnight ahead, so on
    // any day of the current period the target is the next period (e.g. today in
    // the 20/07-02/08 period → planning 03/08-16/08).
    public static DateOnly PlanningPeriodStart(DateOnly? today = null) =>
        PeriodStartByIndex(PeriodIndexFor(today ?? Dates.TodayAu()) + 1);

    // The period that contains today.
    public static DateOnly CurrentPeriodStart(DateOnly? today = null) => PeriodStartFor(today ?? Dates.TodayAu());

    // dd/mm – dd/mm label for a period, e.g. "03/08 – 16/08".
    public static string PeriodLabel(DateOnly start)
    {
        var end = PeriodEndFrom(start);
        static string Dm(DateOnly d) => $"{d.Day:00}/{d.Month:00}";
        return $"{Dm(start)} – {Dm(end)}";
    }
}

public static class DateOnlyExtensions
{
    /// <summary>The ISO form, "2026-11-09".</summary>
    public static string ToIso(this DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parse "2026-11-09".</summary>
    public static DateOnly ParseIso(string iso) =>
        DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
