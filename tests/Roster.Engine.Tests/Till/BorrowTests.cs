// Port of src/lib/till/borrow.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Till;
using static Roster.Engine.Tests.Till.TestNumbers;

namespace Roster.Engine.Tests.Till;

public class BorrowTests
{
    private const double H = 60;
    private const int Mon = 0, Tue = 1, Wed = 2, Fri = 4;
    private static readonly string[] Groups = ["load_running", "cleaning", "specials", "reset", "admin"];

    // A single block that over-plans by `excess` hours at `time`.
    private static BorrowBlock Over(double time, double excess) => new(time, 0.25, 0.25 - excess);

    private static JsMap<string, double> Hours(double lr, double cl, double sp, double rs, double ad) =>
        new JsMap<string, double>().Set("load_running", lr).Set("cleaning", cl).Set("specials", sp).Set("reset", rs).Set("admin", ad);

    private static JsMap<string, double> Plenty() => Hours(100, 100, 100, 100, 100);

    private static JsMap<string, double> With(JsMap<string, double> m, params (string G, double H)[] set)
    {
        var c = m.Copy();
        foreach (var (g, h) in set) c.Set(g, h);
        return c;
    }

    [Fact]
    public void Before_10_borrows_entirely_from_load_running()
    {
        var r = Borrow.ComputeBorrow([Over(8 * H, 1)], Mon, Plenty());
        Assert.Equal(1, Round2(r.Borrowed["load_running"]));
        Close(1, r.BorrowedTotal);
        Assert.Equal(0, r.Shortfall);
    }

    [Fact]
    public void From_18_borrows_entirely_from_cleaning()
    {
        var r = Borrow.ComputeBorrow([Over(19 * H, 2)], Mon, Plenty());
        Assert.Equal(2, Round2(r.Borrowed["cleaning"]));
    }

    [Fact]
    public void Daytime_on_a_non_specials_day_splits_70_30_reset_admin()
    {
        var r = Borrow.ComputeBorrow([Over(12 * H, 10)], Wed, Plenty());
        Assert.Equal(7, Round2(r.Borrowed["reset"]));
        Assert.Equal(3, Round2(r.Borrowed["admin"]));
        Assert.Equal(0, r.Borrowed["specials"]);
    }

    [Fact]
    public void Daytime_on_tuesday_and_friday_splits_65_25_10_with_specials()
    {
        foreach (var day in new[] { Tue, Fri })
        {
            var r = Borrow.ComputeBorrow([Over(12 * H, 10)], day, Plenty());
            Assert.Equal(6.5, Round2(r.Borrowed["reset"]));
            Assert.Equal(2.5, Round2(r.Borrowed["admin"]));
            Assert.Equal(1, Round2(r.Borrowed["specials"]));
        }
    }

    [Fact]
    public void Bands_are_applied_independently_across_one_day()
    {
        var r = Borrow.ComputeBorrow([Over(8 * H, 1), Over(12 * H, 10), Over(19 * H, 2)], Wed, Plenty());
        Assert.Equal(1, Round2(r.Borrowed["load_running"]));
        Assert.Equal(7, Round2(r.Borrowed["reset"]));
        Assert.Equal(3, Round2(r.Borrowed["admin"]));
        Assert.Equal(2, Round2(r.Borrowed["cleaning"]));
        Assert.Equal(13, Round2(r.BorrowedTotal));
    }

    [Fact]
    public void A_block_that_rounds_down_repays_part_of_a_round_up()
    {
        // +0.75h from a ceiling, −0.25h from a floor → 0.5h actually borrowed.
        var r = Borrow.ComputeBorrow([Over(8 * H, 0.75), Over(9 * H, -0.25)], Mon, Plenty());
        Assert.Equal(0.5, Round2(r.Borrowed["load_running"]));
    }

    [Fact]
    public void A_band_that_nets_negative_borrows_nothing_at_all()
    {
        var r = Borrow.ComputeBorrow([Over(8 * H, -1)], Mon, Plenty());
        Assert.Equal(0, r.BorrowedTotal);
        Assert.Equal(0, r.Requested);
        Assert.Equal(0, r.Shortfall);
    }

    [Fact]
    public void One_bands_round_downs_never_subsidise_another_band()
    {
        // Morning nets −2 (no borrow), evening nets +2 (full borrow from Cleaning).
        var r = Borrow.ComputeBorrow([Over(8 * H, -2), Over(19 * H, 2)], Mon, Plenty());
        Assert.Equal(0, r.Borrowed["load_running"]);
        Assert.Equal(2, Round2(r.Borrowed["cleaning"]));
    }

    [Fact]
    public void Leaves_the_floor_behind_and_spills_the_rest_to_other_groups()
    {
        var available = Hours(1, 10, 10, 10, 10); // load_running: only 0.75h borrowable above the floor
        var r = Borrow.ComputeBorrow([Over(8 * H, 3)], Mon, available);
        Assert.Equal(1 - Borrow.MinRetainedHours, Round2(r.Borrowed["load_running"]));
        // The remaining 2.25h came from the other groups, not from Load Running.
        Assert.Equal(3, Round2(r.BorrowedTotal));
        Assert.Equal(0, r.Shortfall);
        Assert.Equal(Borrow.MinRetainedHours, Round2(available["load_running"] - r.Borrowed["load_running"]));
    }

    [Fact]
    public void Never_takes_any_group_below_the_floor_even_when_badly_short()
    {
        var available = Hours(0.5, 0.5, 0.5, 0.5, 0.5);
        var r = Borrow.ComputeBorrow([Over(12 * H, 20)], Wed, available);
        foreach (var g in Groups) Assert.True(available[g] - r.Borrowed[g] >= Borrow.MinRetainedHours - 1e-9);
        // What couldn't be funded is reported rather than silently swallowed.
        Assert.True(r.Shortfall > 0);
        Assert.Equal(20, Round2(r.BorrowedTotal + r.Shortfall));
    }

    [Fact]
    public void Reports_a_shortfall_when_every_group_is_already_at_the_floor()
    {
        var f = Borrow.MinRetainedHours;
        var r = Borrow.ComputeBorrow([Over(8 * H, 5)], Mon, Hours(f, f, f, f, f));
        Assert.Equal(0, r.BorrowedTotal);
        Assert.Equal(5, Round2(r.Shortfall));
    }

    // The live case: Admin's task catalogue had pinned 10.53h of named jobs
    // against a 10.43h benchmark, and the borrow took another 1.25h anyway -
    // so Admin planned 11.50h against a 9.18h benchmark.
    [Fact]
    public void Will_not_lend_hours_a_group_has_already_promised_to_fixed_work()
    {
        var available = With(Plenty(), ("admin", 10.43), ("reset", 6.85));
        var r = Borrow.ComputeBorrow([Over(12 * H, 4.15)], Mon, available, null, null, new JsMap<string, double>().Set("admin", 10.53));
        Assert.Equal(0, r.Borrowed["admin"]);
        // Admin's 30% share fell to Reset, the band's other group.
        Assert.Equal(4.15, Round2(r.Borrowed["reset"]));
        Assert.Equal(0, r.Shortfall);
    }

    [Fact]
    public void Lends_only_the_hours_held_above_the_commitment()
    {
        var available = With(Plenty(), ("admin", 10), ("reset", 0.25)); // Reset at its floor
        var r = Borrow.ComputeBorrow([Over(12 * H, 5)], Wed, available, null, null, new JsMap<string, double>().Set("admin", 8)); // 2h genuinely spare
        Assert.Equal(2, Round2(r.Borrowed["admin"]));
        Assert.Equal(0, r.Borrowed["reset"]);
        // The remaining 3h went outside the band rather than into committed work.
        Assert.Equal(5, Round2(r.BorrowedTotal));
    }

    [Fact]
    public void Keeps_specials_out_of_it_on_a_non_delivery_day_even_when_spilling()
    {
        var available = With(Plenty(), ("reset", 0.25), ("admin", 0.25), ("specials", 50));
        var r = Borrow.ComputeBorrow([Over(12 * H, 3)], Wed, available); // Wednesday
        // Reset and Admin are both at the floor, so the spill leaves the band -
        // Specials is a legitimate target only because nothing in-band remains.
        Assert.Equal(0, r.Borrowed["reset"]);
        Assert.Equal(0, r.Borrowed["admin"]);
        Assert.Equal(3, Round2(r.BorrowedTotal));
    }

    [Fact]
    public void Weights_within_every_band_sum_to_1()
    {
        foreach (var band in Borrow.DefaultBorrowBands)
            foreach (var w in new[] { band.Weights, band.AltWeights }.Where(x => x is not null))
                Assert.Equal(1, Round2(w!.Values().Sum()));
    }

    [Fact]
    public void Covers_the_whole_day_with_no_gaps_or_overlaps()
    {
        var sorted = Js.Sorted(Borrow.DefaultBorrowBands, (a, b) => a.FromMin - b.FromMin);
        Assert.Equal(0, sorted[0].FromMin);
        Assert.Equal(24 * H, sorted[^1].ToMin);
        for (var i = 1; i < sorted.Count; i++) Assert.Equal(sorted[i - 1].ToMin, sorted[i].FromMin);
    }

    // New: the Rules page's band boundaries (R15), as borrowBandsAt labels them.
    [Fact]
    public void Bands_at_moved_boundaries_keep_their_weights_and_say_where_they_are()
    {
        var bands = Borrow.BorrowBandsAt(9 * 60 + 30, 17 * 60);
        Assert.Equal(["Before 09:30", "09:30-17:00", "From 17:00"], bands.Select(b => b.Label));
        Assert.Equal((0.0, 570.0), (bands[0].FromMin, bands[0].ToMin));
        Assert.Equal((570.0, 1020.0), (bands[1].FromMin, bands[1].ToMin));
        Assert.Equal((1020.0, 1440.0), (bands[2].FromMin, bands[2].ToMin));
        Assert.Same(Borrow.DefaultBorrowBands[1].AltWeights, bands[1].AltWeights);
    }
}
