// New: the Plan page's edit mode (DayPlanBlock.tsx's handlers, in DayPlanEditor) and the pure
// half of editPlanDay (PlanDayEdits). The TypeScript app has no tests of its own for these;
// the cases follow the comments in DayPlanBlock.tsx and actions.ts.

using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Plan;

namespace Roster.Web.Tests.Plan;

public class DayPlanEditorTests
{
    private static readonly Stage1Computation Day = new() { HasData = true, StaffOpen = 6 * 60, StaffClose = 20 * 60 + 30 };

    private static PlanShiftComputation Plan() => new()
    {
        HasData = true,
        Shifts =
        [
            PlanEdit.ReshapePlanShift(null, "m1", RoleBadges.Mgr, 6 * 60, 14 * 60),
            PlanEdit.ReshapePlanShift(null, "s1", RoleBadges.Sa, 7 * 60, 10 * 60),
            PlanEdit.ReshapePlanShift(null, "s2", RoleBadges.Sa, 12 * 60, 20 * 60),
        ],
    };

    private static DayPlanEditor Editing(string? tool = null)
    {
        var e = new DayPlanEditor();
        e.Start(Plan());
        if (tool is not null) e.SetTool(tool);
        return e;
    }

    private static PlanShift Row(DayPlanEditor e, string slot) => e.Draft!.Single(s => s.SlotId == slot);

    [Fact]
    public void Painting_inside_a_shift_recolours_the_tile_and_a_sub_task_chip_labels_it()
    {
        var e = Editing("produce");
        e.CellClick(Day, "m1", 7 * 60);
        Assert.Equal(new DisplayBlock(7 * 60, "load_running", "produce"), Row(e, "m1").Blocks.Single(b => b.Time == 7 * 60));
        // A group alone leaves the tile unlabelled (Save labels it by the normal rules).
        e.SetTool("load_running");
        e.CellClick(Day, "m1", 7 * 60);
        Assert.Null(Row(e, "m1").Blocks.Single(b => b.Time == 7 * 60).Sub);
        // Without a tool nothing happens.
        e.SetTool(null);
        e.CellClick(Day, "m1", 8 * 60);
        Assert.Equal("flex", Row(e, "m1").Blocks.Single(b => b.Time == 8 * 60).Group);
    }

    [Fact]
    public void Painting_beyond_a_shift_stretches_it_there_and_paints_only_the_clicked_cell()
    {
        var e = Editing("cleaning");
        e.CellClick(Day, "s1", 11 * 60);
        var s1 = Row(e, "s1");
        Assert.Equal((7 * 60.0, 11 * 60 + 15.0), (s1.Start, s1.End));
        Assert.Equal("cleaning", s1.Blocks.Single(b => b.Time == 11 * 60).Group);
        Assert.Equal("flex", s1.Blocks.Single(b => b.Time == 10 * 60 + 30).Group);
        Assert.Null(e.Error);
    }

    [Fact]
    public void A_stretch_outside_the_staffed_day_is_refused_with_the_reason()
    {
        var e = Editing("cleaning");
        e.CellClick(Day, "s2", 20 * 60 + 30);
        Assert.Equal("SA 12:00-20:45 finishes after the store's staffed day.", e.Error);
        Assert.Equal(20 * 60, Row(e, "s2").End);
    }

    [Fact]
    public void Erasing_the_first_or_last_tile_trims_the_shift_but_not_below_three_hours()
    {
        var e = Editing(PaintTools.Erase);
        e.CellClick(Day, "s2", 12 * 60);
        Assert.Equal(12 * 60 + 15, Row(e, "s2").Start);
        e.CellClick(Day, "s2", 20 * 60 - 15);
        Assert.Equal(20 * 60 - 15, Row(e, "s2").End);
        // s1 is 3 hours: it can't lose a quarter-hour.
        e.CellClick(Day, "s1", 7 * 60);
        Assert.Equal("SA can't be trimmed any further - it must be at least 3 hours long.", e.Error);
        // An erase off the shift does nothing; one in the middle clears the tile.
        e.CellClick(Day, "s1", 11 * 60);
        Assert.Equal((7 * 60.0, 10 * 60.0), (Row(e, "s1").Start, Row(e, "s1").End));
        e.SetTool("cleaning");
        e.CellClick(Day, "m1", 9 * 60);
        e.SetTool(PaintTools.Erase);
        e.CellClick(Day, "m1", 9 * 60);
        Assert.Equal("flex", Row(e, "m1").Blocks.Single(b => b.Time == 9 * 60).Group);
    }

    [Fact]
    public void An_added_shift_is_an_SA_over_the_middle_of_the_day_in_hierarchy_order()
    {
        var e = Editing();
        e.AddShift(Day, "manual-x");
        Assert.Equal(["m1", "s1", "manual-x", "s2"], e.Draft!.Select(s => s.SlotId));
        var added = Row(e, "manual-x");
        Assert.Equal((RoleBadges.Sa, 9 * 60.0, 17 * 60.0), (added.PlanRole, added.Start, added.End));

        var tiny = new DayPlanEditor();
        tiny.Start(Plan());
        tiny.AddShift(new Stage1Computation { StaffOpen = 8 * 60, StaffClose = 10 * 60 }, "manual-y");
        Assert.Equal("The staffed day is too short to add a shift.", tiny.Error);
    }

    [Fact]
    public void The_role_badge_cycles_and_the_shift_is_rebuilt_for_the_role()
    {
        var e = Editing();
        e.CycleRole("s2");
        Assert.Equal(RoleBadges.Jsa, Row(e, "s2").PlanRole);
        e.CycleRole("s2");
        Assert.Equal(RoleBadges.Mgr, Row(e, "s2").PlanRole);
        e.CycleRole("s2");
        Assert.Equal(RoleBadges.Sa, Row(e, "s2").PlanRole);
        e.RemoveShift("s2");
        Assert.DoesNotContain(e.Draft!, s => s.SlotId == "s2");
    }

    [Fact]
    public void Save_is_refused_without_shifts_or_with_a_break_not_as_entitled()
    {
        var e = Editing();
        Assert.Null(e.SaveProblem(Day));
        var brk = Row(e, "s2").Blocks.First(b => b.Group == "break").Time;
        e.SetTool("cleaning");
        e.CellClick(Day, "s2", brk);
        Assert.StartsWith("Break requirements not met: SA 12:00-20:00 has ", e.SaveProblem(Day));
        foreach (var s in e.Draft!.ToList()) e.RemoveShift(s.SlotId);
        Assert.Equal("A day needs at least one shift.", e.SaveProblem(Day));
    }

    [Fact]
    public void The_edits_sent_carry_each_shift_and_its_tiles()
    {
        var e = Editing("re_runs");
        e.CellClick(Day, "m1", 8 * 60);
        var edit = e.Edits().Single(x => x.SlotId == "m1");
        Assert.Equal((RoleBadges.Mgr, 6 * 60.0, 14 * 60.0), (edit.PlanRole, edit.Start, edit.End));
        Assert.Equal(new PlanBlockEdit(8 * 60, "reset", "re_runs"), edit.Blocks.Single(b => b.Time == 8 * 60));
        Assert.Equal("manual-mwwaabbd", DayPlanEditor.ManualSlotId(DateTimeOffset.FromUnixTimeMilliseconds(1795620839881)));
    }

    // ─── editPlanDay's pure half ─────────────────────────────────────────────

    private static PlanSnapshotDay Held() => new() { Date = new DateOnly(2026, 11, 24), Ok = true, Result = Day, Plan = Plan() };

    private static PlanShiftEdit EditOf(PlanShift s, string? role = null, double? start = null, double? end = null) =>
        new(s.SlotId, role ?? s.PlanRole, start ?? s.Start, end ?? s.End, s.Blocks.Select(b => new PlanBlockEdit(b.Time, b.Group, b.Sub)).ToList());

    [Fact]
    public void An_edit_outside_the_rules_is_refused_before_anything_changes()
    {
        var day = Held();
        var shifts = day.Plan!.Shifts;
        var (error, _) = PlanDayEdits.Rebuild(day, [EditOf(shifts[0], start: 5 * 60)]);
        Assert.Equal("MGR 05:00-14:00 starts before the store's staffed day", error);

        var bad = EditOf(shifts[1]) with { Blocks = [new PlanBlockEdit(8 * 60, "cleaning", "re_runs")] };
        (error, _) = PlanDayEdits.Rebuild(day, [bad]);
        Assert.Equal("SA 07:00-10:00: the tile at 08:00 is labelled \"Re-runs\", which is not part of its labour group", error);
        (error, _) = PlanDayEdits.Rebuild(day, []);
        Assert.Equal("A day needs at least one shift.", error);
    }

    [Fact]
    public void Painting_alone_is_a_recolour_and_removals_count_as_changes()
    {
        var day = Held();
        var shifts = day.Plan!.Shifts;
        var painted = EditOf(shifts[0]) with
        {
            Blocks = [.. EditOf(shifts[0]).Blocks.Select(b => b.Time == 7 * 60 ? b with { Group = "admin" } : b)],
        };
        var (error, rebuilt) = PlanDayEdits.Rebuild(day, [painted, EditOf(shifts[2])]);
        Assert.Null(error);
        Assert.Equal(["m1", "s2"], rebuilt!.Shifts.Select(s => s.SlotId));
        Assert.Equal("admin", rebuilt.Shifts[0].Blocks.Single(b => b.Time == 7 * 60).Group);
        Assert.Equal(2, rebuilt.Changed); // one tile, one shift removed
    }

    [Fact]
    public void A_role_changed_without_its_times_keeps_the_held_role_as_the_TypeScript_does()
    {
        var day = Held();
        var s1 = day.Plan!.Shifts[1];
        var (_, rebuilt) = PlanDayEdits.Rebuild(day, [.. day.Plan.Shifts.Select(s => s == s1 ? EditOf(s, role: RoleBadges.Jsa) : EditOf(s))]);
        Assert.Equal(RoleBadges.Sa, rebuilt!.Shifts.Single(s => s.SlotId == "s1").PlanRole);
        // A moved shift is rebuilt in its new role.
        (_, rebuilt) = PlanDayEdits.Rebuild(day, [.. day.Plan.Shifts.Select(s => s == s1 ? EditOf(s, role: RoleBadges.Jsa, end: 10 * 60 + 15) : EditOf(s))]);
        Assert.Equal(RoleBadges.Jsa, rebuilt!.Shifts.Single(s => s.SlotId == "s1").PlanRole);
    }

    [Fact]
    public void An_added_shift_goes_where_the_hierarchy_puts_it_and_breaks_are_checked()
    {
        var day = Held();
        var added = PlanEdit.ReshapePlanShift(null, "manual-1", RoleBadges.Mgr, 10 * 60, 18 * 60);
        var (_, rebuilt) = PlanDayEdits.Rebuild(day, [.. day.Plan!.Shifts.Select(s => EditOf(s)), EditOf(added)]);
        Assert.Equal(["m1", "manual-1", "s1", "s2"], rebuilt!.Shifts.Select(s => s.SlotId));
        Assert.Null(PlanDayEdits.BreakError(rebuilt.Shifts));
        var noBreak = rebuilt.Shifts[0] with { Blocks = [.. rebuilt.Shifts[0].Blocks.Select(b => b.Group == "break" ? b with { Group = "flex" } : b)] };
        Assert.StartsWith("Break requirements not met: MGR 06:00-14:00 has 0m of break tiles", PlanDayEdits.BreakError([noBreak]));
    }
}
