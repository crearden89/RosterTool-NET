// Port of src/app/(app)/plan/generate/runPlanGeneration.test.ts.
// The plan's Generate: each request must answer within PlanAnswer or the run ends with a
// plain message, never an endless wait. The three actions are stood in for.

using Roster.Web.Features.Plan;

namespace Roster.Web.Tests.Plan;

public class RunPlanGenerationTests
{
    private static readonly Guid Store = Guid.Parse("00000000-0000-4000-8000-0000000000f1");
    private static readonly DateOnly Period = new(2026, 11, 23);
    private static readonly IReadOnlyList<IReadOnlyList<DateOnly>> Steps =
    [
        [new(2026, 11, 23), new(2026, 11, 24)],
        [new(2026, 11, 25), new(2026, 11, 26)],
    ];

    private static StubPlanService Plan() => new()
    {
        Start = (_, _) => Task.FromResult(new PlanStartResult(true, RunId: "r1", Steps: Steps)),
        Step = (_, _, _, dates) => Task.FromResult(new PlanStepResult(true, Part: string.Join(",", dates.Select(d => d.ToString("yyyy-MM-dd"))))),
        Finish = (_, _, _, _, _) => Task.FromResult(PlanResult.Done),
    };

    [Fact]
    public async Task Plans_the_steps_in_turn_and_saves_them()
    {
        var plan = Plan();
        IReadOnlyList<string>? savedParts = null;
        PlanChangeReason? savedReason = new(new DateOnly(2000, 1, 1));
        string? savedRun = null;
        plan.Finish = (_, _, run, parts, reason) =>
        {
            savedRun = run;
            savedParts = parts;
            savedReason = reason;
            return Task.FromResult(PlanResult.Done);
        };
        var progress = new List<string>();
        var res = await RunPlanGeneration.RunAsync(plan, Store, Period, progress.Add);
        Assert.Equal(PlanResult.Done, res);
        Assert.Equal(["Planning days 1–2 of 4…", "Planning days 3–4 of 4…", "Saving the plan…"], progress);
        Assert.Equal("r1", savedRun);
        Assert.Equal(["2026-11-23,2026-11-24", "2026-11-25,2026-11-26"], savedParts);
        Assert.Null(savedReason);
    }

    [Fact]
    public void Ends_with_a_message_when_a_step_never_answers_and_saves_nothing()
    {
        using var t = new FakeTimers(DateTimeOffset.Parse("2026-11-14T20:22:00Z"));
        var plan = Plan();
        var steps = 0;
        plan.Step = (_, _, _, _) => ++steps == 1 ? Task.FromResult(new PlanStepResult(true, Part: "p1")) : FakeTimers.Never<PlanStepResult>();
        var run = FakeTimers.Track(RunPlanGeneration.RunAsync(plan, Store, Period, _ => { }, time: t.Clock));
        t.Advance(RunPlanGeneration.PlanAnswer.TotalMilliseconds - 1);
        Assert.False(run.Done);
        t.Advance(1);
        Assert.Equal(
            new PlanResult(false, "The server did not answer while planning. Nothing was changed - reload the page and press Generate again."),
            run.Value);
        Assert.DoesNotContain("finish", plan.Calls);
    }

    [Fact]
    public void Says_how_to_tell_whether_the_plan_was_saved_when_the_save_never_answers()
    {
        using var t = new FakeTimers(DateTimeOffset.Parse("2026-11-14T20:22:00Z"));
        var plan = Plan();
        plan.Finish = (_, _, _, _, _) => FakeTimers.Never<PlanResult>();
        var run = FakeTimers.Track(RunPlanGeneration.RunAsync(plan, Store, Period, _ => { }, time: t.Clock));
        t.Advance(RunPlanGeneration.PlanAnswer.TotalMilliseconds);
        Assert.False(run.Value?.Ok);
        Assert.StartsWith("The server did not answer while saving. Reload the page: if \"Held plan generated\"", run.Value?.Error);
    }

    [Fact]
    public async Task Keeps_the_dropped_connection_message_for_a_request_that_fails_outright()
    {
        var plan = Plan();
        plan.Start = (_, _) => Task.FromException<PlanStartResult>(new HttpRequestException("Failed to fetch"));
        Assert.Equal(
            new PlanResult(false, "The connection to the server dropped while planning. Nothing was changed - press Generate again."),
            await RunPlanGeneration.RunAsync(plan, Store, Period, _ => { }));
    }

    [Fact]
    public async Task Passes_a_refusal_through_and_names_a_single_day_step()
    {
        var plan = Plan();
        plan.Start = (_, _) => Task.FromResult(new PlanStartResult(true, RunId: "r1", Steps: [[new(2026, 11, 23)]]));
        var progress = new List<string>();
        await RunPlanGeneration.RunAsync(plan, Store, Period, progress.Add, new PlanChangeReason(new DateOnly(2026, 11, 23)));
        Assert.Equal("Planning day 1 of 1…", progress[0]);

        plan.Start = (_, _) => Task.FromResult(new PlanStartResult(false, "This fortnight's plan is approved and locked."));
        Assert.Equal(new PlanResult(false, "This fortnight's plan is approved and locked."), await RunPlanGeneration.RunAsync(plan, Store, Period, _ => { }));
    }
}
