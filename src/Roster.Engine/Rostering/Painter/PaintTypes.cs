// Port of the painter's types in src/lib/stage2/engine.ts: its options and its
// result (the painter itself is TaskPainter.cs). The shared vocabulary - roles,
// task groups, eligibility, tiles and paintable shifts - is Core's
// (EngineTypes.cs); the demand curve is the shaper's DemandBlock.
//
// JSON (RosterJson): the TypeScript field names, camelCase.

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.Rostering.Painter;

/// <summary>
/// The manager pair's rules a trim may never break (owner decisions 5 and R7):
/// the handover's minimum, and the shortest manager shift (opts.handover).
/// </summary>
public sealed record PaintHandover(double MinMin, double ManagerMinShiftMin);

/// <summary>
/// The floor-cover rules a trim may never break (the Rostering rules page;
/// opts.cover). The defaults when omitted.
/// </summary>
public sealed record PaintCover(double MinPeople, double MinAdultsWithJunior);

/// <summary>paintTasks' options (opts).</summary>
public sealed record PaintOptions
{
    // Operating rule: breaks are done by 18:00 unless the store trades to
    // 21:00 (the caller decides and passes the cut-off, or null). A
    // PREFERENCE, not a bar - a shift whose only legal break span sits after
    // the cut-off still gets its award break, late.
    public double? BreakEndBy { get; init; }

    // The windows of the group's NAMED tasks (Stage 1's admin and reset
    // catalogues), when it has any. The make-up pass may only rescue work
    // into a block that shares a task window with the block that demanded it
    // - see sameTaskWindow. Keyed by task group.
    public JsMap<string, List<MinuteWindow>>? TaskWindows { get; init; }

    // The job window of each demanded quarter-hour, where Stage 1 knows it
    // (Admin): work displaced from `from` is made up only inside the window
    // of the job it belongs to (owner decision 11, strict for Admin). Keyed by
    // task group, then by the quarter-hour's minute.
    public JsMap<string, JsMap<double, List<JobWindow>>>? UnitWindows { get; init; }

    // The manager pair's rules a trim may never break (owner decisions 5 and
    // R7): the handover's minimum, and the shortest manager shift.
    public PaintHandover? Handover { get; init; }

    // Monday's date checks: their window is a deadline (A12; decision 3), so
    // Admin painted inside it stays inside it.
    public MinuteWindow? AdminDeadline { get; init; }

    // The floor-cover rules a trim may never break (the Rostering rules
    // page); the defaults when omitted.
    public PaintCover? Cover { get; init; }
}

/// <summary>
/// Quarter-hours of a group's demand that no tile carries: work a break
/// displaced with nobody free inside its window to make it up, or work on an
/// open shift. Lost, not moved - for the caller to flag (TaskPainter.LostWorkFlag).
/// </summary>
/// <param name="Group">A task group or "non_op".</param>
/// <param name="Blocks">Quarter-hours lost.</param>
public sealed record LostWork(string Group, double Blocks);

/// <summary>Blank quarter-hours left in one shift: <c>{ shift, times }</c>.</summary>
public sealed record ShiftBlanks<S>(S Shift, List<double> Times) where S : PaintableShift;

/// <summary>
/// What painting could not finish: blank quarter-hours that could only have
/// been cleared by breaking a rule (see the no-blank pass), for the caller to
/// flag by name, and the work no tile carries.
/// </summary>
public sealed record PaintResult<S> where S : PaintableShift
{
    /// <summary>
    /// Quarter-hours of each group's demand that no tile carries, in the order
    /// the groups first appear in the day's demand.
    /// </summary>
    public List<LostWork> Lost { get; init; } = new();

    /// <summary>Each shift still holding blank (flex) quarter-hours, with their minutes.</summary>
    public List<ShiftBlanks<S>> Blanks { get; init; } = new();

    /// <summary>
    /// True when the no-blank pass ran past its work limit (EXACT_PASS_WORK) and
    /// finished on its bounded search: some of the blanks might have been
    /// clearable. It does not happen on an ordinary day. (Absent from the JSON
    /// when false, as in the TypeScript.)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SearchCapped { get; init; }
}
