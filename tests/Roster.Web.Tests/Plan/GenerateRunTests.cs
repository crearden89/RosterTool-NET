// Port of src/app/(app)/plan/generate/generateRun.test.ts.
// One press of Generate Roster (fault F8): it ends when the action answers, and when no
// answer comes it finds out from the server whether the roster was saved. Time is faked;
// the action and the check are stood in for.

using Roster.Web.Features.Plan;
using static Roster.Web.Features.Plan.GenerateRun;

namespace Roster.Web.Tests.Plan;

public class GenerateRunTests
{
    // The held roster on the page when the button was pressed, and a later one.
    private static readonly DateTimeOffset Before = DateTimeOffset.Parse("2026-11-14T20:15:00.000+00:00");
    private static readonly DateTimeOffset Saved = DateTimeOffset.Parse("2026-11-14T20:22:05.123+00:00");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T20:22:00.000Z");

    /// <summary>A stand-in that counts its calls and answers from a list (the last answer repeats).</summary>
    private sealed class HeldAt(params Func<Task<DateTimeOffset?>>[] answers)
    {
        public int Calls { get; private set; }

        public Task<DateTimeOffset?> Call()
        {
            var i = Math.Min(Calls, answers.Length - 1);
            Calls++;
            return answers[i]();
        }

        public static HeldAt Always(DateTimeOffset? at) => new(() => Task.FromResult(at));
    }

    private sealed record Press(Tracked<RunOutcome?> Run, List<string> Waiting, Action Leave);

    private static Press PressIt(Func<Task<PlanResult>> generate, HeldAt heldAt, FakeTimers t, DateTimeOffset? before = null, bool noBefore = false)
    {
        var waiting = new List<string>();
        var left = false;
        var run = FakeTimers.Track(RunRosterGenerateAsync(
            new RosterGenerateDeps
            {
                Generate = generate,
                HeldAt = heldAt.Call,
                Before = noBefore ? null : before ?? Before,
                OnWaiting = waiting.Add,
                Cancelled = () => left,
            },
            time: t.Clock));
        return new Press(run, waiting, () => left = true);
    }

    // ─── The answer arrives ──────────────────────────────────────────────────

    [Fact]
    public void Ends_with_the_confirmation_as_soon_as_the_action_answers_without_asking_the_server()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Saved);
        var p = PressIt(() => t.After(3_000, new PlanResult(true)), heldAt, t);
        t.Advance(2_999);
        Assert.False(p.Run.Done);
        t.Advance(1);
        // No refresh: the action's answer carries the page.
        Assert.Equal(new RunOutcome(true, RosterBuilt, false), p.Run.Value);
        Assert.Equal(0, heldAt.Calls);
    }

    [Fact]
    public void Shows_the_servers_own_refusal_as_it_is()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Before);
        var p = PressIt(() => t.After(1_000, new PlanResult(false, "Generate the plan first.")), heldAt, t);
        t.Advance(1_000);
        Assert.Equal(new RunOutcome(false, "Generate the plan first.", false), p.Run.Value);
        Assert.Equal(0, heldAt.Calls);
    }

    [Fact]
    public void Waits_quietly_for_a_slow_answer_before_the_checks_start()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Before);
        var p = PressIt(() => t.After(19_500, new PlanResult(true)), heldAt, t);
        t.Advance(19_500);
        Assert.Equal(RosterBuilt, p.Run.Value?.Message);
        Assert.Equal(0, heldAt.Calls);
        Assert.Empty(p.Waiting);
    }

    [Fact]
    public void Takes_an_answer_that_arrives_while_it_is_checking()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Before);
        var p = PressIt(() => t.After(32_000, new PlanResult(true)), heldAt, t);
        t.Advance(31_999);
        Assert.False(p.Run.Done);
        t.Advance(1);
        Assert.Equal(new RunOutcome(true, RosterBuilt, false), p.Run.Value);
    }

    // ─── The safety net, when no answer arrives ──────────────────────────────

    [Fact]
    public void From_20s_checks_every_5s_and_a_newer_held_roster_confirms_the_build_and_refreshes_the_page()
    {
        using var t = new FakeTimers(Now);
        var heldAt = new HeldAt(() => Task.FromResult<DateTimeOffset?>(Before), () => Task.FromResult<DateTimeOffset?>(Saved));
        var p = PressIt(FakeTimers.Never<PlanResult>, heldAt, t);
        t.Advance(19_999);
        Assert.Equal(0, heldAt.Calls);
        t.Advance(1);
        Assert.Equal([Checking], p.Waiting);
        Assert.Equal(1, heldAt.Calls);
        Assert.False(p.Run.Done); // the roster on the page is not newer
        t.Advance(5_000);
        Assert.Equal(2, heldAt.Calls);
        Assert.Equal(new RunOutcome(true, RosterBuilt, true), p.Run.Value);
    }

    [Fact]
    public void Says_plainly_when_nothing_was_saved_by_75s_and_refreshes_to_clear_the_unanswered_request()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Before);
        var p = PressIt(FakeTimers.Never<PlanResult>, heldAt, t);
        t.Advance(74_999);
        Assert.False(p.Run.Done);
        t.Advance(1);
        Assert.Equal(new RunOutcome(false, NotSaved, true), p.Run.Value);
        // 20 s, 25 s, ... 75 s.
        Assert.Equal(12, heldAt.Calls);
    }

    [Fact]
    public void Checks_at_once_when_the_connection_drops_and_confirms_a_roster_saved_before_it_dropped()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Saved);
        var p = PressIt(() => t.DropAfter<PlanResult>(2_000), heldAt, t);
        t.Advance(2_000);
        Assert.Contains(CheckingDropped, p.Waiting);
        Assert.Equal(new RunOutcome(true, RosterBuilt, true), p.Run.Value);
    }

    [Fact]
    public void After_a_drop_with_nothing_saved_says_so_checking_every_5s_not_in_a_loop()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Before);
        var p = PressIt(() => t.DropAfter<PlanResult>(2_000), heldAt, t);
        t.Advance(76_000);
        Assert.False(p.Run.Done);
        t.Advance(1_000);
        Assert.Equal(new RunOutcome(false, DroppedNotSaved, false), p.Run.Value);
        // 2 s, 7 s, ... 77 s.
        Assert.Equal(16, heldAt.Calls);
    }

    [Fact]
    public void Treats_any_held_roster_as_new_when_the_fortnight_had_none()
    {
        using var t = new FakeTimers(Now);
        var p = PressIt(FakeTimers.Never<PlanResult>, HeldAt.Always(Saved), t, noBefore: true);
        t.Advance(20_000);
        Assert.Equal(new RunOutcome(true, RosterBuilt, true), p.Run.Value);
    }

    [Fact]
    public void Does_not_take_a_fortnight_with_still_no_roster_as_built()
    {
        using var t = new FakeTimers(Now);
        var p = PressIt(FakeTimers.Never<PlanResult>, HeldAt.Always(null), t, noBefore: true);
        t.Advance(75_000);
        Assert.Equal(new RunOutcome(false, NotSaved, true), p.Run.Value);
    }

    [Fact]
    public void Cuts_off_a_check_that_hangs_and_says_when_it_could_not_check_at_all()
    {
        using var t = new FakeTimers(Now);
        var heldAt = new HeldAt(FakeTimers.Never<DateTimeOffset?>);
        var p = PressIt(FakeTimers.Never<PlanResult>, heldAt, t);
        // Checks at 20, 35, 50 and 65 s, each cut off after 10 s.
        t.Advance(74_999);
        Assert.False(p.Run.Done);
        t.Advance(1);
        Assert.Equal(new RunOutcome(false, CannotCheck, false), p.Run.Value);
        Assert.Equal(4, heldAt.Calls);
    }

    [Fact]
    public void Gets_past_a_check_that_fails_and_confirms_on_the_next_one()
    {
        using var t = new FakeTimers(Now);
        var heldAt = new HeldAt(
            () => Task.FromException<DateTimeOffset?>(new InvalidOperationException("The check failed (500)")),
            () => Task.FromResult<DateTimeOffset?>(Saved));
        var p = PressIt(FakeTimers.Never<PlanResult>, heldAt, t);
        t.Advance(25_000);
        Assert.Equal(new RunOutcome(true, RosterBuilt, true), p.Run.Value);
    }

    [Fact]
    public void Stops_checking_with_no_outcome_once_the_page_is_left()
    {
        using var t = new FakeTimers(Now);
        var heldAt = HeldAt.Always(Before);
        var p = PressIt(FakeTimers.Never<PlanResult>, heldAt, t);
        t.Advance(20_000);
        Assert.Equal(1, heldAt.Calls);
        p.Leave();
        t.Advance(60_000);
        Assert.True(p.Run.Done);
        Assert.Null(p.Run.Value);
        Assert.Equal(1, heldAt.Calls);
    }

    // ─── isNewer ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsNewer_compares_the_held_rosters_times_as_instants()
    {
        Assert.True(IsNewer(Saved, Before));
        Assert.False(IsNewer(Before, Saved));
        Assert.False(IsNewer(Before, Before));
        Assert.False(IsNewer(DateTimeOffset.Parse("2026-11-14T20:15:00Z"), Before));
    }

    [Fact]
    public void IsNewer_counts_any_roster_as_newer_than_none_and_none_as_never_newer()
    {
        Assert.True(IsNewer(Saved, null));
        Assert.False(IsNewer(null, Before));
        Assert.False(IsNewer(null, null));
    }

    // ─── singleFlight: one build at a time ────────────────────────────────────

    [Fact]
    public void SingleFlight_ignores_a_second_press_while_the_first_is_running_and_takes_one_after_it_ends()
    {
        using var t = new FakeTimers(Now);
        var start = new SingleFlight();
        var calls = 0;
        Task Job()
        {
            calls++;
            return t.After(5_000, 0);
        }
        Assert.True(start.Start(Job));
        Assert.False(start.Start(Job));
        Assert.Equal(1, calls);
        t.Advance(5_000);
        Assert.True(start.Start(Job));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SingleFlight_is_free_again_after_a_job_that_fails()
    {
        using var t = new FakeTimers(Now);
        var start = new SingleFlight();
        Assert.True(start.Start(() => t.DropAfter<int>(1_000)));
        t.Advance(1_000);
        Assert.True(start.Start(() => t.DropAfter<int>(1_000)));
    }

    [Fact]
    public void SingleFlight_is_free_again_after_a_job_that_throws_at_once()
    {
        var start = new SingleFlight();
        var e = Assert.Throws<InvalidOperationException>(() => start.Start(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", e.Message);
        Assert.True(start.Start(() => Task.CompletedTask));
    }

    // ─── withDeadline ─────────────────────────────────────────────────────────

    [Fact]
    public void WithDeadline_passes_an_answer_or_a_failure_through_and_fails_with_NoAnswer_when_there_is_neither()
    {
        using var t = new FakeTimers(Now);
        var ok = FakeTimers.Track(WithDeadline(Task.FromResult(7), TimeSpan.FromSeconds(1), t.Clock));
        var failed = FakeTimers.Track(WithDeadline(Task.FromException<int>(new InvalidOperationException("dropped")), TimeSpan.FromSeconds(1), t.Clock));
        var lost = FakeTimers.Track(WithDeadline(FakeTimers.Never<int>(), TimeSpan.FromMilliseconds(75_000), t.Clock));
        t.Drain();
        Assert.Equal(7, ok.Value);
        Assert.Equal("dropped", failed.Error?.Message);
        t.Advance(74_999);
        Assert.False(lost.Done);
        t.Advance(1);
        var e = Assert.IsType<NoAnswer>(lost.Error);
        Assert.Equal("No answer within 75 seconds", e.Message);
    }
}
