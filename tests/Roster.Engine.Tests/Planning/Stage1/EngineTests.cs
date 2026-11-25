// Port of src/lib/stage1/engine.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Stage1;

/// <summary>Shared builders for the Stage 1 tests.</summary>
internal static class S1
{
    /// <summary>{ ...zeroBench, g: h }: every window group at 0, in the TypeScript key order.</summary>
    public static JsMap<string, double> Bench(params (string Group, double Hours)[] hours)
    {
        var m = new JsMap<string, double>();
        foreach (var g in new[] { "load_running", "cleaning", "specials", "reset", "admin" }) m.Set(g, 0);
        foreach (var (g, h) in hours) m.Set(g, h);
        return m;
    }

    public static WindowRule W(string group, double start, double end) => new(group, start, end);

    public static List<PlannedBlock> Blocks(Stage1Result r, string group) =>
        Js.Sorted(r.Blocks.Where(b => b.Group == group), (a, b) => a.Time - b.Time);

    public static GroupAllocation Alloc(Stage1Result r, string group) => r.Allocations.First(a => a.Group == group);
}

public class Stage1PlacementTests
{
    [Fact]
    public void Places_a_groups_hours_contiguously_within_its_window()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 7 * 60,
            TradingClose = 21 * 60,
            Benchmark90 = S1.Bench(("reset", 4)), // 4h → 16 blocks
            Windows = [S1.W("reset", 8 * 60, 16 * 60)],
            Checkout = [],
        });
        var reset = S1.Blocks(r, "reset");
        Assert.Equal(16, reset.Count); // 16 contiguous 15-min blocks
        // One contiguous run anchored to the window END (a partial layer abuts
        // whatever follows it, so shift-cutting can flow it into longer shifts).
        Assert.Equal(12 * 60, reset[0].Time);
        Assert.Equal(16 * 60 - 15, reset[^1].Time);
        for (var i = 1; i < reset.Count; i++) Assert.Equal(15, reset[i].Time - reset[i - 1].Time);
        Assert.Equal(4, S1.Alloc(r, "reset").AllocatedHours);
    }

    [Fact]
    public void Layers_headcount_when_hours_exceed_the_window_length()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 6 * 60,
            TradingClose = 21 * 60,
            Benchmark90 = S1.Bench(("cleaning", 4)), // 16 blocks
            Windows = [S1.W("cleaning", 6 * 60, 9 * 60)], // only 3h = 12 slots
            Checkout = [],
        });
        // 12 slots get 1 headcount, first 4 slots get a 2nd → total 16 block-headcounts.
        Assert.Equal(16, r.Blocks.Where(b => b.Group == "cleaning").Sum(b => b.Headcount));
        Assert.Equal(4, S1.Alloc(r, "cleaning").AllocatedHours);
    }

    [Fact]
    public void Adds_NonOp_hours_on_top_of_task_demand()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 7 * 60,
            TradingClose = 21 * 60,
            Benchmark90 = S1.Bench(("reset", 4)), // 4h task work
            Windows = [S1.W("reset", 8 * 60, 16 * 60)],
            Checkout = [],
            NonOpHours = 5, // 5h non-op ⇒ 9h total to cover
        });
        var nonOp = S1.Alloc(r, "non_op");
        Assert.Equal(5, nonOp.BenchmarkHours);
        Assert.Equal(5, nonOp.AllocatedHours, 6);
        // Total is benchmark reset (4h) + non-op (5h) = 9h, not 4 - 5.
        Assert.Equal(9, r.TotalAllocated, 6);
        Assert.Equal(9, r.TotalBenchmark, 6);
    }

    [Fact]
    public void Includes_checkout_blocks_and_keeps_the_day_within_the_1_hour_tolerance()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 7 * 60,
            TradingClose = 21 * 60,
            Benchmark90 = S1.Bench(("load_running", 20), ("admin", 8)),
            Windows = [S1.W("load_running", 6 * 60, 11 * 60), S1.W("admin", 7 * 60, 17 * 60)],
            Checkout = [new(9 * 60, 2), new(9 * 60 + 15, 3)],
        });
        Assert.Equal(1.25, S1.Alloc(r, "checkout").AllocatedHours, 6);
        // benchmark rounds to whole blocks, so day variance stays within an hour.
        Assert.True(r.WithinTolerance);
        Assert.True(Math.Abs(r.Variance) <= 1);
    }

    [Fact]
    public void Prefers_a_0700_start_and_leaves_premium_slots_unused_when_the_load_fits()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 8 * 60 + 30, // store opens 08:30
            TradingClose = 20 * 60,
            StaffOpen = 6 * 60, // pre-open prep floor 06:00
            StaffClose = 20 * 60 + 30,
            Benchmark90 = S1.Bench(("load_running", 1.5)), // 6 blocks - fits 07:00→08:30 exactly
            Windows = [S1.W("load_running", 6 * 60, 11 * 60)],
            Checkout = [],
        });
        var lr = S1.Blocks(r, "load_running");
        Assert.Equal(6, lr.Count);
        // Never dips into the pre-07:00 premium floor when normal capacity fits...
        Assert.All(lr, b => Assert.True(b.Time >= 7 * 60));
        // ...and anchors to the window end as one contiguous run.
        Assert.Equal(11 * 60 - 15, lr[^1].Time);
        for (var i = 1; i < lr.Count; i++) Assert.Equal(15, lr[i].Time - lr[i - 1].Time);
    }

    [Fact]
    public void Places_PH_reallocated_Load_Running_into_the_prior_evening_after_1800()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 8 * 60 + 30,
            TradingClose = 20 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 20 * 60 + 30,
            Benchmark90 = S1.Bench(("load_running", 2)), // normal morning LR
            Windows = [S1.W("load_running", 6 * 60, 11 * 60)],
            Checkout = [],
            EveningLoadRunningHours = 1.5, // 6 blocks pulled in from the next day's PH
        });
        var evening = S1.Blocks(r, "load_running").Where(b => b.Time >= 18 * 60).ToList();
        Assert.Equal(6, evening.Count); // 1.5h of evening blocks
        // All after 18:00 (the PH-reallocation floor), contiguous to the staffed close.
        Assert.All(evening, b => Assert.True(b.Time >= 18 * 60));
        Assert.Equal(20 * 60 + 30 - 15, evening[^1].Time);
        Assert.Equal(2 + 1.5, S1.Alloc(r, "load_running").AllocatedHours, 6); // morning + reallocated evening
    }

    [Fact]
    public void Never_places_work_outside_its_window_and_flags_an_impossible_window()
    {
        // Cleaning windowed 20:00-22:00 but the staffed day ends 19:30: windows are
        // hard time barriers, so the hours are NOT smuggled elsewhere - they stay
        // unplaced and the conflict is flagged for the planner to resolve.
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 8 * 60 + 30,
            TradingClose = 19 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 19 * 60 + 30,
            Benchmark90 = S1.Bench(("cleaning", 2)),
            Windows = [S1.W("cleaning", 20 * 60, 22 * 60)],
            Checkout = [],
        });
        Assert.Empty(r.Blocks.Where(b => b.Group == "cleaning"));
        Assert.Equal(0, S1.Alloc(r, "cleaning").AllocatedHours);
        Assert.Contains(r.Flags, f => f.StartsWith("cleaning", StringComparison.Ordinal) && f.Contains("2.00h"));
    }

    [Fact]
    public void Never_stacks_Specials_deeper_than_its_limit_RC4()
    {
        // The old 17:00 close: Specials from 17:00 left one quarter-hour before the
        // staffed day ended, and all 3.75h landed there, 15 people deep, silently.
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 8 * 60,
            TradingClose = 17 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 17 * 60 + 15,
            Benchmark90 = S1.Bench(("specials", 3.75)), // 15 blocks
            Windows = [S1.W("specials", 17 * 60, 21 * 60)],
            Checkout = [],
        });
        var specials = r.Blocks.Where(b => b.Group == "specials").ToList();
        Assert.Equal(Stage1Engine.MaxBulkDepth["specials"], specials.Max(b => b.Headcount));
        var placedH = Stage1Engine.MaxBulkDepth["specials"] * 15 / 60;
        Assert.Equal(placedH, S1.Alloc(r, "specials").AllocatedHours);
        var flag = r.Flags.First(f => f.StartsWith("specials", StringComparison.Ordinal) && f.Contains("too short"));
        Assert.Contains($"{Js.ToFixed(3.75 - placedH, 2)}h could not be placed", flag);
        // ...and only that flag - not the generic "outside the staffed day" one too.
        Assert.Single(r.Flags.Where(f => f.StartsWith("specials", StringComparison.Ordinal)));
    }

    [Fact]
    public void Leaves_a_window_wide_enough_for_its_hours_untouched_by_the_depth_limit()
    {
        // The same 3.75h with the window following the close (15:00 on a 17:00
        // close): ten quarter-hours, two deep at most - nothing capped, nothing flagged.
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 8 * 60,
            TradingClose = 17 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 17 * 60 + 30,
            Benchmark90 = S1.Bench(("specials", 3.75)),
            Windows = [S1.W("specials", 15 * 60, 21 * 60)],
            Checkout = [],
        });
        Assert.Equal(3.75, S1.Alloc(r, "specials").AllocatedHours);
        Assert.Empty(r.Flags.Where(f => f.StartsWith("specials", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dips_into_pre_0700_slots_only_once_the_normal_cost_window_is_exhausted()
    {
        var r = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = 8 * 60 + 30,
            TradingClose = 20 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 20 * 60 + 30,
            Benchmark90 = S1.Bench(("load_running", 2.5)), // 10 blocks; only 6 fit 07:00→08:30
            Windows = [S1.W("load_running", 6 * 60, 8 * 60 + 30)], // pre-open only
            Checkout = [],
        });
        var lr = S1.Blocks(r, "load_running");
        // 6 normal (07:00-08:30) + 4 premium (06:00-07:00) = 10 blocks, earliest now 06:00.
        Assert.Equal(10, lr.Count);
        Assert.Equal(6 * 60, lr[0].Time);
    }
}
