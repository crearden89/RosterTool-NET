// Port of src/lib/subtasks/roster.test.ts. The server labeller's mocked
// context (no load board history, default shares, and one Reset task whose
// saved mapping disagrees with its name) is Day.ContextFor's input here.

using Roster.Engine.Core;
using Roster.Engine.SubTasks;
using static Roster.Engine.Tests.SubTasks.SubTaskTestKit;

namespace Roster.Engine.Tests.SubTasks;

public class RosterTests
{
    private static LabelContext ServerCtx(ResetSource? s1) =>
        Day.ContextFor(Tue, s1, new LoadBoardAverages([]), RosteringRules.Resolve(new Dictionary<string, double>()), [("Shelf sweep", "re_runs")]);

    /// <summary>No held plan to carry labels from.</summary>
    private static readonly IReadOnlyList<CarryShift> NoPlan = [];

    /// <summary>A roster shift: a named person with runs of tiles.</summary>
    private static Shift RosterShift(string id, (string Group, double From, int N)[] runs, bool unfilled = false)
    {
        var blocks = Tiles(runs).Select(b => new ShiftBlock(b.Time, b.Group)).ToList();
        return new Shift
        {
            EmployeeId = id, Name = id, Role = "SA", Start = blocks.Min(b => b.Time), End = blocks.Max(b => b.Time) + 15,
            BreakMinutes = 0, Blocks = blocks, Unfilled = unfilled,
        };
    }

    private static List<Shift> TheRoster() =>
    [
        RosterShift("a", [("load_running", 360, 12), ("break", 540, 2), ("cleaning", 570, 4), ("checkout", 630, 8)]),
        RosterShift("b", [("load_running", 360, 8), ("reset", 480, 6), ("specials", 570, 6)]),
        RosterShift("c", [("checkout", 540, 8), ("specials", 660, 4), ("cleaning", 720, 6)]),
        // An open shift nobody could take still has tiles, and they are labelled too.
        RosterShift("", [("reset", 1080, 4), ("cleaning", 1140, 4)], true),
    ];

    private static readonly ResetSource Stage1 = new()
    {
        StaffOpen = 360,
        StaffClose = 1320,
        ResetTasks = [new("Re-run 1", 60, 420, 600), new("Full decard", 60, 1020, 1320)],
        Blocks = [new ResetSourceBlock("reset", 480, 10)],
    };

    private static int TilesOf(IEnumerable<Shift> shifts, string group) => shifts.Sum(s => s.Blocks.Count(b => b.Group == group));

    [Fact]
    public void Every_tile_of_the_four_groups_gets_a_label_of_its_own_group()
    {
        var before = TheRoster();
        var after = Label.LabelShifts(before, new LabelContext { Date = Tue, Stage1 = Stage1, Rules = RosteringRules.Default });
        // Labels only: people, times and groups exactly as painted.
        Assert.Equal(before.Select(s => s.Blocks.Select(b => $"{b.Time}:{b.Group}")), after.Select(s => s.Blocks.Select(b => $"{b.Time}:{b.Group}")));
        Assert.Equal(before.Select(s => (s.EmployeeId, s.Start, s.End, s.Unfilled)), after.Select(s => (s.EmployeeId, s.Start, s.End, s.Unfilled)));
        var subs = Label.SubtaskTiles(after);
        foreach (var (g, sts) in Catalogue.Subtasks) Assert.Equal(TilesOf(after, g), sts.Sum(st => subs.GetOr(st.Key, 0)));
        foreach (var s in after)
            foreach (var b in s.Blocks)
                if (Catalogue.IsSubtaskGroup(b.Group)) Assert.True(Catalogue.IsSubtaskOf(b.Group, b.Sub), $"{b.Group} {b.Sub}");
                else Assert.Null(b.Sub);
        Assert.False(Label.NeedsLabels(after));
        // The shifts given are left as they were.
        Assert.True(Label.NeedsLabels(before));
    }

    [Fact]
    public void A_roster_held_or_published_before_subtasks_is_labelled_when_read_a_labelled_one_is_left_as_it_is()
    {
        var old = TheRoster();
        Assert.True(Label.NeedsLabels(old));
        // No held plan to carry labels from: the rules label every tile.
        var read = Day.WithDayLabels(NoPlan, old, ServerCtx(Stage1));
        Assert.False(Label.NeedsLabels(read));
        Assert.Equal(TilesOf(old, "reset"), TilesOf(read, "reset"));
        // Read again: already labelled, so nothing is relabelled.
        Assert.Same(read, Day.WithDayLabels(NoPlan, read, ServerCtx(Stage1)));
    }

    [Fact]
    public void A_label_from_another_group_is_never_kept_on_a_tile()
    {
        // Shown: a Cleaning tile carrying a Reset label shows no label at all.
        var rows = Display.BuildRosterRows(
        [
            new RawShift("a", "A", "SA", 540, 615, 0,
            [
                // Its own break tile, so no break is laid over the shift's middle.
                new RawBlock(600, "break"),
                new RawBlock(540, "cleaning", "re_runs"),
                new RawBlock(555, "cleaning", "boh"),
                new RawBlock(570, "checkout", "front_end"),
                new RawBlock(585, "reset", "decarding"),
            ]),
        ]);
        Assert.Equal([null, "boh", null, "decarding", null], rows[0].Blocks.Select(b => b.Sub));
        // Labelled: the wrong label is dropped and the tile labelled by the normal rules.
        var bad = RosterShift("a", [("cleaning", 540, 4)]);
        bad.Blocks[0].Sub = "re_runs";
        var out_ = Label.LabelShifts([bad], new LabelContext { Date = Tue, Rules = RosteringRules.Default });
        Assert.All(out_[0].Blocks, b => Assert.True(Catalogue.IsSubtaskOf("cleaning", b.Sub)));
    }

    [Fact]
    public void The_printed_roster_lists_a_persons_subtasks_once_each_in_the_order_worked()
    {
        Assert.Equal(["PR", "AM", "FE"], Runs.SubtaskRun(
        [
            new DisplayBlock(600, "cleaning", "front_end"),
            new DisplayBlock(360, "load_running", "produce"),
            new DisplayBlock(375, "load_running", "ambient"),
            new DisplayBlock(390, "load_running", "produce"),
            new DisplayBlock(420, "checkout"),
            new DisplayBlock(435, "cleaning", "re_runs"),
        ]));
    }

    [Fact]
    public void Reset_tasks_a_saved_mapping_decides_empty_the_name_does()
    {
        Assert.Equal("re_runs", Catalogue.ResetSubtaskFor("Re-run 2"));
        Assert.Equal("decarding", Catalogue.ResetSubtaskFor("Full decard", null));
        Assert.Equal("re_runs", Catalogue.ResetSubtaskFor("Shelf sweep", "re_runs"));
        Assert.Equal("decarding", Catalogue.ResetSubtaskFor("Re-run 1", "decarding"));
        // A value that is not a Reset sub-task is ignored.
        Assert.Equal("re_runs", Catalogue.ResetSubtaskFor("Re-run 1", "ambient"));
    }

    private static readonly ResetSource ShelfSweep = new()
    {
        StaffOpen = 360,
        StaffClose = 1320,
        ResetTasks = [new("Shelf sweep", 60, 420, 600)],
        Blocks = [new ResetSourceBlock("reset", 480, 4)],
    };

    [Fact]
    public void A_renamed_reset_task_keeps_its_subtask_once_it_has_a_mapping()
    {
        Assert.All(Label.ResetUnits(ShelfSweep), u => Assert.Equal("decarding", u.Sub));
        Assert.All(Label.ResetUnits(ShelfSweep, new JsMap<string, string?>().Set("Shelf sweep", "re_runs")), u => Assert.Equal("re_runs", u.Sub));
    }

    [Fact]
    public void The_server_labeller_reads_the_reset_tasks_saved_mappings()
    {
        var out_ = Day.WithDayLabels(NoPlan, [RosterShift("a", [("reset", 480, 4)])], ServerCtx(ShelfSweep));
        Assert.Equal(["re_runs", "re_runs", "re_runs", "re_runs"], out_[0].Blocks.Select(b => b.Sub));
    }

    [Fact]
    public void Load_types_a_saved_mapping_decides_empty_the_name_does_ds_stay_ambients_last_part()
    {
        Assert.Equal("ambient", Catalogue.LoadPartFor("Chilled pallets"));
        Assert.Equal("short_life", Catalogue.LoadPartFor("Chilled pallets", "short_life"));
        Assert.Equal("ambient_last", Catalogue.LoadPartFor("D's"));
        Assert.Equal("ambient_last", Catalogue.LoadPartFor("D's", ""));
        Assert.Equal("ambient_last", Catalogue.LoadPartFor("Late cages", "ambient_last"));
        Assert.Equal("produce", Catalogue.LoadPartFor("Produce", "nonsense"));
    }

    [Fact]
    public void Renaming_a_mapped_load_type_does_not_change_the_split()
    {
        DailyLoadRow[] rows =
        [
            new() { Date = DateOnlyExtensions.ParseIso("2026-10-13"), LoadTypeId = "t1", Qty = 4 },
            new() { Date = DateOnlyExtensions.ParseIso("2026-10-13"), LoadTypeId = "t2", Qty = 2 },
        ];
        var start = DateOnlyExtensions.ParseIso("2026-11-09");
        static string Shape(LoadBoardAverages a) => string.Join(";", a.ByDow.Select(d =>
            string.Join(",", d.Minutes.Select(kv => $"{kv.Key}={kv.Value}")) + "|" + string.Join(",", d.Priority.Select(kv => $"{kv.Key}={kv.Value}"))));
        var before = LoadBoard.LoadBoardAverages(
            [new() { Id = "t1", Name = "Produce", MinutesPerPallet = 10, Priority = 3 }, new() { Id = "t2", Name = "Bread", MinutesPerPallet = 10, Priority = 100 }],
            rows, start);
        var after = LoadBoard.LoadBoardAverages(
            [
                new() { Id = "t1", Name = "Fruit & veg", MinutesPerPallet = 10, Priority = 3, SubTask = "produce" },
                new() { Id = "t2", Name = "Bakery", MinutesPerPallet = 10, Priority = 100, SubTask = "bread" },
            ],
            rows, start);
        Assert.Equal(Shape(before), Shape(after));
        // Without the mapping the rename would have moved both into Ambient.
        var unmapped = LoadBoard.LoadBoardAverages(
            [new() { Id = "t1", Name = "Fruit & veg", MinutesPerPallet = 10, Priority = 3 }, new() { Id = "t2", Name = "Bakery", MinutesPerPallet = 10, Priority = 100 }],
            rows, start);
        Assert.Equal(new Dictionary<string, double> { ["ambient"] = 60 }, unmapped.ByDow[1].Minutes.ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
