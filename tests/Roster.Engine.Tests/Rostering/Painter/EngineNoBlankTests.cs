// Port of src/lib/stage2/engine.noblank.test.ts.
// The no-blank pass (Roster Engine Review 07/11/2026, RC1). Blank tiles are
// cleared by cutting shifts around the work - but never by breaking a rule:
// nobody left alone, a manager always on, a junior's three adults kept,
// breaks legal, fixed rosters untouched. What cannot be cleared within the
// rules is returned to be flagged.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;
using static Roster.Engine.Tests.Rostering.Painter.PaintFixtures;

namespace Roster.Engine.Tests.Rostering.Painter;

public class EngineNoBlankTests
{
    // shift(role, start, end, over) with the times in HOURS.
    private static PaintableShift Sh(string role, double start, double end, bool fix = false, params string[] restricted) =>
        Shift(role, start * H, end * H, fix, false, restricted);

    /// <summary>The guarantees every painted day keeps, whatever else happens.</summary>
    private static void ExpectWithinRules(List<PaintableShift> shifts, PaintResult<PaintableShift> result)
    {
        Assert.Empty(Coverage.CoverIssues(shifts));
        LegalBreaks(shifts);
        foreach (var s in shifts)
        {
            Assert.True(s.End - s.Start >= 3 * H);
            var blanks = s.Blocks.Where(b => b.Group == "flex").Select(b => b.Time).Order().ToList();
            var reported = (result.Blanks.Find(b => ReferenceEquals(b.Shift, s))?.Times ?? []).Order().ToList();
            Assert.Equal(blanks, reported);
        }
    }

    // Who may do what, and when: admin until `adminEnd`, reset until `resetEnd`,
    // everything else (tills, cleaning, specials) all day.
    private static Func<double, IReadOnlyList<string>?> Windows(double adminEnd, double resetEnd = 24) =>
        t => [
            "checkout",
            "cleaning",
            "specials",
            .. t < adminEnd * H ? new[] { "admin" } : [],
            .. t < resetEnd * H ? new[] { "reset" } : [],
        ];

    [Fact]
    public void Keeps_someone_on_rather_than_leave_a_colleague_alone_and_reports_the_blank()
    {
        // The assistant's cleaning stops at 13:00 and they can't do the manager's
        // admin, so their last hour is empty - but sending them home leaves the
        // manager alone.
        var sa = Sh("SA", 9, 14);
        var shifts = new List<PaintableShift> { Sh("ASM", 9, 14), sa };
        var result = Paint(
            shifts,
            Demand((9, 14, G(("admin", 1))), (9, 13, G(("cleaning", 1)))),
            t => t < 13 * H ? ["admin", "cleaning"] : ["admin"]);
        Assert.Equal([14 * H, 14 * H], shifts.Select(s => s.End));
        Assert.NotEmpty(result.Blanks);
        ExpectWithinRules(shifts, result);
    }

    [Fact]
    public void Keeps_the_only_manager_on_and_sends_a_store_assistant_home_instead()
    {
        // Two tills all day for the assistants; the manager's admin window shuts
        // at 13:00, leaving them idle - but they are the only manager. They take
        // an assistant's till instead, and that assistant goes home early.
        var asm = Sh("ASM", 8, 14);
        var shifts = new List<PaintableShift> { asm, Sh("SA", 8, 14), Sh("SA", 8, 14) };
        var result = Paint(shifts, Demand((8, 14, G(("checkout", 2))), (8, 13, G(("admin", 1)))), Windows(13));
        Assert.Equal(14 * H, asm.End);
        Assert.True(shifts.Skip(1).Min(s => s.End) < 14 * H);
        Assert.Empty(result.Blanks);
        ExpectWithinRules(shifts, result);
    }

    [Fact]
    public void Keeps_three_adults_with_a_junior_and_reports_what_that_leaves_blank()
    {
        // As above, but a junior is on from 17:00 on a three-hour shift (too short
        // to trim). Sending any adult home leaves the junior with two.
        var shifts = new List<PaintableShift> { Sh("ASM", 14, 20), Sh("SA", 14, 20), Sh("SA", 14, 20), Sh("JSA", 17, 20) };
        var result = Paint(
            shifts,
            Demand((14, 20, G(("checkout", 2))), (14, 19, G(("admin", 1))), (17, 20, G(("cleaning", 1)))),
            Windows(19));
        Assert.Equal([20 * H, 20 * H, 20 * H, 20 * H], shifts.Select(s => s.End));
        Assert.NotEmpty(result.Blanks);
        ExpectWithinRules(shifts, result);
    }

    [Fact]
    public void Never_changes_a_fixed_rosters_times()
    {
        var fix = Sh("SA", 8, 14, fix: true);
        var shifts = new List<PaintableShift> { Sh("ASM", 8, 16), fix, Sh("SA", 8, 16) };
        var result = Paint(shifts, Demand((8, 12, G(("cleaning", 3))), (12, 16, G(("cleaning", 2)))));
        Assert.Equal([8 * H, 14 * H], new[] { fix.Start, fix.End });
        ExpectWithinRules(shifts, result);
    }

    [Fact]
    public void Gives_a_shorter_shift_its_shorter_break_and_the_work_the_longer_one_pushed_out_comes_back()
    {
        // The owner's example (U5): 06:00-10:30 with nothing to do after 10:00
        // becomes 06:00-10:00 - a 15-minute break instead of 30, and the reset the
        // longer break had pushed out is worked in the minutes it frees.
        var sa = Sh("SA", 6, 10.5, restricted: "specials");
        var shifts = new List<PaintableShift> { Sh("ASM", 6, 14), sa, Sh("SA", 6, 14, restricted: "reset") };
        var result = Paint(
            shifts,
            Demand((6, 14, G(("admin", 1), ("specials", 1))), (6, 10, G(("reset", 1)))),
            Windows(24, 10));
        Assert.Equal([6 * H, 10 * H], new[] { sa.Start, sa.End });
        Assert.Single(sa.Blocks, b => b.Group == "break");
        Assert.Equal(15, Count(sa, "reset"));
        Assert.DoesNotContain(sa.Blocks, b => b.Group == "flex");
        ExpectWithinRules(shifts, result);
    }

    [Fact]
    public void Slides_a_break_later_rather_than_refuse_to_trim_an_idle_start()
    {
        // Nothing for this assistant before 07:30, so the break was placed at
        // 07:00 - the first legal minute. Trimming the idle start brings the first
        // hour up to the break; the break moves along instead of the trim being
        // refused.
        var sa = Sh("SA", 6, 12, restricted: "specials");
        var shifts = new List<PaintableShift> { Sh("ASM", 6, 12), sa, Sh("SA", 6, 12, restricted: "reset") };
        var result = Paint(shifts, Demand((6, 12, G(("admin", 1), ("specials", 1))), (7.5, 12, G(("reset", 1)))));
        Assert.True(sa.Start > 6 * H);
        Assert.DoesNotContain(sa.Blocks, b => b.Group == "flex");
        ExpectWithinRules(shifts, result);
    }

    [Fact]
    public void Sends_two_idle_closers_home_together_when_nothing_is_owed_at_that_minute()
    {
        // Trimming either alone would leave the other locking up alone; trimming
        // both leaves nobody, at minutes with no work.
        var shifts = new List<PaintableShift> { Sh("ASM", 12, 20.25), Sh("SA", 12, 20.25) };
        var result = Paint(shifts, Demand((12, 19, G(("cleaning", 2)))));
        Assert.Equal(shifts[0].End, shifts[1].End);
        Assert.True(shifts[0].End < 20.25 * H);
        Assert.Empty(result.Blanks);
        ExpectWithinRules(shifts, result);
    }

    // ── work no tile carries is reported, not lost in silence ──

    [Fact]
    public void Counts_the_load_a_break_displaces_with_nobody_free_to_make_it_up()
    {
        // Two on two people's load, 06:00-10:00, and the window shuts at 10:00:
        // the manager's flat half hour and the assistant's quarter both fall
        // inside it, and nobody is free.
        var shifts = new List<PaintableShift> { Sh("ASM", 6, 10), Sh("SA", 6, 10) };
        var result = Paint(shifts, Demand((6, 10, G(("load_running", 2)))), _ => ["load_running"]);
        Assert.Equal([new LostWork("load_running", 3)], result.Lost);
        Assert.Equal(
            "Load running: 0.75h of today's plan is on nobody's shift - no one was free inside its window to make up the work breaks displaced",
            TaskPainter.LostWorkFlag("load_running", 3));
    }

    [Fact]
    public void Reports_nothing_when_every_displaced_quarter_hour_is_made_up()
    {
        var shifts = new List<PaintableShift> { Sh("ASM", 6, 10), Sh("SA", 6, 10), Sh("SA", 7, 10) };
        var result = Paint(shifts, Demand((6, 10, G(("load_running", 2)))), _ => ["load_running"]);
        Assert.Empty(result.Lost);
    }

    [Fact]
    public void Names_the_blank_quarter_hours_of_a_shift_as_runs()
    {
        // New: blankFlag has no TypeScript test; the fortnight's flags carry it.
        Assert.Equal(
            "Rory Nolan 07:00–14:15: 3 quarter-hours with no work (10:00–10:30, 13:00–13:15) that could not be cleared within the rules",
            TaskPainter.BlankFlag("Rory Nolan", 7 * H, 14.25 * H, [13 * H, 10 * H, 10.25 * H]));
        Assert.Equal(
            "Ann Lee 09:00–12:00: 1 quarter-hour with no work (11:45–12:00) that could not be cleared within the rules",
            TaskPainter.BlankFlag("Ann Lee", 9 * H, 12 * H, [11.75 * H]));
    }

    [Fact]
    public void Names_an_unmanned_till_as_such()
    {
        Assert.StartsWith("Tills: 0.25h of register time has nobody on it", TaskPainter.LostWorkFlag("checkout", 1));
    }
}
