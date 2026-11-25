// Port of src/app/(app)/plan/generate/runPlanGeneration.ts.

namespace Roster.Web.Features.Plan;

public static class RunPlanGeneration
{
    /// <summary>
    /// Each request's answer must arrive within this long. It is past the server's own
    /// 60-second limit, so a request still unanswered then was lost on the way.
    /// </summary>
    public static readonly TimeSpan PlanAnswer = TimeSpan.FromMilliseconds(75_000);

    /// <summary>
    /// Generate a fortnight's plan in steps: a few days per request, one request after
    /// another, so none comes near the server's time limit (see <see cref="GenerateParts"/>).
    /// <paramref name="progress"/> is told what is happening in plain words. Nothing is held
    /// until the last step, so stopping part-way leaves the held plan as it was. A request
    /// that fails outright or never answers is reported here as a message - never the error
    /// page, and never an endless wait.
    /// </summary>
    public static async Task<PlanResult> RunAsync(
        IPlanService plan,
        Guid storeId,
        DateOnly periodStart,
        Action<string> progress,
        PlanChangeReason? reason = null,
        TimeProvider? time = null)
    {
        const string dropped = "The connection to the server dropped while planning. Nothing was changed - press Generate again.";
        // An unanswered request may still be pending: a reload clears it.
        const string unanswered =
            "The server did not answer while planning. Nothing was changed - reload the page and press Generate again.";
        PlanResult Lost(Exception e) => new(false, e is NoAnswer ? unanswered : dropped);

        PlanStartResult start;
        try
        {
            start = await GenerateRun.WithDeadline(plan.StartPlanGenerationAsync(storeId, periodStart), PlanAnswer, time);
        }
        catch (Exception e)
        {
            return Lost(e);
        }
        if (!start.Ok || string.IsNullOrEmpty(start.RunId) || start.Steps is null)
            return new PlanResult(false, start.Error ?? "Generation failed");

        var total = start.Steps.Sum(s => s.Count);
        var parts = new List<string>();
        var done = 0;
        foreach (var dates in start.Steps)
        {
            var first = done + 1;
            var last = done + dates.Count;
            progress(first == last ? $"Planning day {first} of {total}…" : $"Planning days {first}–{last} of {total}…");
            PlanStepResult step;
            try
            {
                step = await GenerateRun.WithDeadline(plan.PlanDaysStepAsync(storeId, periodStart, start.RunId, dates), PlanAnswer, time);
            }
            catch (Exception e)
            {
                return Lost(e);
            }
            if (!step.Ok || string.IsNullOrEmpty(step.Part)) return new PlanResult(false, step.Error ?? "Generation failed");
            parts.Add(step.Part);
            done = last;
        }

        progress("Saving the plan…");
        try
        {
            return await GenerateRun.WithDeadline(plan.FinishPlanGenerationAsync(storeId, periodStart, start.RunId, parts, reason), PlanAnswer, time);
        }
        catch (Exception e)
        {
            // The save may or may not have gone through before the answer was lost.
            var what = e is NoAnswer
                ? "The server did not answer while saving."
                : "The connection to the server dropped while saving.";
            return new PlanResult(
                false,
                $"{what} Reload the page: if \"Held plan generated\" shows the time you pressed Generate, the plan was saved; if not, press Generate again.");
        }
    }
}
