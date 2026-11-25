// Port of src/lib/roster/dayStatus.test.ts.

using Roster.Engine.Core;
using Roster.Engine.RosterOps;

namespace Roster.Engine.Tests.RosterOps;

public class DayStatusTests
{
    private static Shift Shift(bool unfilled = false) => new()
    {
        EmployeeId = unfilled ? "" : "e1", Name = unfilled ? "UNFILLED - needs cover" : "Alex", Role = "SA",
        Start = 420, End = 600, BreakMinutes = 15, Unfilled = unfilled,
    };

    private static RosterDay Day(string[] flags, bool closed = false, bool hasData = true, Shift[]? shifts = null) =>
        new(shifts ?? [Shift()], flags, hasData, closed);

    // Wording the roster gives today (the run-through, 14/11/2026).
    private static readonly string[] Warnings =
    [
        "Extra store assistant 07:00-10:00 so the crew can take its breaks without losing the work - 0.75h of award break falls inside a window that closes at 10:00",
        "2-Carey, Wade 07:45–17:15: 2 quarter-hours with no work (13:45–14:15) that could not be cleared within the rules",
        "0.25h added to existing shifts so the crew can take its breaks without losing the work",
        "Time added so breaks don't cost work: 0.25 h - Manager 11:30–17:30 starts at 11:15 (+15 min), so the work the crew's breaks displace is still done inside its window",
        "Hours issue: 2.12 h over the benchmark (42.25 h planned, 40.13 h benchmark) - Admin tasks past the Admin benchmark 0.05 h, cover 1.50 h, fixed shifts 0.50 h",
    ];
    private const string Open = "Store assistant 07:00–10:00 - external cover may be required";
    private const string Why = "Why: of 14 staff - 9 already rostered today, 3 not available";
    private const string TillGap = "Tills: 0.50h of register time has nobody on it - no one could take the till while the crew took its breaks";

    [Fact]
    public void Is_ok_with_warnings_alone()
    {
        var d = Day(Warnings);
        Assert.False(DayStatus.RosterDayHasError(d));
        Assert.True(DayStatus.RosterDayOk(d));
        var split = DayStatus.SplitRosterFlags(d.Flags);
        Assert.Empty(split.Errors);
        Assert.Equal(Warnings, split.Warnings);
    }

    [Fact]
    public void Has_an_error_for_an_open_shift_by_its_flag_or_by_the_shift_itself()
    {
        Assert.True(DayStatus.RosterDayHasError(Day([Open, Why])));
        // An open shift counts even if its flag were worded differently.
        var silent = Day([], shifts: [Shift(), Shift(true)]);
        Assert.Equal(1, DayStatus.OpenShiftCount(silent));
        Assert.True(DayStatus.RosterDayHasError(silent));
        Assert.False(DayStatus.RosterDayOk(silent));
    }

    [Fact]
    public void Has_an_error_for_a_cover_gap_the_review_grades_as_one()
    {
        Assert.True(DayStatus.RosterDayHasError(Day([TillGap, .. Warnings])));
        Assert.False(DayStatus.RosterDayOk(Day(["No manager rostered 07:00–07:30"])));
    }

    [Fact]
    public void Puts_the_open_shifts_why_line_with_the_errors_warnings_after()
    {
        var split = DayStatus.SplitRosterFlags([Warnings[0], Open, Why, Warnings[1]]);
        Assert.Equal([Open, Why], split.Errors);
        Assert.Equal([Warnings[0], Warnings[1]], split.Warnings);
        // A Why line alone is not an error.
        var alone = DayStatus.SplitRosterFlags([Why]);
        Assert.Empty(alone.Errors);
        Assert.Equal([Why], alone.Warnings);
        Assert.False(DayStatus.RosterDayHasError(Day([Why])));
    }

    [Fact]
    public void Treats_a_closed_day_as_ok_and_a_day_with_no_roster_as_not_ok()
    {
        Assert.True(DayStatus.RosterDayOk(Day([Open], closed: true, hasData: false, shifts: [])));
        Assert.False(DayStatus.RosterDayHasError(Day([Open], closed: true)));
        Assert.False(DayStatus.RosterDayOk(Day([], hasData: false, shifts: [])));
        Assert.False(DayStatus.RosterDayOk((RosterDay?)null));
        Assert.False(DayStatus.RosterDayOk((Roster.Engine.Rostering.Fortnight.RosterComputation?)null));
    }

    [Fact]
    public void Asks_for_a_note_on_cover_only_for_the_days_with_an_error()
    {
        var byDate = new JsMap<string, RosterDay>()
            .Set("2026-11-23", Day(Warnings))
            .Set("2026-11-24", Day([Open, Why]))
            .Set("2026-11-25", Day([TillGap]))
            .Set("2026-11-29", Day([Open], closed: true, hasData: false, shifts: []));
        string[] dates = ["2026-11-23", "2026-11-24", "2026-11-25", "2026-11-26", "2026-11-29"];
        Assert.Equal(["2026-11-24", "2026-11-25"], DayStatus.DaysNeedingCoverNote(byDate, dates));
        Assert.Empty(DayStatus.DaysNeedingCoverNote(new JsMap<string, RosterDay>().Set("2026-11-23", Day(Warnings)), dates));
    }

    [Fact]
    public void Says_which_days_need_the_note_in_plain_words()
    {
        Assert.Equal("1 day has an error (open shifts or cover gaps): 24/11", DayStatus.CoverNoteReason(["2026-11-24"]));
        Assert.Equal("2 days have an error (open shifts or cover gaps): 24/11, 01/12", DayStatus.CoverNoteReason(["2026-11-24", "2026-12-01"]));
        Assert.Equal(
            "6 days have an error (open shifts or cover gaps): 23/11, 24/11, 25/11, 26/11 and 2 more",
            DayStatus.CoverNoteReason(["2026-11-23", "2026-11-24", "2026-11-25", "2026-11-26", "2026-11-27", "2026-11-28"]));
    }
}
