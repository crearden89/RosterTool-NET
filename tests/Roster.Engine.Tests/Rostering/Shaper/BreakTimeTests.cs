// Port of src/lib/stage2/breakTime.test.ts.

using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Shaper;

public class BreakTimeTests
{
    private const double H = 60;

    private static readonly BreakTimeLimits Lim = new()
    {
        StaffOpen = 6 * H,
        StaffClose = 17 * H + 30,
        MaxClock = new Dictionary<string, double> { ["MGR"] = 9 * H + 30, ["SA"] = 9 * H + 30, ["JSA"] = 5 * H },
        IsSchoolDay = false,
        BudgetMin = 60,
    };

    // A stand-in painter: Admin owed 14:30-15:00 can be done only while the
    // opening manager is still on, so each quarter-hour the opener stays past
    // 14:30 saves one; any other change saves nothing.
    private static ScoredPaint<List<PlanSlot>, BreakTimeScore> PaintFor(List<PlanSlot> slots)
    {
        var opener = slots.First(s => s.Id == "mgr-open");
        var lost = Math.Max(0, 2 - Math.Max(0, (opener.End - 14 * H - 30) / 15));
        return new(slots, new BreakTimeScore(lost, lost != 0 ? [15 * H + 45] : [], 0, 0));
    }

    private static List<PlanSlot> Day() =>
    [
        new("mgr-open", "MGR", 8 * H + 30, 14 * H + 30),
        new("mgr-close", "MGR", 11 * H + 30, 17 * H + 30),
        new("sa-2", "SA", 8 * H + 30, 13 * H + 45),
        new("jsa-1", "JSA", 16 * H, 17 * H),
        new("fixed-1", "SA", 9 * H, 14 * H),
    ];

    [Fact]
    public void Keeps_the_manager_who_can_do_the_work_on_by_the_least_time_that_saves_it()
    {
        var r = BreakTime.AddTimeForBreaks(Day(), Lim, PaintFor, PaintFor(Day()));
        Assert.Equal(0, r.Score.Lost);
        Assert.Equal(30, r.AddedMin);
        var opener = r.Slots.First(s => s.Id == "mgr-open");
        Assert.Equal((8 * H + 30, 15 * H), (opener.Start, opener.End));
        var note = BreakTime.BreakTimeNote(r.Changes)!;
        // It says what the shift is now, not what it was.
        Assert.Equal(
            "Longer shift so a break doesn't cost work: 0.50 h - Manager now 08:30–15:00 (finishes 30 minutes later), so the work a break would push out is still done in its window",
            note);
        // A note, never an error (severityOf: see the review checks' tests) -
        // and the hours check reads the time back.
        Assert.Equal(0.5, BreakTime.BreakTimeAdded(["something else", note]));
        // Plans held before the rewording are read the same.
        Assert.Equal(0.25, BreakTime.BreakTimeAdded(
        [
            "Time added so breaks don't cost work: 0.25 h - Manager 11:30–17:30 starts at 11:15 (+15 min), so the work the crew's breaks displace is still done inside its window",
        ]));
    }

    [Fact]
    public void Adds_nothing_when_nothing_is_lost_when_the_setting_is_0_or_when_no_change_helps()
    {
        var none = new ScoredPaint<List<PlanSlot>, BreakTimeScore>(Day(), new BreakTimeScore(0, [], 0, 0));
        Assert.Empty(BreakTime.AddTimeForBreaks(Day(), Lim, PaintFor, none).Changes);
        Assert.Empty(BreakTime.AddTimeForBreaks(Day(), Lim with { BudgetMin = 0 }, PaintFor, PaintFor(Day())).Changes);
        static ScoredPaint<List<PlanSlot>, BreakTimeScore> Stuck(List<PlanSlot> slots) =>
            new(slots, new BreakTimeScore(2, [15 * H + 45], 0, 0));
        Assert.Empty(BreakTime.AddTimeForBreaks(Day(), Lim, Stuck, Stuck(Day())).Changes);
    }

    [Fact]
    public void Keeps_the_shift_rules_fixed_shifts_the_staffed_day_the_longest_shift_a_juniors_hours()
    {
        var tried = new List<List<PlanSlot>>();
        ScoredPaint<List<PlanSlot>, BreakTimeScore> Spy(List<PlanSlot> slots)
        {
            tried.Add(slots);
            return new(slots, new BreakTimeScore(2, [15 * H], 0, 0));
        }
        var maxClock = new Dictionary<string, double>(Lim.MaxClock) { ["MGR"] = 6 * H + 15 };
        BreakTime.AddTimeForBreaks(Day(), Lim with { MaxClock = maxClock, IsSchoolDay = true }, Spy, Spy(Day()));
        tried.RemoveAt(0);
        foreach (var slots in tried)
        {
            var fix = slots.First(s => s.Id == "fixed-1");
            Assert.Equal((9 * H, 14 * H), (fix.Start, fix.End));
            var was = Day();
            for (var i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s.Start == was[i].Start && s.End == was[i].End) continue;
                Assert.True(s.Start >= Lim.StaffOpen);
                Assert.True(s.End <= Lim.StaffClose);
                if (s.Role == "MGR") Assert.True(s.End - s.Start <= 6 * H + 15);
                if (s.Role == "JSA")
                {
                    Assert.True(s.Start >= 16 * H);
                    Assert.True(s.End - s.Start <= 5 * H);
                }
            }
        }
        Assert.NotEmpty(tried);
    }

    [Fact]
    public void Never_takes_a_change_that_adds_an_error()
    {
        static ScoredPaint<List<PlanSlot>, BreakTimeScore> Worse(List<PlanSlot> slots)
        {
            var r = PaintFor(slots);
            return r with { Score = r.Score with { Errors = r.Score.Lost < 2 ? 1 : 0 } };
        }
        Assert.Empty(BreakTime.AddTimeForBreaks(Day(), Lim, Worse, Worse(Day())).Changes);
    }

    [Fact]
    public void The_notes_name_every_shift_lengthened_as_it_is_now_and_how_it_changed()
    {
        var note = BreakTime.BreakTimeNote(
        [
            new BreakTimeChange("mgr-close", "MGR", new ShiftTimes(11 * H + 30, 17 * H + 30), new ShiftTimes(11 * H + 15, 17 * H + 30)),
            new BreakTimeChange("sa-2", "SA", new ShiftTimes(7 * H, 12 * H), new ShiftTimes(6 * H + 45, 12 * H + 15)),
        ])!;
        Assert.Equal(
            "Longer shifts so breaks don't cost work: 0.75 h - Manager now 11:15–17:30 (starts 15 minutes earlier); Store assistant now 06:45–12:15 (starts 15 minutes earlier and finishes 15 minutes later), so the work a break would push out is still done in its window",
            note);
        Assert.Equal(0.75, BreakTime.BreakTimeAdded([note]));
    }

    [Fact]
    public void On_the_roster_the_notes_name_the_person_and_are_read_back_apart_from_the_plans()
    {
        var note = BreakTime.RosterBreakTimeNote([new RosterBreakTimeChange("Sam", new ShiftTimes(7 * H, 12 * H), new ShiftTimes(7 * H, 12 * H + 30))])!;
        Assert.Equal(
            "Longer shift on the roster so a break doesn't cost work: 0.50 h - Sam now 07:00–12:30 (finishes 30 minutes later), so the work a break would push out is still done in its window",
            note);
        Assert.Equal(0.5, BreakTime.RosterBreakTimeAdded([note]));
        Assert.Equal(0, BreakTime.BreakTimeAdded([note]));
        Assert.Equal(0.25, BreakTime.RosterBreakTimeAdded(
            ["Time added on the roster so breaks don't cost work: 0.25 h - Sam 07:00–12:00 kept on to 12:15 (+15 min), so ..."]));
    }

    [Fact]
    public void The_notes_say_lengths_in_plain_words()
    {
        Assert.Equal("15 minutes", BreakTime.MinutesInWords(15));
        Assert.Equal("1 hour", BreakTime.MinutesInWords(60));
        Assert.Equal("1 hour 30 minutes", BreakTime.MinutesInWords(90));
    }
}
