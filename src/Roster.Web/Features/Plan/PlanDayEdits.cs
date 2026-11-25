// Port of the pure half of editPlanDay in src/app/(app)/plan/generate/actions.ts: the
// checks on the edits, the rebuilt shifts, the break check and the re-check of the day.
// The reads and writes around it (the held snapshot, the labels, the review lapse, the
// audit row) are the service's.

using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Shaper;
using Roster.Engine.SubTasks;

namespace Roster.Web.Features.Plan;

public static class PlanDayEdits
{
    // Manual plan edits (Plan view edit mode) on the HELD snapshot for one day - both the
    // SHIFT STRUCTURE (times, added shifts, removed shifts) and the painted tiles, in one
    // write so a resize and a repaint can't half-apply. Guardrails: a tile can only take a
    // real paintable group ("flex" for erased, "break" so breaks can be MOVED); every shift
    // must sit on the 15-minute grid, clear the 3-hour minimum and stay inside the store's
    // staffed day; and the day is rejected outright unless every shift still carries
    // EXACTLY its break entitlement. Edits live in the held snapshot until the next
    // Regenerate, which re-solves the plan from demand and replaces them.
    public static readonly IReadOnlySet<string> Paintable = new HashSet<string>
    {
        "registers", "scos", "load_running", "reset", "admin", "specials", "cleaning", "non_op", "break", "flex",
    };

    private static string Hhmm(double min) => Grid.MinToSqlTime(min)[..5];

    /// <summary>The edits applied to the held day's shifts, before the labels: the shifts in their order and how many things changed.</summary>
    public sealed record Rebuilt(List<PlanShift> Shifts, int Changed);

    /// <summary>
    /// Check the edits and rebuild the day's shifts from them. Returns the refusal, or the
    /// shifts (in the order the day had, an added shift where the hierarchy puts it).
    /// </summary>
    public static (string? Error, Rebuilt? Result) Rebuild(PlanSnapshotDay day, IReadOnlyList<PlanShiftEdit> edits)
    {
        var plan = day.Plan ?? throw new ArgumentException("the day has no plan", nameof(day));
        // Structural validation FIRST, so a bad span never reaches the snapshot.
        var staffOpen = day.Result.StaffOpen;
        var staffClose = day.Result.StaffClose;
        foreach (var e in edits)
        {
            if (PlanEdit.PlanShiftError(e.Start, e.End, staffOpen, staffClose) is { } problem)
                return ($"{e.PlanRole} {Hhmm(e.Start)}-{Hhmm(e.End)} {problem}", null);
        }

        // A tile may carry a sub-task of its own group, and nothing else.
        foreach (var e in edits)
        {
            foreach (var b in e.Blocks)
            {
                if (b.Sub is null || Catalogue.IsSubtaskOf(b.Group, b.Sub)) continue;
                var sub = Catalogue.SubtaskByKey.GetValueOrDefault(b.Sub);
                return ($"{e.PlanRole} {Hhmm(e.Start)}-{Hhmm(e.End)}: the tile at {Hhmm(b.Time)} is labelled {(sub is not null ? $"\"{sub.Label}\"" : "with an unknown sub-task")}, which is not part of its labour group", null);
            }
        }

        var before = new JsMap<string, PlanShift>();
        foreach (var s in plan.Shifts) before.Set(s.SlotId, s);
        var next = new List<PlanShift>();
        var changed = 0;

        foreach (var e in edits)
        {
            var prev = before.Get(e.SlotId);
            // A resized or brand-new shift is rebuilt from the shared helper (carrying
            // painted work, re-placing the break for the new length); an untouched one keeps
            // its blocks so painting alone stays a pure recolour. (An untouched one keeps its
            // role too, as the TypeScript's { ...prev } does.)
            var moved = prev is null || prev.Start != e.Start || prev.End != e.End;
            var shift = moved
                ? PlanEdit.ReshapePlanShift(prev, e.SlotId, e.PlanRole, e.Start, e.End)
                : prev! with { Blocks = prev!.Blocks.Select(b => b with { }).ToList() };
            if (moved) changed++;

            // Apply the painted groups on top, for times the shift actually has. A tile sent
            // without a sub-task is labelled by the normal rules afterwards.
            var incoming = new JsMap<double, PlanBlockEdit>();
            foreach (var b in e.Blocks) incoming.Set(b.Time, b);
            for (var i = 0; i < shift.Blocks.Count; i++)
            {
                var b = shift.Blocks[i];
                if (!incoming.TryGet(b.Time, out var want) || !Paintable.Contains(want.Group)) continue;
                var sub = Catalogue.IsSubtaskOf(want.Group, want.Sub) ? want.Sub : null;
                if (want.Group == b.Group && sub == b.Sub) continue;
                changed++;
                shift.Blocks[i] = b with { Group = want.Group, Sub = sub };
            }
            next.Add(shift);
        }

        changed += before.Size - edits.Count(e => before.Has(e.SlotId)); // removals
        if (next.Count == 0) return ("A day needs at least one shift.", null);
        // The rows keep the order they had (managers, then store assistants, then
        // juniors); an added shift goes where that order puts it.
        return (null, new Rebuilt(PlanEdit.KeepShiftOrder([.. before.Keys()], next), changed));
    }

    /// <summary>
    /// Break requirements are non-negotiable: every shift must still hold its exact
    /// entitlement (minutes AND segment structure) or nothing is saved.
    /// </summary>
    public static string? BreakError(IEnumerable<PlanShift> shifts)
    {
        var violations = new List<string>();
        foreach (var s in shifts)
        {
            var times = s.Blocks.Where(b => b.Group == TaskGroups.Break).Select(b => b.Time);
            if (Roster.Engine.Pay.Breaks.BreakTileError(times, s.BreakMinutes, s.Start, s.End) is { } problem)
                violations.Add($"{s.PlanRole} {Hhmm(s.Start)}-{Hhmm(s.End)} {problem}");
        }
        return violations.Count > 0 ? $"Break requirements not met: {string.Join("; ", violations)}" : null;
    }

    /// <summary>
    /// Every edit re-checks the whole day from the shifts as they now stand - cover, who may
    /// do each task, windows, shift lengths, the tills, work on nobody's shift, the hours -
    /// and refreshes its flags and status (owner decision R4). The edit saves; whatever it
    /// breaks is flagged. The planner's notes on what it did are kept as they were.
    /// </summary>
    public static PlanSnapshotDay Recheck(PlanSnapshotDay day, List<PlanShift> shifts, DayCheckOptions options)
    {
        var plan = day.Plan!;
        var notes = plan.Notes ?? [];
        var check = DayChecker.CheckPlanDay(
            day.Result,
            shifts.Select(s => s.ToChecked()).ToList(),
            options with { BreakTimeAdded = BreakTime.BreakTimeAdded(notes) });
        var newPlan = plan with
        {
            Shifts = shifts,
            Notes = notes,
            Checks = check.Flags,
            Hours = check.Hours,
            Flags = [.. notes, .. check.Flags],
        };
        return day with
        {
            Plan = newPlan,
            Ok = day.Result.Closed || DayChecker.DayIsOk([.. day.Result.Flags, .. newPlan.Flags]),
        };
    }
}
