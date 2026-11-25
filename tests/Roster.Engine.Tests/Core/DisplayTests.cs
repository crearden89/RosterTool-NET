// Port of src/lib/roster/display.test.ts.

using Roster.Engine.Core;

namespace Roster.Engine.Tests.Core;

public class DisplayTests
{
    private const double H = 60;

    private static RawShift Shift(string key = "s1", IReadOnlyList<RawBlock>? rawBlocks = null) =>
        new(key, "Alex", "SA", 9 * H, 17 * H, 60, rawBlocks ?? []);

    // Fill a span with one group, on the 15-minute grid.
    private static List<RawBlock> Span(double from, double to, string group)
    {
        var out_ = new List<RawBlock>();
        for (var m = from; m < to; m += 15) out_.Add(new RawBlock(m, group));
        return out_;
    }

    private static List<double> GroupsAt(RosterRow row, string group) =>
        row.Blocks.Where(b => b.Group == group).Select(b => b.Time).ToList();

    // The Floor Display reads persisted tiles. When approval kept the real break
    // placement, it must be shown AS PLANNED - not re-derived at the midpoint.
    [Fact]
    public void BuildRosterRows_uses_the_break_tiles_it_was_given()
    {
        var raw = Span(9 * H, 10 * H, "load_running").Concat(Span(10 * H, 11 * H, "break")).Concat(Span(11 * H, 17 * H, "checkout")).ToList();
        var row = Display.BuildRosterRows([Shift(rawBlocks: raw)])[0];
        Assert.Equal([10 * H, 10 * H + 15, 10 * H + 30, 10 * H + 45], GroupsAt(row, "break"));
    }

    // Legacy rows approved before breaks were persisted carry no break tile at
    // all; those still get the midpoint overlay rather than a blank gap.
    [Fact]
    public void BuildRosterRows_falls_back_to_the_midpoint_overlay_when_no_break_tile_is_present()
    {
        var row = Display.BuildRosterRows([Shift(rawBlocks: Span(9 * H, 17 * H, "checkout"))])[0];
        var breaks = GroupsAt(row, "break");
        Assert.NotEmpty(breaks);
        // Around the middle of a 09:00-17:00 shift, nowhere near the edges.
        Assert.True(breaks.Min() > 11 * H);
        Assert.True(breaks.Max() < 15 * H);
    }

    // The regression itself: a planned break that was stripped on the way to the
    // database leaves the tile missing AND the overlay painting elsewhere.
    [Fact]
    public void BuildRosterRows_would_show_a_gap_at_the_real_break_if_the_tile_were_dropped()
    {
        var withBreak = Span(9 * H, 10 * H, "load_running").Concat(Span(10 * H, 11 * H, "break")).Concat(Span(11 * H, 17 * H, "checkout")).ToList();
        var stripped = withBreak.Where(b => b.Group != "break").ToList();
        var kept = Display.BuildRosterRows([Shift(rawBlocks: withBreak)])[0];
        var lost = Display.BuildRosterRows([Shift("s2", stripped)])[0];
        // Kept: a tile at 10:00. Lost: nothing at 10:00 at all (the gap).
        Assert.Contains(kept.Blocks, b => b.Time == 10 * H);
        Assert.DoesNotContain(lost.Blocks, b => b.Time == 10 * H);
        // ...and the overlay puts the break somewhere else entirely.
        Assert.NotEqual(GroupsAt(kept, "break"), GroupsAt(lost, "break"));
    }

    // One row per argument; each string is that row's group at 09:00, 09:15, …
    private static List<List<(double Time, string Group)>> Rows(params string[][] groupLists) =>
        groupLists.Select(gs => gs.Select((g, j) => (9 * H + j * 15, g)).ToList()).ToList();

    [Fact]
    public void CheckoutTilesByTime_counts_the_merged_Checkout_group()
    {
        // The regression: before the Registers/SCO -> Checkout merge the count
        // matched on "registers"/"scos". Those groups stopped arriving, so every
        // column counted zero and the Plan view flagged a shortfall on a fully
        // manned front end.
        var got = Display.CheckoutTilesByTime(Rows(["checkout", "checkout"], ["checkout", "cleaning"]));
        Assert.Equal(2, got.Get(9 * H));
        Assert.Equal(1, got.Get(9 * H + 15));
    }

    [Fact]
    public void CheckoutTilesByTime_still_counts_pre_merge_registers_and_scos_blocks()
    {
        var got = Display.CheckoutTilesByTime(Rows(["registers"], ["scos"], ["checkout"]));
        Assert.Equal(3, got.Get(9 * H));
    }

    [Fact]
    public void CheckoutTilesByTime_ignores_every_non_checkout_group()
    {
        var got = Display.CheckoutTilesByTime(Rows(["load_running", "break", "cleaning", "non_op", "flex"]));
        Assert.Equal(0, got.Size);
    }

    [Fact]
    public void CheckoutTilesByTime_agrees_with_SplitCheckout_on_what_a_checkout_block_is()
    {
        // The two halves of the contract that drifted apart. Anything SplitCheckout
        // renders as a CO tile must also be counted behind it.
        string[] groups = ["checkout", "registers", "scos", "load_running", "cleaning", "break"];
        var painted = Display.SplitCheckout([groups.Select((g, j) => (Time: 9 * H + j * 15, Group: g))], b => b.Group)[0];
        var counted = Display.CheckoutTilesByTime(Rows(groups));
        for (var j = 0; j < groups.Length; j++)
            Assert.Equal(painted[j] == "checkout", counted.Has(9 * H + j * 15));
    }
}
