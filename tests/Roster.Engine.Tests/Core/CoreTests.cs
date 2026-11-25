// Ports of src/lib/grid.test.ts, period.test.ts, fixedCycle.test.ts,
// positions.test.ts and weekendWindow.test.ts.

using Roster.Engine.Core;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.Core;

public class GridTests
{
    [Fact]
    public void MinToTime_prints_whole_minutes_as_before()
    {
        Assert.Equal("00:00", Grid.MinToTime(0));
        Assert.Equal("06:30", Grid.MinToTime(6 * 60 + 30));
        Assert.Equal("21:45", Grid.MinToTime(21 * 60 + 45));
        Assert.Equal("24:00", Grid.MinToTime(24 * 60));
    }

    [Fact]
    public void MinToTime_rounds_decimal_minutes_to_the_nearest_minute_before_printing()
    {
        // A new starter's pallets are priced from their band's decimal minutes.
        Assert.Equal("09:05", Grid.MinToTime(544.800000000000068));
        Assert.Equal("08:52", Grid.MinToTime(532.19999999999993));
        Assert.Equal("01:00", Grid.MinToTime(59.6));
        Assert.Equal("07:00", Grid.MinToTime(7 * 60 + 0.4));
    }

    [Fact]
    public void MinToSqlTime_keeps_an_SQL_time_whole() => Assert.Equal("09:05:00", Grid.MinToSqlTime(544.8));

    [Fact]
    public void TimeToMin_reads_HH_MM_and_HH_MM_SS()
    {
        Assert.Equal(510, Grid.TimeToMin("08:30"));
        Assert.Equal(510, Grid.TimeToMin("08:30:00"));
    }
}

public class PeriodTests
{
    private static DateOnly D(string iso) => ParseIso(iso);

    [Fact]
    public void Anchors_index_0_at_the_national_origin()
    {
        Assert.Equal(Period.PeriodAnchor, Period.PeriodStartByIndex(0)); // 2026-07-20
        Assert.Equal(0, Period.PeriodIndexFor(Period.PeriodAnchor));
        Assert.True(Period.IsPeriodStart(Period.PeriodAnchor));
    }

    [Fact]
    public void Keeps_a_date_within_its_own_14_day_period()
    {
        // 2026-07-20 .. 2026-08-02 is period 0.
        Assert.Equal(D("2026-07-20"), Period.PeriodStartFor(D("2026-07-20")));
        Assert.Equal(D("2026-07-20"), Period.PeriodStartFor(D("2026-07-27")));
        Assert.Equal(D("2026-07-20"), Period.PeriodStartFor(D("2026-08-02"))); // last day of period 0
        Assert.Equal(D("2026-08-03"), Period.PeriodStartFor(D("2026-08-03"))); // first day of period 1
    }

    [Fact]
    public void Derives_the_period_end_and_full_date_list()
    {
        Assert.Equal(D("2026-08-02"), Period.PeriodEndFrom(D("2026-07-20")));
        var dates = Period.PeriodDatesFrom(D("2026-07-20"));
        Assert.Equal(14, dates.Count);
        Assert.Equal(D("2026-07-20"), dates[0]);
        Assert.Equal(D("2026-08-02"), dates[13]);
    }

    [Fact]
    public void Handles_dates_before_the_anchor_with_floor_division()
    {
        Assert.Equal(-1, Period.PeriodIndexFor(D("2026-07-19"))); // one day before anchor
        Assert.Equal(D("2026-07-06"), Period.PeriodStartFor(D("2026-07-19")));
        Assert.Equal(D("2026-07-19"), Period.PeriodEndFrom(D("2026-07-06")));
    }

    [Fact]
    public void Crosses_a_month_boundary_without_drifting()
    {
        // Every period start must land exactly 14 days apart, no off-by-one.
        var prev = Period.PeriodStartByIndex(0);
        for (var i = 1; i <= 30; i++)
        {
            var cur = Period.PeriodStartByIndex(i);
            Assert.Equal(14, cur.DayNumber - prev.DayNumber);
            prev = cur;
        }
    }

    [Fact]
    public void AddPeriods_steps_by_whole_fortnights()
    {
        Assert.Equal(D("2026-08-03"), Period.AddPeriods(D("2026-07-20"), 1));
        Assert.Equal(D("2026-07-06"), Period.AddPeriods(D("2026-07-20"), -1));
    }

    [Fact]
    public void PlanningPeriodStart_targets_the_period_after_todays()
    {
        // Interview: on 21/07 stores plan 03/08-16/08.
        Assert.Equal(D("2026-08-03"), Period.PlanningPeriodStart(D("2026-07-21")));
        Assert.Equal(D("2026-07-20"), Period.CurrentPeriodStart(D("2026-07-21")));
        // Anywhere in a period yields the same next period.
        Assert.Equal(D("2026-08-03"), Period.PlanningPeriodStart(D("2026-08-02")));
        Assert.Equal(D("2026-08-03"), Period.PlanningPeriodStart(D("2026-07-20")));
    }

    [Fact]
    public void Formats_a_dd_mm_label() => Assert.Equal("03/08 – 16/08", Period.PeriodLabel(D("2026-08-03")));

    [Fact]
    public void DowMon0_counts_from_Monday()
    {
        Assert.Equal(0, Period.DowMon0(D("2026-11-09"))); // a Monday
        Assert.Equal(6, Period.DowMon0(D("2026-11-15")));
        Assert.Equal(3, Period.DowMon0(D("1970-01-01"))); // a Thursday
    }
}

public class FixedCycleTests
{
    private static DateOnly D(string iso) => ParseIso(iso);

    [Fact]
    public void MondayOf_maps_every_day_of_a_week_to_its_Monday()
    {
        // 2026-08-03 is a Monday.
        for (var i = 0; i < 7; i++) Assert.Equal(D("2026-08-03"), FixedCycle.MondayOf(D("2026-08-03").AddDays(i)));
        Assert.Equal(D("2026-08-10"), FixedCycle.MondayOf(D("2026-08-10")));
    }

    [Fact]
    public void Is_always_0_for_a_1_week_cycle() => Assert.Equal(0, FixedCycle.FixedWeekIndex(1, D("2026-08-03"), D("2026-09-30")));

    [Fact]
    public void Alternates_weekly_for_a_2_week_cycle()
    {
        var start = D("2026-08-03"); // Monday, Week 1
        Assert.Equal(0, FixedCycle.FixedWeekIndex(2, start, D("2026-08-05"))); // same week
        Assert.Equal(0, FixedCycle.FixedWeekIndex(2, start, D("2026-08-09"))); // Sunday of week 1
        Assert.Equal(1, FixedCycle.FixedWeekIndex(2, start, D("2026-08-10"))); // next Monday
        Assert.Equal(0, FixedCycle.FixedWeekIndex(2, start, D("2026-08-17"))); // cycle restarts
    }

    [Fact]
    public void Runs_0_to_3_for_a_4_week_cycle()
    {
        var start = D("2026-08-03");
        Assert.Equal(0, FixedCycle.FixedWeekIndex(4, start, D("2026-08-03")));
        Assert.Equal(1, FixedCycle.FixedWeekIndex(4, start, D("2026-08-12")));
        Assert.Equal(2, FixedCycle.FixedWeekIndex(4, start, D("2026-08-19")));
        Assert.Equal(3, FixedCycle.FixedWeekIndex(4, start, D("2026-08-28")));
        Assert.Equal(0, FixedCycle.FixedWeekIndex(4, start, D("2026-08-31")));
    }

    [Fact]
    public void Anchors_on_the_Monday_of_the_start_date_whatever_day_was_entered() =>
        // A Thursday start behaves identically to its Monday.
        Assert.Equal(FixedCycle.FixedWeekIndex(2, D("2026-08-03"), D("2026-08-10")), FixedCycle.FixedWeekIndex(2, D("2026-08-06"), D("2026-08-10")));

    [Fact]
    public void Resolves_dates_before_the_start_date_by_extending_the_cycle_backwards()
    {
        var start = D("2026-08-17"); // Week 1
        Assert.Equal(1, FixedCycle.FixedWeekIndex(2, start, D("2026-08-10"))); // one week earlier
        Assert.Equal(0, FixedCycle.FixedWeekIndex(2, start, D("2026-08-03"))); // two weeks earlier
        Assert.Equal(3, FixedCycle.FixedWeekIndex(4, start, D("2026-08-10")));
    }

    [Fact]
    public void Treats_employees_without_a_cycle_row_as_a_1_week_cycle()
    {
        var resolve = FixedCycle.MakeCycleResolver([new CycleRow("a", 2, D("2026-08-03"))]);
        Assert.Equal(1, resolve("a", D("2026-08-10")));
        Assert.Equal(0, resolve("b", D("2026-08-10"))); // no row - legacy behaviour
    }
}

public class PositionsTests
{
    [Fact]
    public void Covers_every_requested_position_exactly_once()
    {
        var expected = new List<string> { "JSA-16", "JSA-17", "SA-FF", "SA-LR", "DSM", "RSM" };
        foreach (var h in Positions.AsmHourBands) foreach (var y in Roles.AsmYearBands) expected.Add($"ASM-{h}-{y}");
        foreach (var h in Positions.SmHourBands) foreach (var y in Roles.SmYearBands) expected.Add($"SM-{h}-{y}");
        Assert.Equal(expected.Order(StringComparer.Ordinal), Positions.All.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.Equal(31, Positions.All.Count); // 30 rate-bearing + RSM
        Assert.Equal(Positions.All.Count, Positions.All.Select(p => p.Code).Distinct().Count());
    }

    [Fact]
    public void Gives_every_position_a_rate_column_except_RSM()
    {
        Assert.Equal(30, Positions.RatePositions.Count);
        Assert.DoesNotContain(Positions.RatePositions, p => p.Code == "RSM");
    }

    [Fact]
    public void Carries_the_contract_size_and_tenure_year_on_manager_positions()
    {
        var p = Positions.ByCode["SM-45-3"];
        Assert.Equal("SM", p.BaseRole);
        Assert.Equal(45, p.WeeklyHours);
        Assert.Equal(3, p.YearBand);
        Assert.Equal("SM 45 hour year 3", p.Label);
        var asm = Positions.ByCode["ASM-32-2"];
        Assert.Equal("ASM", asm.BaseRole);
        Assert.Equal(32, asm.WeeklyHours);
        Assert.Equal(2, asm.YearBand);
    }

    [Fact]
    public void Leaves_contract_size_open_for_the_non_manager_positions()
    {
        foreach (var code in new[] { "JSA-16", "JSA-17", "SA-FF", "SA-LR", "DSM", "RSM" }) Assert.Null(Positions.ByCode[code].WeeklyHours);
    }

    [Fact]
    public void Converts_a_weekly_position_size_to_fortnightly_contract_hours()
    {
        Assert.Equal(70, Positions.FortnightHoursFor(35));
        Assert.Equal(100, Positions.FortnightHoursFor(50));
        Assert.Null(Positions.FortnightHoursFor(null));
    }

    [Fact]
    public void PayKeyFor_uses_the_employees_position_when_set()
    {
        Assert.Equal("SM-50-7", Positions.PayKeyFor("SM-50-7", "SM", 7));
        Assert.Equal("SA-LR", Positions.PayKeyFor("SA-LR", "SA", null));
        Assert.Equal("JSA-17", Positions.PayKeyFor("JSA-17", "JSA", null));
    }

    [Fact]
    public void PayKeyFor_prices_RSM_at_the_Store_Assistant_rate_whatever_its_position_says()
    {
        Assert.Equal(Positions.RsmRatePosition, Positions.PayKeyFor("RSM", "RSM", null));
        Assert.Equal(Positions.RsmRatePosition, Positions.PayKeyFor(null, "RSM", null));
        // RSM owns no rate column, so the key it resolves to must have one.
        Assert.Contains(Positions.RatePositions, p => p.Code == Positions.RsmRatePosition);
    }

    [Fact]
    public void PayKeyFor_falls_back_to_role_and_tenure_band_when_no_position_is_set()
    {
        Assert.Equal("SM-45-3", Positions.PayKeyFor(null, "SM", 3));
        Assert.Equal("ASM-40-2", Positions.PayKeyFor(null, "ASM", 2));
        Assert.Equal("SA-FF", Positions.PayKeyFor(null, "SA", null));
        Assert.Equal("JSA-16", Positions.PayKeyFor(null, "JSA", null));
        Assert.Equal("DSM", Positions.PayKeyFor(null, "DSM", null));
    }

    [Fact]
    public void Defaults_a_banded_role_with_no_band_to_year_1()
    {
        Assert.Equal("SM-45-1", Positions.PayRoleKeyFor("SM", null));
        Assert.Equal("ASM-40-1", Positions.PayRoleKeyFor("ASM", null));
    }

    [Fact]
    public void Every_fallback_and_default_key_is_a_real_rate_column()
    {
        var codes = Positions.RatePositions.Select(p => p.Code).ToList();
        foreach (var role in new[] { "SM", "ASM", "DSM", "SA", "JSA" })
        {
            foreach (var band in new double?[] { null, 1, 2, 3, 4, 7 })
            {
                var key = Positions.PayRoleKeyFor(role, band);
                if (role == "SM" && !new double[] { 1, 2, 3, 4, 7 }.Contains(band ?? 1)) continue;
                if (role == "ASM" && !new double[] { 1, 2 }.Contains(band ?? 1)) continue;
                Assert.Contains(key, codes);
            }
            Assert.Contains(Positions.DefaultPositionFor(role), codes);
        }
    }

    [Fact]
    public void FormatStoreId_normalises_to_G_and_three_digits()
    {
        Assert.Equal("G025", StoreIds.FormatStoreId(25));
        Assert.Equal("G025", StoreIds.FormatStoreId("025"));
        Assert.Equal("G025", StoreIds.FormatStoreId("g025"));
        Assert.Equal("G025", StoreIds.FormatStoreId("25.0"));
        Assert.Equal("", StoreIds.FormatStoreId("  "));
        Assert.Equal("ABC", StoreIds.FormatStoreId("abc"));
    }
}

public class WeekendWindowTests
{
    private static List<DateOnly> WindowSaturdays(DateOnly periodStart) => WeekendWindow.WindowSaturdays(periodStart);

    [Fact]
    public void Makes_the_editable_pair_exactly_the_rostered_fortnights_two_Saturdays()
    {
        var editable = WindowSaturdays(ParseIso("2026-08-03")).Skip(2).ToList();
        Assert.Equal([ParseIso("2026-08-08"), ParseIso("2026-08-15")], editable);
    }

    [Fact]
    public void Shows_the_previous_fortnights_Saturdays_as_the_trailing_context()
    {
        Assert.Equal(
            [ParseIso("2026-07-25"), ParseIso("2026-08-01"), ParseIso("2026-08-08"), ParseIso("2026-08-15")],
            WindowSaturdays(ParseIso("2026-08-03")));
    }

    [Fact]
    public void Every_Saturday_in_the_window_belongs_to_the_fortnight_it_is_shown_under()
    {
        for (var p = -3; p <= 3; p++)
        {
            var start = Period.AddPeriods(ParseIso("2026-08-03"), p);
            var sats = WindowSaturdays(start);
            Assert.Equal(start, Period.PeriodStartFor(sats[2]));
            Assert.Equal(start, Period.PeriodStartFor(sats[3]));
        }
    }

    [Fact]
    public void Steps_a_whole_fortnight_at_a_time_so_no_weekend_is_skipped_or_repeated()
    {
        var a = WindowSaturdays(ParseIso("2026-08-03"));
        var b = WindowSaturdays(Period.AddPeriods(ParseIso("2026-08-03"), 1));
        // The next period's context pair is this period's editable pair.
        Assert.Equal(a.Skip(2), b.Take(2));
    }
}
