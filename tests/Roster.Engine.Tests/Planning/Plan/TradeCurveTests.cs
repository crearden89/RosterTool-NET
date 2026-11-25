// Port of src/lib/plan/tradeCurve.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;

namespace Roster.Engine.Tests.Planning.Plan;

public class TradeCurveTests
{
    private static QuarterHourPoint Q(string time, double allocation) => new(time, allocation);

    // ── hourly bucketing ──

    [Fact]
    public void Sums_the_four_blocks_of_an_hour_and_renormalises_to_1()
    {
        var curve = TradeCurve.BucketHourly([Q("09:00:00", 0.01), Q("09:15:00", 0.02), Q("09:30:00", 0.03), Q("09:45:00", 0.04), Q("10:00:00", 0.1)]);
        Assert.Equal([9.0, 10.0], curve.Select(p => p.Hour));
        Vitest.CloseTo(0.5, curve[0].Share);
        Vitest.CloseTo(1, curve.Aggregate(0.0, (s, p) => s + p.Share));
    }

    [Fact]
    public void Drops_zero_and_unusable_blocks_and_returns_nothing_for_a_closed_day()
    {
        Assert.Empty(TradeCurve.BucketHourly([Q("09:00:00", 0), Q("09:15:00", double.NaN)]));
        Assert.Empty(TradeCurve.BucketHourly([]));
    }

    // The reason for bucketing at all: G242's real Monday has a 3.71% block at
    // 16:15 wedged between 2.64% and 2.79% neighbours, while the 12:00 hour is
    // steadier. Per-block the spike wins; per-hour the genuinely busy hour does.
    [Fact]
    public void Puts_the_peak_on_the_sustained_hour_not_a_single_spiky_block()
    {
        List<QuarterHourPoint> rows =
        [
            Q("12:00:00", 0.03), Q("12:15:00", 0.03), Q("12:30:00", 0.03), Q("12:45:00", 0.03), // 12:00 hour = 0.12 sustained
            Q("16:00:00", 0.0264), Q("16:15:00", 0.0371), /* the spike */ Q("16:30:00", 0.0279), Q("16:45:00", 0.02), // 16:00 hour = 0.1114
        ];
        Assert.Equal("16:15:00", rows.Aggregate((b, r) => r.Allocation > b.Allocation ? r : b).BlockTime);
        Assert.Equal(12, TradeCurve.PeakHour(TradeCurve.BucketHourly(rows)));
    }

    [Fact]
    public void Has_no_peak_when_there_is_no_trade() => Assert.Null(TradeCurve.PeakHour([]));

    // ── busy period ──

    private static readonly List<CurvePoint> Curve = [new(9, 0.05), new(10, 0.09), new(11, 0.1), new(12, 0.095), new(13, 0.04)];

    [Fact]
    public void Includes_every_hour_within_the_threshold_of_the_peak() => Assert.Equal([10.0, 11, 12], TradeCurve.BusyHours(Curve, 85));

    [Fact]
    public void Narrows_to_the_peak_alone_at_100_percent() => Assert.Equal([11.0], TradeCurve.BusyHours(Curve, 100));

    [Fact]
    public void Is_empty_for_a_day_with_no_trade() => Assert.Empty(TradeCurve.BusyHours([], 85));

    // G242's real Monday: the strict test admits 12, 14, 16 and 17 but skips 13
    // and 15, which sit a single point lower. Drawing that as stripes says
    // "13:00 is a lull", which is false - the span is the honest shape.
    [Fact]
    public void Spans_the_busy_period_continuously_across_shallow_dips()
    {
        List<CurvePoint> plateau =
        [
            new(11, 0.1031), new(12, 0.1088), new(13, 0.0985), new(14, 0.1106),
            new(15, 0.1028), new(16, 0.1241), new(17, 0.1059), new(18, 0.0712),
        ];
        // 11:00 is 83% of the peak so it falls outside; 13:00 and 15:00 clear the
        // bar on either side of it, which is exactly the striping to avoid.
        Assert.Equal([12.0, 14, 16, 17], TradeCurve.BusyHours(plateau, 85));
        Assert.Equal((12.0, 17.0), TradeCurve.BusySpan(plateau, 85));
    }

    [Fact]
    public void Has_no_span_on_a_day_with_no_trade() => Assert.Null(TradeCurve.BusySpan([], 85));

    // ── axis ticks ──
    // A raw max/4 split on a 12.4% peak gives 3% / 7% / 10% / 13% - gridlines
    // that look like they mean something. Clean steps keep them round.

    [Fact]
    public void Steps_on_round_numbers_and_always_reaches_past_the_data()
    {
        // The top tick sets the plot's scale, so a ramp stopping at 0.10 under a
        // 0.1241 peak would draw the curve above the axis.
        var t = TradeCurve.NiceTicks(0.1241, 4);
        Assert.Equal([0, 0.05, 0.1, 0.15], t);
        Assert.True(t[^1] >= 0.1241);
    }

    [Fact]
    public void Stops_exactly_on_the_maximum_when_it_already_lands_on_a_step() =>
        Assert.Equal([0, 0.025, 0.05, 0.075, 0.1], TradeCurve.NiceTicks(0.1, 4));

    [Fact]
    public void Handles_a_flat_or_empty_day_without_dividing_by_zero() => Assert.Equal([0.0], TradeCurve.NiceTicks(0));

    // ── late trade share ──

    [Fact]
    public void Measures_the_share_from_an_hour_onward()
    {
        List<CurvePoint> curve = [new(12, 0.75), new(18, 0.25)];
        Vitest.CloseTo(0.25, TradeCurve.LateShare(curve, 17));
        Vitest.CloseTo(1, TradeCurve.LateShare(curve, 0));
        Assert.Equal(0, TradeCurve.LateShare([], 17));
    }

    // ── heat steps ──
    // A trading day's hours cluster in the top half of their own range, so equal
    // 0→max bands drop nearly every cell in the last two steps and the map reads
    // as a wall of red. Quantiles spread the ramp over the values that occur.
    private static readonly double[] Trading = [0.01, 0.04, 0.09, 0.1, 0.103, 0.106, 0.109, 0.11, 0.118, 0.124];

    [Fact]
    public void Spreads_the_ramp_across_the_values_that_actually_occur()
    {
        var t = TradeCurve.HeatThresholds(Trading);
        Assert.Equal(TradeCurve.HeatRamp.Count, Trading.Select(s => TradeCurve.HeatStep(s, t)).Distinct().Count()); // every step earns its place
    }

    [Fact]
    public void Puts_the_quietest_hour_at_the_bottom_and_the_busiest_at_the_top()
    {
        var t = TradeCurve.HeatThresholds(Trading);
        Assert.Equal(0, TradeCurve.HeatStep(0.01, t));
        Assert.Equal(TradeCurve.HeatRamp.Count - 1, TradeCurve.HeatStep(0.124, t));
    }

    [Fact]
    public void Uses_one_scale_for_every_day_so_equal_shares_get_equal_colours()
    {
        var t = TradeCurve.HeatThresholds(Trading);
        Assert.Equal(TradeCurve.HeatStep(0.106, t), TradeCurve.HeatStep(0.106, t));
        Assert.True(TradeCurve.HeatStep(0.04, t) < TradeCurve.HeatStep(0.11, t));
    }

    [Fact]
    public void Marks_no_trade_cells_as_absent_rather_than_as_the_lightest_step()
    {
        Assert.Equal(-1, TradeCurve.HeatStep(0, TradeCurve.HeatThresholds(Trading)));
        Assert.Equal(-1, TradeCurve.HeatStep(0.05, []));
    }

    [Fact]
    public void Ignores_closed_hours_when_working_out_the_bins()
    {
        Assert.Empty(TradeCurve.HeatThresholds([0, 0, 0]));
        Assert.Empty(TradeCurve.HeatThresholds([]));
    }

    [Fact]
    public void Stays_inside_the_ramp()
    {
        var t = TradeCurve.HeatThresholds(Trading);
        foreach (var share in new[] { 0.0001, 0.03, 0.09, 0.2 })
        {
            var i = TradeCurve.HeatStep(share, t);
            Assert.True(i >= 0 && i < TradeCurve.HeatRamp.Count);
        }
    }

    // ── weekday numbering ──

    [Fact]
    public void Counts_Monday_as_0_matching_the_quarter_hour_rows()
    {
        Assert.Equal(0, Period.DowMon0(new DateOnly(2026, 8, 17))); // a Monday
        Assert.Equal(6, Period.DowMon0(new DateOnly(2026, 8, 23))); // the Sunday after
    }

    // ── quarter-hour shares, for the plan grid's heat row ──

    [Fact]
    public void Keeps_the_15_minute_grain_and_renormalises_to_the_day()
    {
        var shares = TradeCurve.QuarterHourShares([Q("09:15:00", 0.02), Q("09:00:00", 0.01), Q("09:30:00", 0), Q("09:45:00", 0.01)]);
        // In time order, the empty quarter-hour left out, summing to the day.
        Assert.Equal([540.0, 555, 585], shares.Select(p => p.Time));
        Assert.Equal([0.25, 0.5, 0.25], shares.Select(p => p.Share));
    }

    [Fact]
    public void Quarter_hour_shares_are_empty_for_a_day_with_no_trade() =>
        Assert.Empty(TradeCurve.QuarterHourShares([Q("09:00:00", 0)]));
}
