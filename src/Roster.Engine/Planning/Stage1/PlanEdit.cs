// Port of src/lib/stage1/planEdit.ts.
// ─── Manual plan-shift reshaping (Task view "Edit") ──────────────────────────
// Pure module, shared by the client's live preview and the server's save, so
// the grid a manager sees while dragging times and the plan that gets written
// can never disagree.
//
// The shaper cuts the day's ideal shift structure from demand. A manager may
// still need to override it - stretch a shift, add cover, drop one - and this
// is what that edit does to a shift's 15-minute blocks:
//   - painted work is PRESERVED wherever the new span still covers it;
//   - newly-added time starts as `flex` (unpainted, costs nothing on the
//     benchmark table until it is given a labour group);
//   - the break entitlement is RECOMPUTED for the new length and re-placed,
//     because it is a function of shift length, not something the manager
//     should have to fix up by hand after every resize.

using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Engine.Planning.Stage1;

public static class PlanEdit
{
    // The engine role each slot type is costed/painted as (mirrors planShifts).
    public static readonly IReadOnlyDictionary<string, string> PlanRoleEngine = new Dictionary<string, string>
    {
        [RoleBadges.Mgr] = Roles.ASM,
        [RoleBadges.Sa] = Roles.SA,
        [RoleBadges.Jsa] = Roles.JSA,
    };

    public const double MinPlanShiftMin = 180; // 3h - the hard floor for any shift

    /// <summary>
    /// Rebuild one shift at a new span. <paramref name="prev"/> is the shift being
    /// resized, or null for a brand-new one.
    /// </summary>
    public static PlanShift ReshapePlanShift(PlanShift? prev, string slotId, string planRole, double start, double end)
    {
        var brk = Breaks.BreakForRole(PlanRoleEngine[planRole], (end - start) / 60);

        // Painted tiles worth carrying over, with their sub-task labels - never the
        // old break tiles, which are re-placed below for the new length.
        var carried = new JsMap<double, DisplayBlock>();
        foreach (var b in prev?.Blocks ?? [])
            if (b.Group != TaskGroups.Break) carried.Set(b.Time, b);

        var blocks = new List<DisplayBlock>();
        for (var t = start; t < end; t += Grid.BlockMin)
            blocks.Add(carried.TryGet(t, out var c) ? new DisplayBlock(t, c.Group, c.Sub is { Length: > 0 } ? c.Sub : null) : new DisplayBlock(t, TaskGroups.Flex));
        foreach (var (ws, we) in Display.BreakWindows(start, end, brk))
            for (var i = 0; i < blocks.Count; i++)
                if (blocks[i].Time >= ws && blocks[i].Time < we) blocks[i] = blocks[i] with { Group = TaskGroups.Break, Sub = null };

        return new PlanShift
        {
            SlotId = slotId,
            PlanRole = planRole,
            Start = start,
            End = end,
            BreakMinutes = brk.TotalMinutes,
            UnpaidBreakMinutes = brk.UnpaidMinutes,
            Blocks = blocks,
        };
    }

    /// <summary>
    /// Structural checks a manual edit must pass before it can be saved. Break
    /// placement is validated separately (Breaks.BreakTileError), against the
    /// entitlement this module has just recomputed.
    /// </summary>
    public static string? PlanShiftError(double start, double end, double staffOpen, double staffClose)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end)) return "needs a start and an end time";
        if (start % Grid.BlockMin != 0 || end % Grid.BlockMin != 0) return "times must land on 15-minute boundaries";
        if (end <= start) return "must finish after it starts";
        if (end - start < MinPlanShiftMin) return $"must be at least {Js.NumberToString(MinPlanShiftMin / 60)} hours long";
        // Staff hours, not trading hours: pre-open unload and post-close pack-down
        // are legitimate plan time.
        if (staffOpen > 0 && start < staffOpen) return "starts before the store's staffed day";
        if (staffClose > 0 && end > staffClose) return "finishes after the store's staffed day";
        return null;
    }

    // ─── The order of a day's rows ───────────────────────────────────────────
    // The plan lists its shifts in hierarchy order: managers first (opener, then
    // closer), then store assistants, then juniors, each by start time. An edit
    // keeps that order exactly - a row a manager clicked stays where it was after
    // "Save changes", so the next click lands on the same person - and a new row
    // goes where the hierarchy order puts it.
    private static double RoleRank(string planRole) => planRole switch
    {
        RoleBadges.Mgr => 0,
        RoleBadges.Sa => 1,
        RoleBadges.Jsa => 2,
        _ => double.NaN, // undefined in the TypeScript
    };

    /// <summary><paramref name="shifts"/> with <paramref name="added"/> put in its hierarchy place.</summary>
    public static List<T> PlaceNewShift<T>(IReadOnlyList<T> shifts, T added) where T : IPlanShiftTimes
    {
        var r = RoleRank(added.PlanRole);
        var at = -1;
        for (var i = 0; i < shifts.Count; i++)
        {
            var s = shifts[i];
            var sr = RoleRank(s.PlanRole);
            if (sr > r || (sr == r && (s.Start > added.Start || (s.Start == added.Start && s.End > added.End))))
            {
                at = i;
                break;
            }
        }
        var out_ = shifts.ToList();
        if (at < 0) out_.Add(added);
        else out_.Insert(at, added);
        return out_;
    }

    /// <summary>
    /// A day's shifts after an edit, in the order the day had before it (by
    /// SlotId); shifts it did not have are placed by the hierarchy order.
    /// </summary>
    public static List<T> KeepShiftOrder<T>(IReadOnlyList<string> before, IReadOnlyList<T> shifts) where T : IPlanShiftSlot
    {
        var pos = new JsMap<string, double>();
        for (var i = 0; i < before.Count; i++) pos.Set(before[i], i);
        var kept = Js.Sorted(shifts.Where(s => pos.Has(s.SlotId)), (a, b) => pos.Get(a.SlotId) - pos.Get(b.SlotId));
        var added = Js.Sorted(
            shifts.Where(s => !pos.Has(s.SlotId)),
            (a, b) => (RoleRank(a.PlanRole) - RoleRank(b.PlanRole)).Or(a.Start - b.Start).Or(a.End - b.End));
        return added.Aggregate(kept, (out_, s) => PlaceNewShift(out_, s));
    }
}
