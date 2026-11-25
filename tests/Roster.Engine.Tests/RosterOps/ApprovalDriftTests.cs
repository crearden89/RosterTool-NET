// New: the pure parts of src/lib/roster/approval.ts and drift.ts, which have no
// TypeScript tests of their own: what publishing writes, and when a published
// roster has drifted from the held one.

using Roster.Engine.Core;
using Roster.Engine.RosterOps;

namespace Roster.Engine.Tests.RosterOps;

public class ApprovalDriftTests
{
    private static Shift S(string id, double start, double end, bool unfilled = false, params ShiftBlock[] blocks) => new()
    {
        EmployeeId = id, Name = id.ToUpperInvariant(), Role = "SA", Start = start, End = end, BreakMinutes = 15, Unfilled = unfilled, Blocks = [.. blocks],
    };

    [Fact]
    public void Publishes_named_shifts_with_their_tiles_breaks_kept_flex_dropped()
    {
        var payload = Approval.ShiftPayload(
        [
            S("a", 420, 480, false,
                new ShiftBlock(420, "registers"), new ShiftBlock(435, "cleaning", "boh"), new ShiftBlock(450, "break"), new ShiftBlock(465, "flex"),
                new ShiftBlock(480, "reset", "boh")),
            S("", 480, 600, true, new ShiftBlock(480, "checkout")),
        ]);
        var a = Assert.Single(payload); // the open shift has no employee: never persisted
        Assert.Equal(("a", "A", "SA", "07:00:00", "08:00:00", 15.0, false), (a.EmployeeId, a.EmployeeName, a.Role, a.StartTime, a.EndTime, a.BreakMinutes, a.Locked));
        Assert.Equal(
            [("07:00:00", "checkout", null), ("07:15:00", "cleaning", "boh"), ("07:30:00", "break", null), ("08:00:00", "reset", (string?)null)],
            a.Blocks.Select(b => (b.BlockTime, b.LabourGroup, b.SubTask)));
    }

    [Fact]
    public void Signs_off_a_closed_day_and_skips_a_day_with_no_roster()
    {
        Assert.Equal(PublishStep.ApproveClosed, Approval.StepFor(new RosterDay([], [], false, true)));
        Assert.Equal(PublishStep.Skip, Approval.StepFor(new RosterDay([], [], false, false)));
        Assert.Equal(PublishStep.Skip, Approval.StepFor((RosterDay?)null));
        Assert.Equal(PublishStep.Approve, Approval.StepFor(new RosterDay([S("a", 420, 480)], [], true, false)));
    }

    [Fact]
    public void Asks_for_a_note_only_when_a_day_has_an_error_and_none_was_given()
    {
        var d = DateOnlyExtensions.ParseIso("2026-11-24");
        var open = new RosterDay([S("", 420, 480, true)], [], true, false);
        Assert.Equal(
            "1 day has an error (open shifts or cover gaps): 24/11. Add a note on how you tried to cover them before publishing.",
            Approval.CoverNoteError(_ => open, [d], "  "));
        Assert.Null(Approval.CoverNoteError(_ => open, [d], "Called two casuals"));
        Assert.Null(Approval.CoverNoteError(_ => new RosterDay([S("a", 420, 480)], [], true, false), [d], null));
        Assert.Equal("Called two casuals", Approval.CoverageComment("  Called two casuals "));
        Assert.Null(Approval.CoverageComment("   "));
    }

    private static readonly DateTimeOffset Approved = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_an_approved_day_drifts_and_not_from_a_roster_solved_before_its_approval()
    {
        List<Shift> held = [S("a", 420, 600)];
        Assert.False(Drift.DetectRosterDrift("none", null, true, Approved, held, []).Drifted);
        Assert.False(Drift.DetectRosterDrift("approved", Approved, false, null, held, []).Drifted);
        Assert.False(Drift.DetectRosterDrift("approved", Approved, true, Approved.AddMinutes(-5), held, []).Drifted);
        Assert.False(Drift.DetectRosterDrift("approved", Approved, true, Approved.AddMinutes(5), null, []).Drifted);
    }

    [Fact]
    public void Drifts_when_who_or_when_differs_from_what_was_published()
    {
        List<Shift> held = [S("a", 420, 600), S("", 600, 720, true)];
        var later = Approved.AddMinutes(5);
        Assert.False(Drift.DetectRosterDrift("approved", Approved, true, later, held, [new("a", 420, 600)]).Drifted);
        Assert.True(Drift.DetectRosterDrift("approved", Approved, true, later, held, [new("a", 420, 615)]).Drifted);
        Assert.True(Drift.DetectRosterDrift("approved", Approved, true, later, held, [new("a", 420, 600), new("b", 420, 600)]).Drifted);
        var d = Drift.DetectRosterDrift("approved", Approved, true, later, held, [new("b", 420, 600)]);
        Assert.Equal((true, true, Approved, later), (d.Drifted, d.Approved, d.ApprovedAt, d.RegeneratedAt));
    }
}
