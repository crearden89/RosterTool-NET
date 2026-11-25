// Port of the reads of src/lib/stage1/planShifts.ts (computePlanShifts). The
// painting, the plan's shifts, the planner's notes and the day check are the
// engine's PlanSlots and PlanShifts.

using Roster.Data.Services.RefData;
using Roster.Data.Services.SubTasks;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;
using Roster.Engine.SubTasks;

namespace Roster.Data.Services.Planning;

// ─── Generic Shift Plan (the Task view) ──────────────────────────────────────
// The PLAN is the demand-driven ideal template: the shaper turns Stage 1's
// demand curve into typed slots, and this paints each slot's 15-min blocks with
// its tasks for display. The ROSTER fills these same slots with real names -
// the structures match by construction because both read computeDaySlots.
public sealed class PlanShiftsLoader(
    IDaySlotsLoader daySlots,
    IRefData refData,
    IDayCheckOptionsLoader checkOptions,
    ISubtaskLabels labels) : IPlanShiftsLoader
{
    public async Task<PlanShiftComputation> ComputePlanShiftsAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var slots = await daySlots.ComputeDaySlotsAsync(storeId, date, ct);
        var stage1 = slots.Stage1;
        if (!stage1.HasData || slots.Shaped is not { } shaped) return PlanShiftComputation.Empty();

        var priority = await refData.GetTaskPriorityAsync(ct);
        var ruleOverrides = await refData.GetRuleOverridesAsync(ct);
        var options = await checkOptions.DayCheckOptionsAsync(storeId, date, ct);

        // Every slot's blocks painted from the (shaper-adjusted) demand curve,
        // window-aware - the exact painter the roster uses, so tiles always agree.
        // The trial paint computeDaySlots kept is the Task view's paint.
        var painted = slots.Painted ?? PlanSlots.PaintSlots(shaped, stage1, priority, ruleOverrides);

        // Every Reset, Load Running, Cleaning and Specials tile labelled with its
        // sub-task - one pass after painting that writes labels only, so nothing
        // after it (the day checker included) sees a difference.
        var shifts = await labels.LabelPlanDayAsync(storeId, date, ResetSource.From(stage1),
            PlanShifts.Unlabelled(painted.Painted, p => p.Slot), ct);

        // The planner's notes on what it did, then the day checker's findings on
        // the shifts as painted.
        return PlanShifts.Finish(stage1, shaped.Flags, shifts, painted.Result.SearchCapped, TaskPainter.SearchCapFlag, options);
    }
}
