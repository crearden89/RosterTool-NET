// Port of src/lib/stage2/shiftCost.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Shaper;

public class ShiftLengthCostTests
{
    private const double H = 60;
    private const double Rate = 43.23; // the client's costing sheet

    private static double PerWorkedHour(double lenMin) => ShiftLengthCost.CostRatio("tiered", lenMin) * Rate;

    /// <summary>vitest's toBeCloseTo(want, digits): |want - got| &lt; 10^-digits / 2.</summary>
    private static void CloseTo(double want, double got, int digits, string because) =>
        Assert.True(Math.Abs(want - got) < Math.Pow(10, -digits) / 2, $"{because}: {got} is not close to {want}");

    [Fact]
    public void Reproduces_the_costing_sheet()
    {
        // length → $ per worked hour, straight off the client's table.
        (double Len, double Want)[] sheet =
        [
            (3 * H, 47.16),
            (4 * H, 46.11),
            (4.25 * H, 47.03),
            (5 * H, 46.4),
            (6 * H, 45.85), // sheet says 45.82; it rounds the 10-minute unpaid to 0.17
            (6.25 * H, 47.35),
            (8 * H, 46.32),
            (9 * H, 45.93),
            (9.5 * H, 45.77),
        ];
        foreach (var (len, want) in sheet) CloseTo(want, PerWorkedHour(len), 1, $"{Js.NumberToString(len / 60)}h");
    }

    [Fact]
    public void Prices_the_six_hour_tier_edge_a_longer_shift_that_delivers_less()
    {
        // The single most expensive thing the cutter can do: cross six hours by one
        // block. The entitlement doubles, so the shift is paid for more and works
        // less.
        // A quarter hour LONGER on the clock, a quarter hour LESS of work.
        Assert.Equal(5.5 * H, ShiftLengthCost.WorkMinutes("tiered", 6 * H));
        Assert.Equal(5.25 * H, ShiftLengthCost.WorkMinutes("tiered", 6.25 * H));
        // The wage bill even falls, because more of the bigger break is unpaid -
        // which is exactly why total cost is the wrong thing to compare shifts on.
        Assert.True(ShiftLengthCost.PaidMinutes("tiered", 6.25 * H) < ShiftLengthCost.PaidMinutes("tiered", 6 * H));
        // Per hour of work actually delivered, it is the worst length on the board.
        Assert.True(ShiftLengthCost.CostRatio("tiered", 6.25 * H) > ShiftLengthCost.CostRatio("tiered", 6 * H));
    }

    [Fact]
    public void Ranks_9_5h_first_and_6_25h_last_of_the_legal_lengths()
    {
        var order = ShiftLengthCost.PreferredLengths("tiered", 3 * H, 9.5 * H);
        Assert.Equal(9.5 * H, order[0]);
        Assert.Equal(6.25 * H, order[^1]);
        // 6.00 beats everything except the near-nine lengths.
        Assert.True(order.IndexOf(6 * H) < order.IndexOf(8 * H));
    }

    [Fact]
    public void Is_the_same_ranking_whatever_the_wage()
    {
        // The rate cancels: the ordering is a property of the break tiers, so one
        // table serves every store and every pay grade.
        var a = ShiftLengthCost.PreferredLengths("tiered", 3 * H, 9.5 * H);
        var scaled = Js.Sorted(a, (x, y) =>
            (ShiftLengthCost.CostRatio("tiered", x) * 99.9 - ShiftLengthCost.CostRatio("tiered", y) * 99.9).Or(y - x));
        Assert.Equal(a, scaled);
    }

    [Fact]
    public void A_managers_flat_unpaid_half_hour_has_no_tier_edge_to_fall_off()
    {
        var order = ShiftLengthCost.PreferredLengths("flat_30_unpaid", 3 * H, 12 * H);
        Assert.Equal(12 * H, order[0]); // longer is always cheaper per worked hour
    }
}
