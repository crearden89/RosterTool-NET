// Port of src/lib/stage1/planEdit.test.ts and planShifts.notes.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Stage1;

public class PlanEditTests
{
    private const double H = 60;

    private static List<double> GroupsAt(PlanShift s, string group) => s.Blocks.Where(b => b.Group == group).Select(b => b.Time).ToList();

    // ── reshapePlanShift ──

    // A 06:00-15:30 SA shift with the morning painted as load running.
    private static PlanShift Base()
    {
        var s = PlanEdit.ReshapePlanShift(null, "sa-1", "SA", 6 * H, 15.5 * H);
        return s with { Blocks = s.Blocks.Select(b => b.Group == "flex" && b.Time < 10 * H ? b with { Group = "load_running" } : b).ToList() };
    }

    [Fact]
    public void Builds_a_new_shift_as_flex_with_its_break_placed_and_valid()
    {
        var s = PlanEdit.ReshapePlanShift(null, "sa-1", "SA", 6 * H, 15.5 * H);
        Assert.Equal(9.5 * H / 15, s.Blocks.Count);
        Assert.True(s.BreakMinutes > 0);
        // Every non-break block starts unpainted...
        Assert.All(s.Blocks, b => Assert.True(b.Group is "flex" or "break"));
        // ...and the placed break already satisfies the award timing rules.
        Assert.Null(Breaks.BreakTileError(GroupsAt(s, "break"), s.BreakMinutes, s.Start, s.End));
    }

    [Fact]
    public void Carries_painted_work_that_the_new_span_still_covers()
    {
        var grown = PlanEdit.ReshapePlanShift(Base(), "sa-1", "SA", 6 * H, 17 * H);
        // The 06:00-08:00 load-running tiles survive the resize.
        for (var t = 6 * H; t < 8 * H; t += 15) Assert.Equal("load_running", grown.Blocks.FirstOrDefault(b => b.Time == t)?.Group);
        Assert.Equal(17 * H, grown.End);
    }

    [Fact]
    public void Brings_newly_added_time_in_as_flex_not_as_painted_work()
    {
        var grown = PlanEdit.ReshapePlanShift(Base(), "sa-1", "SA", 6 * H, 17 * H);
        var added = grown.Blocks.Where(b => b.Time >= 15.5 * H).ToList();
        Assert.Equal(1.5 * H / 15, added.Count);
        // Added time is unpainted (or carries the re-placed break) - never work
        // the manager didn't ask for.
        Assert.All(added, b => Assert.True(b.Group is "flex" or "break"));
    }

    [Fact]
    public void Drops_work_that_falls_outside_a_shortened_span()
    {
        var prev = Base();
        var shrunk = PlanEdit.ReshapePlanShift(prev, "sa-1", "SA", 9 * H, 15 * H);
        Assert.Equal(9 * H, shrunk.Blocks[0].Time);
        Assert.Equal(15 * H - 15, shrunk.Blocks[^1].Time);
        // The 06:00-09:00 hours are gone, and the work painted on them with them.
        Assert.DoesNotContain(shrunk.Blocks, b => b.Time < 9 * H);
        // Every load-running tile that survives was one before - none invented,
        // and each was inside the surviving span. (Asserted as a set rather than
        // at a fixed time, because where the break lands is the helper's choice.)
        var wasPainted = GroupsAt(prev, "load_running").ToHashSet();
        Assert.All(GroupsAt(shrunk, "load_running"), t => Assert.True(wasPainted.Contains(t) && t >= 9 * H));
    }

    [Fact]
    public void Recomputes_and_re_places_the_break_for_the_new_length()
    {
        var prev = Base();
        var shrunk = PlanEdit.ReshapePlanShift(prev, "sa-1", "SA", 6 * H, 10 * H);
        // A 4h shift earns a different entitlement from a 9.5h one...
        Assert.NotEqual(prev.BreakMinutes, shrunk.BreakMinutes);
        // ...and whatever it is, the tiles match it and pass the timing rules, so
        // a resize never leaves the day unsaveable.
        Assert.Null(Breaks.BreakTileError(GroupsAt(shrunk, "break"), shrunk.BreakMinutes, shrunk.Start, shrunk.End));
        // Break tiles from the OLD placement are never left stranded.
        Assert.Equal(shrunk.BreakMinutes, GroupsAt(shrunk, "break").Count * 15);
    }

    [Fact]
    public void Keeps_a_manager_shift_on_the_manager_break_template()
    {
        var mgr = PlanEdit.ReshapePlanShift(null, "mgr-open", "MGR", 6 * H, 15.5 * H);
        var sa = PlanEdit.ReshapePlanShift(null, "sa-1", "SA", 6 * H, 15.5 * H);
        Assert.Equal("MGR", mgr.PlanRole);
        // MGR is costed on the ASM template (flat 30 unpaid), SA on the tiered one.
        Assert.Equal(30, mgr.UnpaidBreakMinutes);
        Assert.Equal(30, sa.UnpaidBreakMinutes);
        Assert.NotEqual(sa.BreakMinutes, mgr.BreakMinutes);
    }

    [Fact]
    public void Keeps_a_tiles_sub_task_label_and_drops_it_where_a_break_lands()
    {
        var s = PlanEdit.ReshapePlanShift(null, "sa-1", "SA", 6 * H, 15.5 * H);
        var labelled = s with { Blocks = s.Blocks.Select(b => b with { Group = "reset", Sub = "rerun1" }).ToList() };
        var again = PlanEdit.ReshapePlanShift(labelled, "sa-1", "SA", 6 * H, 15.5 * H);
        Assert.All(again.Blocks, b => Assert.Equal(b.Group == "break" ? null : "rerun1", b.Sub));
    }

    // ── planShiftError ──

    private const double Open = 6 * H;
    private const double Close = 21.5 * H;

    [Fact]
    public void Accepts_an_ordinary_shift_inside_staffed_hours() => Assert.Null(PlanEdit.PlanShiftError(9 * H, 17 * H, Open, Close));

    [Fact]
    public void Rejects_an_inverted_or_zero_length_span()
    {
        Assert.Contains("finish after", PlanEdit.PlanShiftError(17 * H, 9 * H, Open, Close));
        Assert.Contains("finish after", PlanEdit.PlanShiftError(9 * H, 9 * H, Open, Close));
    }

    [Fact]
    public void Rejects_anything_under_the_3_hour_minimum()
    {
        Assert.Contains("at least 3 hours", PlanEdit.PlanShiftError(9 * H, 9 * H + PlanEdit.MinPlanShiftMin - 15, Open, Close));
        Assert.Null(PlanEdit.PlanShiftError(9 * H, 9 * H + PlanEdit.MinPlanShiftMin, Open, Close));
    }

    [Fact]
    public void Rejects_times_off_the_15_minute_grid() => Assert.Contains("15-minute", PlanEdit.PlanShiftError(9 * H + 5, 17 * H, Open, Close));

    [Fact]
    public void Keeps_shifts_inside_the_staffed_day_not_trading_hours()
    {
        Assert.Contains("before the store", PlanEdit.PlanShiftError(5 * H, 12 * H, Open, Close));
        Assert.Contains("after the store", PlanEdit.PlanShiftError(12 * H, 22 * H, Open, Close));
        // Pre-open and post-close work inside staff hours is legitimate.
        Assert.Null(PlanEdit.PlanShiftError(Open, 12 * H, Open, Close));
        Assert.Null(PlanEdit.PlanShiftError(12 * H, Close, Open, Close));
    }

    // ── the order of a day's rows after an edit ──

    // A held day in hierarchy order: managers, then store assistants, then a junior.
    private static List<PlanShift> Held() =>
    [
        PlanEdit.ReshapePlanShift(null, "mgr-open", "MGR", 7 * H, 14 * H),
        PlanEdit.ReshapePlanShift(null, "mgr-close", "MGR", 13 * H, 21 * H),
        PlanEdit.ReshapePlanShift(null, "sa-1", "SA", 7 * H, 10 * H),
        PlanEdit.ReshapePlanShift(null, "sa-2", "SA", 7 * H, 10 * H),
        PlanEdit.ReshapePlanShift(null, "sa-3", "SA", 9 * H, 17 * H),
        PlanEdit.ReshapePlanShift(null, "jsa-1", "JSA", 16 * H, 20 * H),
    ];

    private static List<string> Ids(IEnumerable<PlanShift> shifts) => shifts.Select(s => s.SlotId).ToList();

    [Fact]
    public void Keeps_the_order_the_rows_had_whatever_order_the_edit_sends_them_in()
    {
        var before = Held();
        // Sent sorted by start time (as the save used to sort them), one stretched.
        var sent = Js.Sorted(
            before.Select(s => s.SlotId == "sa-1" ? PlanEdit.ReshapePlanShift(s, s.SlotId, s.PlanRole, 7 * H, 11 * H) : s),
            (a, b) => (a.Start - b.Start).Or(a.End - b.End));
        Assert.NotEqual(Ids(before), Ids(sent));
        Assert.Equal(Ids(before), Ids(PlanEdit.KeepShiftOrder(Ids(before), sent)));
    }

    [Fact]
    public void Drops_a_removed_row_without_moving_the_others()
    {
        var before = Held();
        var sent = before.Where(s => s.SlotId != "sa-2").Reverse().ToList();
        Assert.Equal(["mgr-open", "mgr-close", "sa-1", "sa-3", "jsa-1"], Ids(PlanEdit.KeepShiftOrder(Ids(before), sent)));
    }

    [Fact]
    public void Puts_an_added_row_where_the_hierarchy_order_says()
    {
        var before = Held();
        var sa = PlanEdit.ReshapePlanShift(null, "manual-a", "SA", 8 * H, 16 * H);
        var mgr = PlanEdit.ReshapePlanShift(null, "manual-b", "MGR", 10 * H, 18 * H);
        var jsa = PlanEdit.ReshapePlanShift(null, "manual-c", "JSA", 17 * H, 21 * H);
        // An assistant starting 08:00 goes after the 07:00 assistants and before the 09:00 one.
        Assert.Equal(["mgr-open", "mgr-close", "sa-1", "sa-2", "manual-a", "sa-3", "jsa-1"], Ids(PlanEdit.PlaceNewShift(before, sa)));
        // Saved, the added rows land in the same places.
        var saved = PlanEdit.KeepShiftOrder(Ids(before), [jsa, .. before, mgr, sa]);
        Assert.Equal(["mgr-open", "manual-b", "mgr-close", "sa-1", "sa-2", "manual-a", "sa-3", "jsa-1", "manual-c"], Ids(saved));
    }

    [Fact]
    public void Gives_the_same_order_on_save_as_the_page_showed_before_it()
    {
        var before = Held();
        var draft = PlanEdit.PlaceNewShift(before, PlanEdit.ReshapePlanShift(null, "manual-a", "SA", 12 * H, 20 * H));
        Assert.Equal(Ids(draft), Ids(PlanEdit.KeepShiftOrder(Ids(before), draft)));
    }
}

public class PlannerNotesTests
{
    // The planner's notes as they stand on the published shifts (review RC-29).
    private static PlanShift Shift(string slotId, string planRole, double start, double end) => new()
    {
        SlotId = slotId,
        PlanRole = planRole,
        Start = start,
        End = end,
        BreakMinutes = 15,
        UnpaidBreakMinutes = 0,
        Blocks = [],
    };

    private static string Body(string from, string to, string h = "1.00") =>
        $"Extra body {from}-{to} so the crew can take its breaks without losing the work - {h}h of award break falls inside a window that closes at 10:00";

    [Fact]
    public void Describes_an_extra_body_as_the_shift_it_became()
    {
        // Added as a store assistant 07:00-10:00, published as a junior 07:15-10:00.
        Assert.Equal(
            ["Extra junior for the crew's breaks: 07:15–10:00, because 1.00 h of breaks fall in work that has to be finished by 10:00"],
            PlanShifts.PlannerNotes([Body("07:00", "10:00")], [Shift("sa-cover-1", "JSA", 435, 600)]));
    }

    [Fact]
    public void Says_two_people_added_at_the_same_times_are_two_in_one_line()
    {
        // G049 4/12/2026: two extra assistants 06:00-09:00 used to read as the same note written twice.
        List<PlanShift> covers = [Shift("sa-cover-1", "SA", 360, 540), Shift("sa-cover-2", "SA", 360, 540)];
        Assert.Equal(
            ["2 extra store assistants for the crew's breaks: both 06:00–09:00, because 3.25 h of breaks fall in work that has to be finished by 10:00"],
            PlanShifts.PlannerNotes([Body("06:00", "09:00", "3.00"), Body("06:00", "09:00", "3.25")], covers));
        // Two people at different times are two lines, each its own shift.
        List<PlanShift> apart = [Shift("sa-cover-1", "SA", 360, 540), Shift("sa-cover-2", "SA", 420, 600)];
        Assert.Equal(
            [
                "Extra store assistant for the crew's breaks: 06:00–09:00, because 1.00 h of breaks fall in work that has to be finished by 10:00",
                "Extra store assistant for the crew's breaks: 07:00–10:00, because 1.00 h of breaks fall in work that has to be finished by 10:00",
            ],
            PlanShifts.PlannerNotes([Body("06:00", "09:00"), Body("07:00", "10:00")], apart));
    }

    [Fact]
    public void Names_time_given_to_breaks_as_one_family_each_saying_which_kind_it_is()
    {
        Assert.Equal(
            [
                "Longer shifts for the crew's breaks: 0.75 h added to existing shifts, because breaks fall in work that has to be finished in its window",
                "Longer shifts for the crew's breaks at the tills: 0.50 h added so the tills stay manned while people take their breaks",
            ],
            PlanShifts.PlannerNotes(
                [
                    "0.75h added to existing shifts so the crew can take its breaks without losing the work",
                    "0.50h added so the tills stay manned while the crew takes its breaks",
                ],
                []));
    }

    [Fact]
    public void Drops_an_extra_body_that_no_longer_exists_or_covers_no_break()
    {
        Assert.Empty(PlanShifts.PlannerNotes([Body("07:00", "10:00")], [Shift("sa-1", "SA", 420, 600)]));
        Assert.Empty(PlanShifts.PlannerNotes([Body("07:00", "10:00", "0.00")], [Shift("sa-cover-1", "SA", 420, 600)]));
    }

    [Fact]
    public void Leaves_out_the_notes_the_day_checker_re_reads_from_the_finished_shifts()
    {
        const string kept = "Clearing blank time hit its work limit and took a shortcut";
        Assert.Equal(
            [kept],
            PlanShifts.PlannerNotes(
                [
                    "0.75h planned above the curve - 3h-minimum shifts and the ≥2-person close-out escort",
                    "Only 1 person planned 20:00–20:30 - below the 2-person floor (check the benchmark demand)",
                    "1.00h of shift added so nobody works alone - the plan has one person's work there",
                    "0.50h of break falls inside a window that closes at 10:00, and the day is too short there to add a 3.00h shift - that work will be planned short",
                    kept,
                ],
                []));
    }
}
