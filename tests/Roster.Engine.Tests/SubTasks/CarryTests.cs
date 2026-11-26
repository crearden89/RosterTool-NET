// Port of src/lib/subtasks/carry.test.ts. The server labeller's mocked context
// (no load board history, default shares, no Reset mappings) is
// Day.ContextFor's input here.

using Roster.Engine.Core;
using Roster.Engine.SubTasks;
using static Roster.Engine.Tests.SubTasks.SubTaskTestKit;

namespace Roster.Engine.Tests.SubTasks;

public class CarryTests
{
    /// <summary>A roster shift held by `who`.</summary>
    private static Shift Held(string who, params (string Group, double From, int N)[] runs)
    {
        var blocks = Tiles(runs).Select(b => new ShiftBlock(b.Time, b.Group)).ToList();
        return new Shift { EmployeeId = who, Name = who, Role = "SA", Start = blocks.Min(b => b.Time), End = blocks.Max(b => b.Time) + 15, Blocks = blocks };
    }

    /// <summary>A plan shift: its times and tiles, labelled as given by time.</summary>
    private static CarryShift PlanShift((string Group, double From, int N)[] runs, params (double Time, string Sub)[] labels)
    {
        var at = labels.ToDictionary(l => l.Time, l => l.Sub);
        var blocks = Tiles(runs).Select(b => at.TryGetValue(b.Time, out var sub) ? b with { Sub = sub } : b).ToList();
        return new CarryShift(blocks.Min(b => b.Time), blocks.Max(b => b.Time) + 15, blocks);
    }

    private static string? SubAt(CarryShift s, double t) => s.Blocks.FirstOrDefault(b => b.Time == t)?.Sub;
    private static string? SubAt(Shift s, double t) => s.Blocks.FirstOrDefault(b => b.Time == t)?.Sub;

    private static string Shape(Shift s) =>
        $"{s.EmployeeId}|{s.Name}|{s.Role}|{s.Start}|{s.End}|{s.BreakMinutes}|{s.Unfilled}|{s.Locked}|" + string.Join(",", s.Blocks.Select(b => $"{b.Time}:{b.Group}:{b.Sub}"));

    private static readonly List<CarryShift> Plan =
    [
        PlanShift([("load_running", 360, 8), ("break", 480, 2), ("reset", 510, 6)], (360, "bread"), (375, "bread"), (510, "re_runs")),
        PlanShift([("checkout", 540, 8), ("cleaning", 660, 4)], (660, "boh")),
    ];

    [Fact]
    public void Keeps_the_label_where_the_shift_the_quarter_hour_and_the_group_are_the_plans()
    {
        List<Shift> roster =
        [
            Held("a", ("load_running", 360, 8), ("break", 480, 2), ("reset", 510, 6)),
            Held("b", ("checkout", 540, 8), ("cleaning", 660, 4)),
        ];
        var out_ = Carry.CarryLabels(Plan, roster);
        Assert.Equal(new string?[] { "bread", "bread", "re_runs", "boh" }, new[] { SubAt(out_[0], 360), SubAt(out_[0], 375), SubAt(out_[0], 510), SubAt(out_[1], 660) });
        // Tiles the plan left to the rules stay unlabelled, for the rules.
        Assert.Null(SubAt(out_[0], 390));
        // Labels only: people, times and groups exactly as the roster has them.
        Assert.Equal(roster.Select(Shape), Carry.WithoutLabels(out_).Select(Shape));
        Assert.All(roster, s => Assert.All(s.Blocks, b => Assert.Null(b.Sub)));
    }

    [Fact]
    public void Leaves_a_tile_the_roster_changed_or_added_to_the_rules()
    {
        List<Shift> roster =
        [
            // 06:00 is Reset now, not Load Running; 08:00 lost its break to work.
            Held("a", ("reset", 360, 1), ("load_running", 375, 7), ("load_running", 480, 2), ("reset", 510, 6)),
            // A quarter-hour added so a break doesn't cost work: a shift the roster grew.
            Held("b", ("cleaning", 525, 1), ("checkout", 540, 8), ("cleaning", 660, 4)),
        ];
        var out_ = Carry.CarryLabels(Plan, roster);
        Assert.Null(SubAt(out_[0], 360));
        Assert.Equal("bread", SubAt(out_[0], 375));
        Assert.Null(SubAt(out_[0], 480));
        Assert.Null(SubAt(out_[1], 525));
        Assert.Equal("boh", SubAt(out_[1], 660));
    }

    [Fact]
    public void Follows_the_tile_when_a_shift_changes_hands()
    {
        // The plan's first shift is now held by "b" and the second by "a".
        List<Shift> roster =
        [
            Held("a", ("checkout", 540, 8), ("cleaning", 660, 4)),
            Held("b", ("load_running", 360, 8), ("break", 480, 2), ("reset", 510, 6)),
        ];
        Assert.Equal([(0, 1), (1, 0)], Carry.ShiftPairs(Plan, roster.Select(CarryShift.Of).ToList()).Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key));
        var out_ = Carry.CarryLabels(Plan, roster);
        Assert.Equal("b", out_[1].EmployeeId);
        Assert.Equal(new string?[] { "bread", "re_runs", "boh" }, new[] { SubAt(out_[1], 360), SubAt(out_[1], 510), SubAt(out_[0], 660) });
    }

    [Fact]
    public void Pairs_shifts_of_the_same_times_by_their_tiles_and_a_trimmed_shift_with_its_own()
    {
        List<CarryShift> twins =
        [
            PlanShift([("load_running", 360, 4), ("reset", 420, 4)], (360, "produce"), (375, "long_life")),
            PlanShift([("reset", 360, 4), ("load_running", 420, 4)], (420, "freezer")),
        ];
        // The two shifts of 06:00-08:00 held the other way round, and the first
        // cut by a quarter-hour at its head (to fill a break's gap elsewhere).
        List<Shift> roster = [Held("x", ("load_running", 375, 3), ("reset", 420, 4)), Held("y", ("reset", 360, 4), ("load_running", 420, 4))];
        Assert.Equal([(0, 0), (1, 1)], Carry.ShiftPairs(twins, roster.Select(CarryShift.Of).ToList()).Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key));
        var out_ = Carry.CarryLabels(twins, roster);
        Assert.Equal("freezer", SubAt(out_[1], 420));
        Assert.Null(SubAt(out_[1], 360));
        // The trimmed shift's tiles keep theirs.
        Assert.Equal("long_life", SubAt(out_[0], 375));
    }

    [Fact]
    public void Compares_labels_tile_for_tile_a_label_of_another_group_counting_as_none()
    {
        List<CarryShift> a = [PlanShift([("cleaning", 600, 2)], (600, "boh"))];
        Assert.True(Carry.SameLabels(a, a));
        Assert.False(Carry.SameLabels(a, Carry.WithoutLabels(a)));
        List<CarryShift> wrong = [PlanShift([("cleaning", 600, 2)], (600, "re_runs"))];
        Assert.True(Carry.SameLabels(wrong, Carry.WithoutLabels(wrong)));
    }

    // The roster build's own path: the plan's labels from the rules, or relabelled.
    private static readonly ResetSource Stage1 = new()
    {
        StaffOpen = 360,
        StaffClose = 1320,
        ResetTasks = [new("Re-run 1", 60, 420, 600), new("Full decard", 90, 420, 1320)],
        Blocks = [new ResetSourceBlock("reset", 480, 10)],
    };

    private static LabelContext ServerCtx() => Day.ContextFor(Tue, Stage1, new LoadBoardAverages([]), RosteringRules.Default, []);

    private static List<CarryShift> PlanTiles() =>
    [
        PlanShift([("load_running", 360, 12), ("break", 540, 2), ("cleaning", 570, 4), ("checkout", 630, 8)]),
        PlanShift([("load_running", 360, 8), ("reset", 480, 6), ("specials", 570, 6)]),
        PlanShift([("load_running", 390, 6), ("reset", 480, 4), ("cleaning", 600, 4)]),
    ];

    private static List<CarryShift> LabelPlanDay(List<CarryShift> shifts) => Label.LabelShifts(shifts, ServerCtx(), s => s.Blocks, (s, b) => s with { Blocks = b });

    // The roster keeps the first two shifts, held the other way round, and paints
    // the third afresh: its 07:30 tile is Reset now.
    private static List<Shift> RosterOf() =>
    [
        Held("b", ("load_running", 360, 8), ("reset", 480, 6), ("specials", 570, 6)),
        Held("a", ("load_running", 360, 12), ("break", 540, 2), ("cleaning", 570, 4), ("checkout", 630, 8)),
        Held("c", ("reset", 390, 1), ("load_running", 405, 5), ("reset", 480, 4), ("cleaning", 600, 4)),
    ];

    // The roster build carries the plan's labels (owner, 15/11 and 26/11/2026).
    [Fact]
    public void Carries_them_on_every_day_from_a_plan_the_rules_labelled_as_well_as_an_edited_one()
    {
        var plan = LabelPlanDay(PlanTiles());
        var roster = RosterOf();
        var out_ = Day.LabelRosterDay(plan, roster, ServerCtx());
        // "a" (the plan's first shift) and "b" (its second), kept whole: every
        // tile has the plan's label.
        foreach (var (r, p) in new[] { (1, 0), (0, 1) })
            foreach (var b in out_[r].Blocks) Assert.True(SubAt(plan[p], b.Time) == b.Sub, $"{out_[r].EmployeeId} {b.Time}");
        // "c" was painted afresh at 07:30: that tile is labelled by the rules, its
        // unchanged tiles keep the plan's.
        foreach (var b in out_[2].Blocks)
            if (b.Time != 390) Assert.True(SubAt(plan[2], b.Time) == b.Sub, $"c {b.Time}");
        Assert.True(Catalogue.IsSubtaskOf("reset", SubAt(out_[2], 390)));
        Assert.False(Label.NeedsLabels(out_));
        // Labels only: people, times and groups exactly as the roster has them.
        Assert.Equal(roster.Select(Shape), Carry.WithoutLabels(out_).Select(Shape));
        // The rules alone, handing sub-tasks out over the roster's own order,
        // label these tiles otherwise: this day is one the carry changes.
        Assert.NotEqual(Label.LabelShifts(roster, ServerCtx()).Select(Shape), out_.Select(Shape));
    }

    [Fact]
    public void Keeps_a_relabel_on_the_tiles_the_roster_kept()
    {
        var plan = LabelPlanDay(PlanTiles());
        // The manager's relabels on the Plan page: a Reset tile from Decarding to
        // Re-runs, and two Load Running tiles of the first shift to Bread.
        var resetAt = plan[1].Blocks.First(b => b.Group == "reset" && b.Sub == "decarding").Time;
        var toBread = plan[0].Blocks.Where(b => b.Group == "load_running" && b.Sub != "bread").Take(2).Select(b => b.Time).ToList();
        Assert.Equal(2, toBread.Count);
        plan[1] = plan[1] with { Blocks = plan[1].Blocks.Select(b => b.Time == resetAt ? b with { Sub = "re_runs" } : b).ToList() };
        plan[0] = plan[0] with { Blocks = plan[0].Blocks.Select(b => toBread.Contains(b.Time) ? b with { Sub = "bread" } : b).ToList() };

        var out_ = Day.LabelRosterDay(plan, RosterOf(), ServerCtx());
        // On "a" (the plan's first shift) and "b" (its second), kept whole: every
        // tile has the plan's label, the relabels included.
        Assert.Equal(["bread", "bread", "re_runs"], toBread.Select(t => SubAt(out_[1], t)).Append(SubAt(out_[0], resetAt)));
        foreach (var (r, p) in new[] { (1, 0), (0, 1) })
            foreach (var b in out_[r].Blocks) Assert.Equal(SubAt(plan[p], b.Time), b.Sub);
        // "c" was painted afresh at 07:30: that tile is labelled by the rules, its
        // unchanged tiles keep the plan's.
        foreach (var b in out_[2].Blocks)
            if (b.Time != 390) Assert.Equal(SubAt(plan[2], b.Time), b.Sub);
        Assert.True(Catalogue.IsSubtaskOf("reset", SubAt(out_[2], 390)));
        Assert.False(Label.NeedsLabels(out_));
    }
}
