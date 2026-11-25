// Port of src/lib/subtasks/label.test.ts.

using Roster.Engine.Core;
using Roster.Engine.SubTasks;
using static Roster.Engine.Tests.SubTasks.SubTaskTestKit;

namespace Roster.Engine.Tests.SubTasks;

public class LabelTests
{
    private const double H = 60;

    [Fact]
    public void LargestRemainder_gives_whole_counts_that_add_up()
    {
        var m = Label.LargestRemainder(7, [("a", 25), ("b", 50), ("c", 25)]);
        Assert.Equal(7, m.Values().Sum());
        Assert.Equal([2, 3, 2], m.Values());
    }

    [Fact]
    public void LargestRemainder_shares_equally_when_every_weight_is_zero_and_gives_nothing_to_a_zero_weight_otherwise()
    {
        Assert.Equal([2, 2], Label.LargestRemainder(4, [("a", 0), ("b", 0)]).Values());
        Assert.Equal(0, Label.LargestRemainder(5, [("a", 0), ("b", 3)])["a"]);
    }

    private static readonly List<IReadOnlyList<DisplayBlock>> TheDay =
    [
        Tiles(("load_running", 6 * H, 12), ("checkout", 9 * H, 8), ("cleaning", 11 * H, 4)),
        Tiles(("load_running", 6 * H + 30, 10), ("specials", 9 * H, 6), ("break", 10 * H + 30, 2), ("reset", 11 * H, 6)),
        Tiles(("admin", 7 * H, 8), ("load_running", 9 * H, 3), ("cleaning", 20 * H, 6), ("non_op", 22 * H, 1)),
    ];

    [Fact]
    public void Labels_every_tile_of_the_four_groups_and_the_labels_add_up()
    {
        foreach (var date in new[] { Mon, Tue, Fri }) ExpectAddsUp(TheDay, Label.LabelBlocks(TheDay, Ctx(date)));
    }

    [Fact]
    public void Uses_the_default_shares_when_the_load_board_has_no_history()
    {
        var crew = Enumerable.Range(0, 5).Select(i => (IReadOnlyList<DisplayBlock>)Tiles(("load_running", 6 * H + i * 15, 20))).ToList();
        var subs = Label.SubtaskTiles(Label.LabelBlocks(crew, Ctx()));
        // 100 tiles: 12 / 20 / 12 / 36 / 5 / 10 / 5.
        Assert.Equal(
            [("produce", 12.0), ("long_life", 20.0), ("freezer", 12.0), ("ambient", 36.0), ("bread", 5.0), ("short_life", 10.0), ("freeze_to_thaw", 5.0)],
            Catalogue.SubtasksOf("load_running").Select(s => (s.Key, subs.GetOr(s.Key, 0))));
    }

    [Fact]
    public void Follows_the_load_boards_average_day_when_it_has_one()
    {
        List<IReadOnlyList<DisplayBlock>> crew = [Tiles(("load_running", 6 * H, 10)), Tiles(("load_running", 6 * H, 10))];
        var load = new LoadSplit { Minutes = Map(("produce", 50), ("ambient", 150)), Priority = Map(("produce", 3), ("ambient", 5)) };
        var subs = Label.SubtaskTiles(Label.LabelBlocks(crew, Ctx(load: load)));
        Assert.Equal(5, subs["produce"]);
        Assert.Equal(15, subs["ambient"]);
        Assert.False(subs.Has("long_life"));
    }

    [Fact]
    public void One_persons_load_running_reads_as_runs_not_confetti()
    {
        var one = Label.LabelBlocks([Tiles(("load_running", 6 * H, 16))], Ctx())[0];
        var labels = Seq(one, "load_running");
        Assert.Equal(labels.Distinct().Count() - 1, Switches(labels));
        // Worked in the load board's order: Freeze to Thaw first, Bread last.
        Assert.Equal("freeze_to_thaw", labels[0]);
        Assert.Equal("bread", labels[^1]);
    }

    [Fact]
    public void Each_person_owns_one_part_of_the_load_and_works_it_through()
    {
        var crew = Enumerable.Range(0, 3).Select(_ => (IReadOnlyList<DisplayBlock>)Tiles(("load_running", 6 * H, 12))).ToList();
        var load = new LoadSplit
        {
            Minutes = Map(("produce", 100), ("long_life", 100), ("ambient", 160)),
            Priority = Map(("produce", 3), ("long_life", 4), ("ambient", 5)),
        };
        var out_ = Label.LabelBlocks(crew, Ctx(load: load));
        Assert.Equal(new HashSet<string?> { "produce", "long_life", "ambient" }, out_.Select(s => Seq(s, "load_running")[0]).ToHashSet());
        foreach (var s in out_) Assert.True(Switches(Seq(s, "load_running")) <= 1);
    }

    [Fact]
    public void Works_ds_last_as_part_of_ambient_and_labels_them_ambient()
    {
        var load = new LoadSplit { Minutes = Map(("produce", 60), ("ambient", 60), ("ambient_last", 60)), Priority = Map(("produce", 3), ("ambient", 5)) };
        var one = Label.LabelBlocks([Tiles(("load_running", 6 * H, 12))], Ctx(load: load))[0];
        // Produce first, then Ambient, then the D's - Ambient again - at the end.
        Assert.Equal([.. Enumerable.Repeat("produce", 4), .. Enumerable.Repeat("ambient", 8)], Seq(one, "load_running"));
        var crew = Label.LabelBlocks([Tiles(("load_running", 6 * H, 6)), Tiles(("load_running", 6 * H, 6)), Tiles(("load_running", 6 * H, 6))], Ctx(load: load));
        Assert.Equal(12, Label.SubtaskTiles(crew)["ambient"]);
        // With three people, the D's are worked last: each person's labels change at most twice.
        foreach (var s in crew) Assert.True(Switches(Seq(s, "load_running")) <= 2);
    }

    [Fact]
    public void Builds_specials_on_tuesdays_and_fridays_only()
    {
        List<IReadOnlyList<DisplayBlock>> sp = [Tiles(("specials", 9 * H, 10))];
        foreach (var date in new[] { Tue, Fri })
        {
            var subs = Label.SubtaskTiles(Label.LabelBlocks(sp, Ctx(date)));
            Assert.Equal([5, 2, 3], new[] { subs["building"], subs["tidy"], subs["running"] });
        }
        foreach (var iso in new[] { "2026-11-09", "2026-11-11", "2026-11-12", "2026-11-14", "2026-11-15" })
        {
            var subs = Label.SubtaskTiles(Label.LabelBlocks(sp, Ctx(DateOnlyExtensions.ParseIso(iso))));
            // Tidy and Running in the ratio of their shares, 20 : 30.
            Assert.Equal([0, 4, 6], new[] { subs.GetOr("building", 0), subs["tidy"], subs["running"] });
        }
    }

    [Fact]
    public void Splits_cleaning_25_50_25()
    {
        var subs = Label.SubtaskTiles(Label.LabelBlocks([Tiles(("cleaning", 9 * H, 8))], Ctx()));
        Assert.Equal([2, 4, 2], new[] { subs["front_end"], subs["shop_floor"], subs["boh"] });
    }

    [Fact]
    public void Keeps_a_label_of_the_tiles_own_group_and_drops_any_other()
    {
        var s = Tiles(("cleaning", 9 * H, 4), ("specials", 10 * H, 2));
        s[0] = s[0] with { Sub = "boh" };
        s[1] = s[1] with { Sub = "re_runs" }; // not Cleaning's
        s[4] = s[4] with { Sub = "building" };
        var out_ = Label.LabelBlocks([s], Ctx())[0];
        Assert.Equal("boh", out_[0].Sub);
        Assert.NotEqual("re_runs", out_[1].Sub);
        // A label chosen in an edit stays, even on a day nothing is built.
        Assert.Equal("building", out_[4].Sub);
        var subs = Label.SubtaskTiles([out_]);
        Assert.Equal([1, 2, 1], new[] { subs["front_end"], subs["shop_floor"], subs["boh"] });
    }

    private static readonly ResetSource Stage1 = new()
    {
        StaffOpen = 6 * H,
        StaffClose = 22 * H,
        ResetTasks =
        [
            new("Re-run 1", 30, 9 * H, 12 * H),
            new("Re-run 2", 30, 14 * H, 17 * H),
            new("First declare (light)", 30, 14 * H, 17 * H),
            new("Full decard", 60, 18 * H, 21 * H),
        ],
        UnitWindows =
        [
            .. new[] { 9 * H, 9 * H + 15 }.Select(t => new ResetSourceUnit("reset", t, 9 * H, 12 * H, true, ["Re-run 1"])),
            .. new[] { 14 * H, 14 * H + 15, 14 * H + 30, 14 * H + 45 }.Select(t => new ResetSourceUnit("reset", t, 14 * H, 17 * H, true, ["Re-run 2", "First declare (light)"])),
            .. new[] { 18 * H, 18 * H + 15, 18 * H + 30, 18 * H + 45 }.Select(t => new ResetSourceUnit("reset", t, 18 * H, 21 * H, true, ["Full decard"])),
            new ResetSourceUnit("admin", 7 * H, 6 * H, 22 * H, false),
        ],
    };

    [Fact]
    public void A_rerun_task_is_reruns_any_other_decarding_a_job_of_both_split_by_minutes()
    {
        var units = Label.ResetUnits(Stage1);
        Assert.Equal(10, units.Count);
        Assert.Equal(4, units.Count(u => u.Sub == "re_runs"));
    }

    [Fact]
    public void Matches_painted_tiles_to_the_planned_quarter_hours()
    {
        List<IReadOnlyList<DisplayBlock>> day = [Tiles(("reset", 10 * H, 2), ("reset", 15 * H, 4)), Tiles(("reset", 19 * H, 4))];
        var out_ = Label.LabelBlocks(day, Ctx(stage1: Stage1));
        ExpectAddsUp(day, out_);
        Assert.Equal(["re_runs", "re_runs"], Seq(out_[0], "reset").Take(2));
        Assert.Equal(["decarding", "decarding", "re_runs", "re_runs"], Seq(out_[0], "reset").Skip(2).Order());
        Assert.Equal(["decarding", "decarding", "decarding", "decarding"], Seq(out_[1], "reset"));
    }

    [Fact]
    public void A_tile_outside_every_window_takes_the_nearest_unit_left_extra_tiles_are_decarding()
    {
        var out_ = Label.LabelBlocks([Tiles(("reset", 8 * H, 1), ("reset", 19 * H, 12))], Ctx(stage1: Stage1));
        Assert.Equal("re_runs", Seq(out_[0], "reset")[0]); // 08:00 is nearest the 09:00 re-run
        Assert.Equal(4, Label.SubtaskTiles(out_)["re_runs"]);
        Assert.Equal(9, Label.SubtaskTiles(out_)["decarding"]);
    }

    [Fact]
    public void A_plan_held_from_before_is_read_from_its_reset_task_list()
    {
        var held = Stage1 with { UnitWindows = null, Blocks = [new ResetSourceBlock("reset", 9 * H, 10)] };
        var units = Label.ResetUnits(held);
        Assert.Equal(10, units.Count);
        // 30 + 30 of 150 minutes are re-runs.
        Assert.Equal(4, units.Count(u => u.Sub == "re_runs"));
    }

    [Fact]
    public void With_nothing_to_go_on_reset_is_decarding()
    {
        var out_ = Label.LabelBlocks([Tiles(("reset", 9 * H, 3))], Ctx());
        Assert.Equal(["decarding", "decarding", "decarding"], Seq(out_[0], "reset"));
    }

    [Fact]
    public void Maps_each_load_type_to_the_subtask_of_its_own_name()
    {
        (string Name, string Key)[] names =
        [
            ("Ambient", "ambient"), ("Long Life", "long_life"), ("Short Life", "short_life"), ("Freezer", "freezer"), ("Bread", "bread"),
            ("Produce", "produce"), ("D's", "ambient"), ("Freeze to Thaw", "freeze_to_thaw"), ("Liquor", "ambient"),
        ];
        foreach (var (n, k) in names) Assert.Equal(k, Catalogue.LoadSubtaskFor(n));
        Assert.Equal("ambient_last", Catalogue.LoadPartFor("D's"));
        Assert.Equal("ambient", Catalogue.LoadPartFor("Ambient"));
        foreach (var (_, k) in names) Assert.Equal("load_running", Catalogue.SubtaskByKey[k].Group);
    }

    [Fact]
    public void Averages_the_same_weekday_over_the_eight_weeks_before_the_fortnight()
    {
        LoadTypeRow[] types =
        [
            new() { Id = "a", Name = "Ambient", MinutesPerPallet = 19, Priority = 5 },
            new() { Id = "d", Name = "D's", MinutesPerPallet = 5, Priority = 100 },
            new() { Id = "p", Name = "Produce", MinutesPerPallet = 10, Priority = 3 },
        ];
        static DailyLoadRow Row(string date, string type, double qty) => new() { Date = DateOnlyExtensions.ParseIso(date), LoadTypeId = type, Qty = qty };
        DailyLoadRow[] rows =
        [
            // Two Mondays with counts, one with ambient only.
            Row("2026-11-02", "a", 4), Row("2026-11-02", "p", 6), Row("2026-10-26", "a", 2), Row("2026-10-26", "d", 2),
            // Outside the window: the fortnight itself, and nine weeks before.
            Row("2026-11-09", "a", 100), Row("2026-09-07", "a", 100),
        ];
        var byDow = LoadBoard.LoadBoardAverages(types, rows, DateOnlyExtensions.ParseIso("2026-11-09")).ByDow;
        Assert.Equal(
            new Dictionary<string, double> { ["ambient"] = (4 * 19 + 2 * 19) / 2.0, ["produce"] = 30, ["ambient_last"] = 5 },
            byDow[0].Minutes.ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal(new Dictionary<string, double> { ["ambient"] = 5, ["produce"] = 3 }, byDow[0].Priority.ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal(0, byDow[1].Minutes.Size);
    }

    [Fact]
    public void Stretching_or_trimming_a_shift_keeps_the_labels_of_the_tiles_that_remain()
    {
        var prev = new Roster.Engine.Planning.Stage1.PlanShift
        {
            SlotId = "sa-1",
            PlanRole = "SA",
            Start = 9 * H,
            End = 13 * H,
            BreakMinutes = 0,
            UnpaidBreakMinutes = 0,
            Blocks = Label.LabelBlocks([Tiles(("cleaning", 9 * H, 16))], Ctx())[0],
        };
        var grown = Roster.Engine.Planning.Stage1.PlanEdit.ReshapePlanShift(prev, "sa-1", "SA", 8 * H, 13 * H);
        foreach (var b in grown.Blocks)
            if (b.Group == "cleaning") Assert.Equal(prev.Blocks.First(x => x.Time == b.Time).Sub, b.Sub);
            else Assert.Null(b.Sub);
    }
}
