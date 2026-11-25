// Port of src/lib/stage2/heldPlan.ts.
// ─── The roster from the HELD plan ───────────────────────────────────────────
// Once the store and its EMSO have approved a fortnight's plan, the roster is
// built from THAT plan - not a fresh solve that could come out differently,
// and not one that drops the manager's edits. Each planned shift becomes a
// slot to fill, and the plan's painted tiles become the work to cover, so the
// roster carries out exactly what was approved.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

/// <summary>A held (approved) plan day: its Stage 1 result and its plan shifts (null when it has none).</summary>
public sealed record HeldPlanDay(DateOnly Date, Stage1Computation Result, PlanShiftComputation? Plan);

public static class HeldPlan
{
    // Display-only names for the checkout split back to the one task group.
    private static readonly IReadOnlyDictionary<string, string> Task = new Dictionary<string, string>
    {
        ["checkout"] = TaskGroups.Checkout,
        ["registers"] = TaskGroups.Checkout,
        ["scos"] = TaskGroups.Checkout,
        ["load_running"] = TaskGroups.LoadRunning,
        ["reset"] = TaskGroups.Reset,
        ["admin"] = TaskGroups.Admin,
        ["specials"] = TaskGroups.Specials,
        ["cleaning"] = TaskGroups.Cleaning,
        ["non_op"] = TaskGroups.NonOp,
    };

    // The work on the plan's shifts, quarter-hour by quarter-hour, in the shape
    // the roster's painter takes (demandFromStage1's): every painted tile counts
    // toward the headcount; Non-Op counts but isn't a directed task. Breaks and
    // unpainted time are not work.
    public static List<DemandBlock> DemandFromPlanShifts(IEnumerable<PlanShift> shifts) =>
        DemandFromPlanShifts(shifts.Select(s => s.Blocks.Cast<ITile>()));

    /// <summary>The same over any shifts' tiles (each item is one shift's blocks).</summary>
    public static List<DemandBlock> DemandFromPlanShifts(IEnumerable<IEnumerable<ITile>> shiftBlocks)
    {
        var byTime = new JsMap<double, DemandBlock>();
        foreach (var blocks in shiftBlocks)
        {
            foreach (var b in blocks)
            {
                if (!Task.TryGetValue(b.Group, out var g)) continue; // break, flex
                var d = byTime.Get(b.Time) ?? new DemandBlock(b.Time, 0);
                d.Total += 1;
                if (g != TaskGroups.NonOp) d.Groups.Set(g, d.Groups.GetOr(g, 0) + 1);
                byTime.Set(b.Time, d);
            }
        }
        return byTime.Values().OrderBy(d => d.Time).ToList();
    }

    // A held plan day in the form the roster solve reads a day's slots in
    // (ComputeDaySlots'): no shifts, or no data, is a day with nothing to fill.
    public static DaySlots SlotsFromHeldPlan(HeldPlanDay day)
    {
        var plan = day.Plan;
        if (!day.Result.HasData || plan is null || !plan.HasData || plan.Shifts.Count == 0)
        {
            return new DaySlots(null, day.Result);
        }
        return new DaySlots(
            new ShapedDay
            {
                Slots = plan.Shifts.Select(s => new PlanSlot(s.SlotId, s.PlanRole, s.Start, s.End)).ToList(),
                DemandByBlock = DemandFromPlanShifts(plan.Shifts),
                // The planner's notes on the structure (a manager gap, an extra body for
                // the load) carry over; the plan's own checks (DayChecker) are about the
                // plan's shifts, and the roster checks the shifts it fills.
                Flags = [.. plan.Notes ?? plan.Flags],
            },
            day.Result);
    }

    // The held plan's shifts with their tiles, in the roster's task groups (the
    // till split folded back into checkout; labels dropped), for the roster to
    // keep where it can. Nothing when the day has no held plan.
    public static List<HeldShift>? HeldShiftsOf(HeldPlanDay? day)
    {
        var plan = day?.Plan;
        if (day is null || !day.Result.HasData || plan is null || !plan.HasData || plan.Shifts.Count == 0) return null;
        return plan.Shifts.Select(s => new HeldShift
        {
            Role = s.PlanRole,
            Start = s.Start,
            End = s.End,
            Blocks = s.Blocks.Select(b => new ShiftBlock(
                b.Time,
                b.Group == TaskGroups.Break || b.Group == TaskGroups.Flex
                    ? b.Group
                    : Task.TryGetValue(b.Group, out var g) ? g : TaskGroups.Flex)).ToList(),
        }).ToList();
    }
}
