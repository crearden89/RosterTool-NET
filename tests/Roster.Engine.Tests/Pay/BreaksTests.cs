// Ports of src/lib/payroll/breaks.test.ts and rateGrid.test.ts.

using Roster.Engine.Pay;

namespace Roster.Engine.Tests.Pay;

public class BreaksTests
{
    [Fact]
    public void Managers_get_a_flat_30_min_unpaid_break_at_any_length()
    {
        foreach (var role in new[] { "SM", "ASM", "DSM" })
        {
            Assert.Equal("flat_30_unpaid", Breaks.BreakTypeForRole(role));
            foreach (var h in new double[] { 3, 6, 9, 12 }) Assert.Equal(new BreakResult(30, 30, 1), Breaks.BreakForRole(role, h));
        }
    }

    [Fact]
    public void RSM_shares_the_tiered_break_structure_not_the_manager_one()
    {
        Assert.Equal("tiered", Breaks.BreakTypeForRole("RSM"));
        Assert.Equal(new BreakResult(30, 10, 1), Breaks.BreakForRole("RSM", 5));
    }

    [Fact]
    public void Tiered_up_to_4h_is_15_min_fully_paid()
    {
        Assert.Equal(new BreakResult(15, 0, 1), Breaks.BreakForShift("tiered", 3));
        Assert.Equal(new BreakResult(15, 0, 1), Breaks.BreakForShift("tiered", 4));
    }

    [Fact]
    public void Tiered_over_4h_to_6h_is_30_min_with_10_unpaid()
    {
        Assert.Equal(new BreakResult(30, 10, 1), Breaks.BreakForShift("tiered", 4.25));
        Assert.Equal(new BreakResult(30, 10, 1), Breaks.BreakForShift("tiered", 6));
    }

    [Fact]
    public void Tiered_over_6h_is_two_30_min_breaks()
    {
        Assert.Equal(new BreakResult(60, 30, 2), Breaks.BreakForShift("tiered", 6.25));
        Assert.Equal(new BreakResult(60, 30, 2), Breaks.BreakForShift("tiered", 9));
    }

    [Fact]
    public void Never_awards_a_third_break_however_long_the_shift()
    {
        Assert.Equal(60, Breaks.BreakForShift("tiered", 11).TotalMinutes);
        Assert.Equal(2, Breaks.BreakForShift("tiered", 11).Segments);
    }

    [Fact]
    public void Max_clock_and_work_follow_the_paid_cap()
    {
        Assert.Equal(570, Breaks.MaxClockMinutes("tiered", 9)); // 9 paid + 30 unpaid
        Assert.Equal(510, Breaks.MaxWorkMinutes("tiered", 9));
        Assert.Equal(750, Breaks.MaxClockMinutes("flat_30_unpaid", 12));
    }

    // Shift 8:00-14:00 unless stated.
    private const double S = 8 * 60;
    private const double E = 14 * 60;

    [Fact]
    public void BreakTileError_accepts_the_entitlement_in_the_entitled_shape_at_a_legal_time()
    {
        Assert.Null(Breaks.BreakTileError([600, 615], 30, S, E));
        Assert.Null(Breaks.BreakTileError([600], 15, S, E));
        // 60-min entitlement as two SEPARATE 30-min runs (8:00-17:30 shift).
        Assert.Null(Breaks.BreakTileError([600, 615, 780, 795], 60, S, 17.5 * 60));
    }

    [Fact]
    public void BreakTileError_rejects_too_little_or_too_much_break()
    {
        Assert.Matches("15m of break tiles but is entitled to 30m", Breaks.BreakTileError([600], 30, S, E));
        Assert.Matches("45m of break tiles", Breaks.BreakTileError([600, 615, 630], 30, S, E));
        Assert.Matches("0m of break tiles", Breaks.BreakTileError([], 30, S, E));
    }

    [Fact]
    public void BreakTileError_rejects_the_right_minutes_in_the_wrong_shape()
    {
        // 60 minutes as one solid hour is NOT two 30-min segments.
        Assert.Matches("separate blocks", Breaks.BreakTileError([600, 615, 630, 645], 60, S, 17.5 * 60));
        // 60 minutes as 15 + 45 isn't either.
        Assert.Matches("separate blocks", Breaks.BreakTileError([600, 700, 715, 730], 60, S, 17.5 * 60));
    }

    [Fact]
    public void BreakTileError_rejects_a_break_inside_the_first_hour() =>
        Assert.Matches("first hour", Breaks.BreakTileError([8 * 60 + 15, 8 * 60 + 30], 30, S, E));

    [Fact]
    public void BreakTileError_rejects_a_break_without_an_hour_of_tasks_after_it() =>
        // 12:45-13:15 break on a 14:00 finish leaves only 45min of tasks.
        Assert.Matches("1 hour of tasks after the last break", Breaks.BreakTileError([765, 780], 30, S, E));

    [Fact]
    public void BreakTileError_rejects_a_meal_starting_after_the_5th_hour() =>
        // 13:30 start on an 8:00 shift is 5.5h in (8:00-17:00 shift, not trailing).
        Assert.Matches("5th hour", Breaks.BreakTileError([810, 825], 30, S, 17 * 60));

    [Fact]
    public void BreakTileError_ignores_the_order_of_tile_clicks() =>
        Assert.Null(Breaks.BreakTileError([795, 600, 780, 615], 60, S, 17.5 * 60));

    // B3: two 30-minute breaks at least an hour apart, one in each half. The
    // 8:00-17:30 shift's halves split at 12:45.
    [Fact]
    public void BreakTileError_rejects_two_breaks_less_than_an_hour_apart() =>
        // 11:30-12:00 and 12:45-13:15: 45 minutes apart, though one is in each half.
        Assert.Matches("at least an hour apart", Breaks.BreakTileError([690, 705, 765, 780], 60, S, 17.5 * 60));

    [Fact]
    public void BreakTileError_rejects_two_breaks_in_the_same_half_of_the_shift()
    {
        // 09:15-09:45 and 11:00-11:30: an hour apart, but both before 12:45.
        Assert.Matches("one in each half", Breaks.BreakTileError([555, 570, 660, 675], 60, S, 17.5 * 60));
        // 07:30-14:00 (the review's G045 case): both in the first half.
        Assert.Matches("one in each half", Breaks.BreakTileError([510, 525, 600, 615], 60, 7.5 * 60, 14 * 60));
    }

    [Fact]
    public void BreakTileError_accepts_the_second_break_starting_exactly_at_the_midpoint() =>
        // 11:00-11:30 and 12:45-13:15 on 8:00-17:30.
        Assert.Null(Breaks.BreakTileError([660, 675, 765, 780], 60, S, 17.5 * 60));
}

public class RateGridTests
{
    private static readonly double[] Hours = [0, 1, 2];
    private static readonly string[] RoleCodes = ["SA-FF", "SA-LR"];

    private static Dictionary<string, double?> V(Dictionary<string, double> d) => d.ToDictionary(kv => kv.Key, kv => (double?)kv.Value);

    [Fact]
    public void Sends_nothing_when_nothing_was_edited()
    {
        var b = new Dictionary<string, double> { [RateGrid.RateKey("weekday", 0, "SA-FF")] = 31.27 };
        Assert.Empty(RateGrid.ChangedRateCells(b, V(b), "weekday", RoleCodes, Hours));
    }

    [Fact]
    public void Sends_only_the_edited_cell()
    {
        var b = new Dictionary<string, double>
        {
            [RateGrid.RateKey("weekday", 0, "SA-FF")] = 31.27,
            [RateGrid.RateKey("weekday", 1, "SA-FF")] = 31.27,
        };
        var values = V(b);
        values[RateGrid.RateKey("weekday", 1, "SA-FF")] = 44;
        Assert.Equal([new RateCellChange("weekday", 1, "SA-FF", 44)], RateGrid.ChangedRateCells(b, values, "weekday", RoleCodes, Hours));
    }

    // The bug this whole module exists to prevent: the grid only held the first
    // 1000 rows the server returned, and saving wrote 0 for every cell it hadn't
    // loaded. A cell absent from BOTH maps must never be written.
    [Fact]
    public void Never_writes_a_cell_the_page_never_loaded()
    {
        var b = new Dictionary<string, double> { [RateGrid.RateKey("weekday", 0, "SA-FF")] = 31.27 };
        var out_ = RateGrid.ChangedRateCells(b, V(b), "weekday", RoleCodes, Hours);
        Assert.Empty(out_);
        Assert.DoesNotContain(out_, c => c.Rate == 0);
    }

    [Fact]
    public void Writes_a_genuine_zero_only_when_the_user_clears_a_real_rate()
    {
        var b = new Dictionary<string, double> { [RateGrid.RateKey("weekday", 0, "SA-FF")] = 31.27 };
        var values = new Dictionary<string, double?> { [RateGrid.RateKey("weekday", 0, "SA-FF")] = 0 };
        Assert.Equal([new RateCellChange("weekday", 0, "SA-FF", 0)], RateGrid.ChangedRateCells(b, values, "weekday", RoleCodes, Hours));
    }

    [Fact]
    public void Creates_a_row_for_a_position_that_had_none_once_given_a_rate()
    {
        var values = new Dictionary<string, double?> { [RateGrid.RateKey("weekday", 2, "SA-LR")] = 35.5 };
        Assert.Equal([new RateCellChange("weekday", 2, "SA-LR", 35.5)], RateGrid.ChangedRateCells(new Dictionary<string, double>(), values, "weekday", RoleCodes, Hours));
    }

    [Fact]
    public void Ignores_a_new_cell_left_at_zero()
    {
        var values = new Dictionary<string, double?> { [RateGrid.RateKey("weekday", 2, "SA-LR")] = 0 };
        Assert.Empty(RateGrid.ChangedRateCells(new Dictionary<string, double>(), values, "weekday", RoleCodes, Hours));
    }

    [Fact]
    public void Only_considers_the_day_type_being_saved()
    {
        var b = new Dictionary<string, double> { [RateGrid.RateKey("saturday", 0, "SA-FF")] = 40 };
        var values = V(b);
        values[RateGrid.RateKey("saturday", 0, "SA-FF")] = 50;
        Assert.Empty(RateGrid.ChangedRateCells(b, values, "weekday", RoleCodes, Hours));
        Assert.Single(RateGrid.ChangedRateCells(b, values, "saturday", RoleCodes, Hours));
    }

    [Fact]
    public void Ignores_floating_point_noise()
    {
        var key = RateGrid.RateKey("weekday", 0, "SA-FF");
        Assert.Empty(RateGrid.ChangedRateCells(new Dictionary<string, double> { [key] = 0.1 + 0.2 }, new Dictionary<string, double?> { [key] = 0.3 }, "weekday", RoleCodes, Hours));
    }
}
