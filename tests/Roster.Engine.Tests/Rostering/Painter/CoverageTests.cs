// Port of src/lib/stage2/coverage.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;

namespace Roster.Engine.Tests.Rostering.Painter;

public class CoverageTests
{
    private const double H = 60;

    private static CoverShift Sh(string role, double start, double end, bool unfilled = false) =>
        new(role, start * H, end * H, unfilled);

    [Fact]
    public void Finds_nothing_wrong_with_a_properly_covered_day()
    {
        var day = new[] { Sh("ASM", 6, 14), Sh("DSM", 12, 21), Sh("SA", 6, 15), Sh("SA", 13, 21) };
        Assert.Empty(Coverage.CoverIssues(day));
    }

    [Fact]
    public void Names_the_quarter_hour_between_the_opener_leaving_and_the_closer_arriving()
    {
        // The shape the review found on G999 10/09: opener out at 13:45, closer in
        // at 14:00, store assistants on throughout.
        var day = new[] { Sh("ASM", 6.5, 13.75), Sh("ASM", 14, 21.25), Sh("SA", 6.5, 15), Sh("SA", 12, 21.25) };
        Assert.Equal([new CoverIssue("no_manager", 13.75 * H, 14 * H)], Coverage.CoverIssues(day));
        Assert.Equal(["No manager rostered 13:45–14:00"], Coverage.CoverFlags(day));
    }

    [Fact]
    public void Names_a_stretch_where_one_person_is_left_alone()
    {
        var day = new[] { Sh("ASM", 12, 21.25), Sh("SA", 12, 21) };
        Assert.Equal([new CoverIssue("lone_worker", 21 * H, 21.25 * H)], Coverage.CoverIssues(day));
    }

    [Fact]
    public void Names_a_junior_working_with_fewer_than_three_adults_as_one_merged_range()
    {
        var day = new[]
        {
            Sh("SM", 6, 15),
            Sh("SA", 6, 15),
            Sh("SA", 9, 15),
            Sh("ASM", 12, 21),
            Sh("SA", 15, 21),
            Sh("JSA", 12, 18), // four adults until 15:00, then only two
        };
        Assert.Equal([new CoverIssue("junior_supervision", 15 * H, 18 * H)], Coverage.CoverIssues(day));
        Assert.Equal(["Junior rostered with fewer than 3 adults 15:00–18:00"], Coverage.CoverFlags(day));
    }

    [Fact]
    public void Counts_an_RSM_as_a_manager()
    {
        Assert.Empty(Coverage.CoverIssues(new[] { Sh("RSM", 6, 14), Sh("SA", 6, 14) }));
    }

    [Fact]
    public void Ignores_open_slots_an_unfilled_manager_slot_is_a_gap_not_cover()
    {
        var day = new[] { Sh("DSM", 6, 14, true), Sh("SA", 6, 14), Sh("SA", 6, 14) };
        Assert.Equal([new CoverIssue("no_manager", 6 * H, 14 * H)], Coverage.CoverIssues(day));
    }

    [Fact]
    public void Uses_the_configured_minimums()
    {
        var day = new[] { Sh("ASM", 6, 14), Sh("SA", 6, 14) };
        Assert.Equal([new CoverIssue("lone_worker", 6 * H, 14 * H)], Coverage.CoverIssues(day, new CoverRules(3, 3)));
        // A RosteringRules converts to the two minimums.
        Assert.Equal(
            [new CoverIssue("lone_worker", 6 * H, 14 * H)],
            Coverage.CoverIssues(day, RosteringRules.Default with { MinPeopleOnFloor = 3, MinAdultsWithJsa = 3 }));
        Assert.Equal(["Fewer than 3 people rostered 06:00–14:00 - someone works alone"], Coverage.CoverFlags(day, new CoverRules(3, 3)));
        // ...and so do the plan day check's own cover rules.
        Assert.Equal(
            ["Fewer than 3 people rostered 06:00–14:00 - someone works alone"],
            Coverage.CoverFlags(day, new Roster.Engine.Planning.Plan.CoverRules(3, 3)));
    }

    [Fact]
    public void Reads_paintable_shifts_too()
    {
        // New: the roster's own shifts (Core.PaintableShift and Shift) go straight in.
        var day = new List<Shift>
        {
            new() { Role = "ASM", Start = 6.5 * H, End = 13.75 * H },
            new() { Role = "ASM", Start = 14 * H, End = 21.25 * H },
            new() { Role = "SA", Start = 6.5 * H, End = 15 * H },
            new() { Role = "SA", Start = 12 * H, End = 21.25 * H },
            new() { Role = "SM", Start = 13 * H, End = 15 * H, Unfilled = true },
        };
        Assert.Equal(["No manager rostered 13:45–14:00"], Coverage.CoverFlags(day));
    }
}
