// Port of src/app/(app)/plan/generate/generateRun.ts.
// What one press of Generate Roster does, kept apart from the component so it
// can be tested (GenerateRunTests).
//
// The button runs on plain state (fault F8 in the TypeScript app): it ends when
// the action answers. The safety net, for an answer slow to come: with no
// answer after a while (or a failed call), it asks when the held roster was last
// saved. A roster newer than the one on the page when the button was pressed
// means the build went through: it says so and the page is refreshed. If none
// has been saved by the time the server must have finished or failed, it says
// plainly that nothing was changed.
//
// Time comes from a TimeProvider, so the tests can move it on step by step.

using Roster.Engine.Core;

namespace Roster.Web.Features.Plan;

/// <summary>
/// What the button shows when a press ends, and whether the page should be read again
/// because the action's own answer (which carries the page) did not arrive.
/// </summary>
public sealed record RunOutcome(bool Ok, string Message, bool Refresh);

/// <summary>How long Generate Roster's safety net waits, checks and gives up (ROSTER_WAIT).</summary>
public sealed record RosterWait(TimeSpan CheckAfter, TimeSpan CheckEvery, TimeSpan GiveUpAfter, TimeSpan CheckTimeout)
{
    public static readonly RosterWait Default = new(
        // With no answer after this long, start checking whether the roster has been saved...
        CheckAfter: TimeSpan.FromMilliseconds(20_000),
        // ...then check again this often...
        CheckEvery: TimeSpan.FromMilliseconds(5_000),
        // ...until this long after the press. It is past the server's own 60-second
        // limit, so a roster not saved by then was not built.
        GiveUpAfter: TimeSpan.FromMilliseconds(75_000),
        // A single check that takes longer than this counts as failed.
        CheckTimeout: TimeSpan.FromMilliseconds(10_000));
}

/// <summary>What one press of Generate Roster calls on (runRosterGenerate's deps).</summary>
public sealed record RosterGenerateDeps
{
    /// <summary>The action that builds and holds the roster.</summary>
    public required Func<Task<PlanResult>> Generate { get; init; }

    /// <summary>When the held roster was last saved, asked of the server directly.</summary>
    public required Func<Task<DateTimeOffset?>> HeldAt { get; init; }

    /// <summary>The held roster's time on the page when the button was pressed.</summary>
    public DateTimeOffset? Before { get; init; }

    public Action<string>? OnWaiting { get; init; }

    /// <summary>True once the page has been left: the press then ends with no outcome.</summary>
    public Func<bool>? Cancelled { get; init; }
}

/// <summary>Thrown by <see cref="GenerateRun.WithDeadline{T}"/> when the task has not settled in time.</summary>
public sealed class NoAnswer(TimeSpan deadline)
    : Exception($"No answer within {Js.NumberToString(Js.Round(deadline.TotalMilliseconds / 1000))} seconds");

/// <summary>
/// One build at a time: a press while one is running is ignored. The button is disabled too,
/// but a double click can land before it re-renders.
/// </summary>
public sealed class SingleFlight
{
    private bool _running;

    /// <summary>Starts the job unless one is running. Returns whether it was started.</summary>
    public bool Start(Func<Task> job)
    {
        if (_running) return false;
        _running = true;
        Task run;
        try
        {
            run = job();
        }
        catch
        {
            _running = false;
            throw;
        }
        _ = SettleAsync(run);
        return true;
    }

    private async Task SettleAsync(Task run)
    {
        try
        {
            await run;
        }
        catch
        {
            // The job reports its own failure; this only frees the button.
        }
        _running = false;
    }
}

public static class GenerateRun
{
    public const string RosterBuilt = "✓ Roster built from the approved plan";

    // Shown while the safety net checks.
    public const string Checking = "No answer yet - checking whether the roster has been saved…";
    public const string CheckingDropped = "The connection dropped - checking whether the roster has been saved…";

    // The press's final messages when no answer arrived.
    public const string NotSaved =
        "There was no answer from the server, and no new roster has been saved, so nothing was changed. Press it again.";
    public const string DroppedNotSaved =
        "The connection to the server dropped while building the roster, and no new roster has been saved, so nothing was changed. Press it again.";
    public const string CannotCheck =
        "There was no answer from the server, and the app could not check whether the roster was saved. Reload the page: if \"Held roster generated\" shows the time you pressed it, the roster was built; if not, press it again.";

    /// <summary>Fails with <see cref="NoAnswer"/> when <paramref name="task"/> has not settled within <paramref name="deadline"/>.</summary>
    public static async Task<T> WithDeadline<T>(Task<T> task, TimeSpan deadline, TimeProvider? time = null)
    {
        using var stop = new CancellationTokenSource();
        var timer = Task.Delay(deadline, time ?? TimeProvider.System, stop.Token);
        var first = await Task.WhenAny(task, timer);
        if (first != task) throw new NoAnswer(deadline);
        stop.Cancel();
        return await task;
    }

    /// <summary>
    /// Is the held roster's time <paramref name="after"/> later than <paramref name="before"/>,
    /// the one on the page when the button was pressed? No roster before means any roster is newer.
    /// </summary>
    public static bool IsNewer(DateTimeOffset? after, DateTimeOffset? before)
    {
        if (after is null) return false;
        if (before is null) return true;
        return after.Value > before.Value;
    }

    private sealed record Answer(PlanResult? Res);

    private static RunOutcome FromAnswer(PlanResult res) =>
        res.Ok ? new RunOutcome(true, RosterBuilt, false) : new RunOutcome(false, res.Error ?? "Generation failed", false);

    /// <summary>
    /// One press of Generate Roster. Ends with what to show, or null when the press was
    /// abandoned (the page was left).
    /// </summary>
    public static async Task<RunOutcome?> RunRosterGenerateAsync(RosterGenerateDeps deps, RosterWait? wait = null, TimeProvider? time = null)
    {
        wait ??= RosterWait.Default;
        time ??= TimeProvider.System;
        var started = time.GetUtcNow();
        // The answer, once it has come: Res set, or Res null when the call failed (dropped).
        Answer? got = null;
        var generating = deps.Generate();
        async Task Watch()
        {
            try
            {
                got = new Answer(await generating ?? new PlanResult(false));
            }
            catch
            {
                got = new Answer(null);
            }
        }
        var answer = Watch();

        // The usual case: the answer arrives, and says whether the roster was saved.
        await Task.WhenAny(answer, Task.Delay(wait.CheckAfter, time));
        var first = got;
        if (first?.Res is { } firstRes) return FromAnswer(firstRes);

        // No answer yet, or the call failed: ask whether it was saved.
        deps.OnWaiting?.Invoke(first is not null ? CheckingDropped : Checking);
        for (;;)
        {
            if (deps.Cancelled?.Invoke() == true) return null;
            DateTimeOffset? at = null;
            var checkedOk = true;
            try
            {
                at = await WithDeadline(deps.HeldAt(), wait.CheckTimeout, time);
            }
            catch
            {
                checkedOk = false;
            }
            if (deps.Cancelled?.Invoke() == true) return null;
            // An answer that arrived meanwhile still counts.
            var late = got;
            if (late?.Res is { } lateRes) return FromAnswer(lateRes);
            if (IsNewer(at, deps.Before)) return new RunOutcome(true, RosterBuilt, true);
            if (time.GetUtcNow() - started >= wait.GiveUpAfter)
            {
                if (!checkedOk) return new RunOutcome(false, CannotCheck, false);
                if (late is not null) return new RunOutcome(false, DroppedNotSaved, false);
                // With no answer at all, the request may still be pending: the refresh clears it.
                return new RunOutcome(false, NotSaved, true);
            }
            // Wake early for an answer still to come; a failed one will not come.
            if (late is not null) await Task.Delay(wait.CheckEvery, time);
            else await Task.WhenAny(answer, Task.Delay(wait.CheckEvery, time));
        }
    }
}
