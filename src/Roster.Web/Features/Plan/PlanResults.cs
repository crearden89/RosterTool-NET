// Port of the result and input shapes of src/app/(app)/plan/generate/actions.ts.

namespace Roster.Web.Features.Plan;

/// <summary>What an action answers: done, or the reason it was refused (actions.ts <c>Result</c>).</summary>
public record PlanResult(bool Ok, string? Error = null)
{
    public static readonly PlanResult Done = new(true);

    public static PlanResult Refused(string error) => new(false, error);
}

/// <summary>
/// startPlanGeneration's answer: the run's id and the fortnight's days in steps
/// (<see cref="GenerateParts.PlanSteps"/>).
/// </summary>
public sealed record PlanStartResult(bool Ok, string? Error = null, string? RunId = null, IReadOnlyList<IReadOnlyList<DateOnly>>? Steps = null)
    : PlanResult(Ok, Error);

/// <summary>planDaysStep's answer: the step's days, sealed (<see cref="GenerateParts.SealPlanPart{TDay}"/>).</summary>
public sealed record PlanStepResult(bool Ok, string? Error = null, string? Part = null) : PlanResult(Ok, Error);

/// <summary>
/// Why the plan changed, for the review's history: the public-holiday load setting of
/// <see cref="PhDate"/> (finishPlanGeneration's <c>reason</c>). No reason is a regenerate.
/// </summary>
public sealed record PlanChangeReason(DateOnly PhDate);

/// <summary>One tile of an edited shift (editPlanDay's block): its time, its group and any sub-task chosen.</summary>
public sealed record PlanBlockEdit(double Time, string Group, string? Sub = null);

/// <summary>
/// One shift as the Plan page's edit mode leaves it (actions.ts <c>PlanShiftEdit</c>): its slot,
/// role badge ("MGR", "SA" or "JSA"), span in minutes from midnight and its painted tiles.
/// </summary>
public sealed record PlanShiftEdit(string SlotId, string PlanRole, double Start, double End, IReadOnlyList<PlanBlockEdit> Blocks);

/// <summary>The page's query string, as the TypeScript page's <c>searchParams</c> carries it.</summary>
public sealed record PlanPageQuery(string? Store = null, string? Date = null, string? View = null, string? Period = null);
