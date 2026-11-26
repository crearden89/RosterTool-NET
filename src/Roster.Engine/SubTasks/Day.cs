// Port of the pure part of src/lib/subtasks/day.ts. Its reads - the load
// board's averages for the fortnight, the rule overrides and the Reset tasks'
// saved mappings - are the data layer's; it builds the context with ContextFor
// and calls these.
//
// Used when a plan or a roster is generated, when a held plan or roster from
// before sub-tasks is shown, when an edit is saved, and when a published
// roster is printed.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.SubTasks;

public static class Day
{
    /// <summary>
    /// The labels' context for a date: the load board's average day for its
    /// weekday (null when there is none), the share settings, and the Reset
    /// tasks' saved mappings by task name (an empty list when the catalogue
    /// could not be read - a label must never stop a plan: every task by its name).
    /// </summary>
    public static LabelContext ContextFor(
        DateOnly date,
        ResetSource? stage1,
        LoadBoardAverages? load,
        RosteringRules rules,
        IEnumerable<(string Name, string? SubTask)>? resetTasks)
    {
        var dow = Period.DowMon0(date);
        return new LabelContext
        {
            Date = date,
            Stage1 = stage1,
            Load = load is not null && dow < load.ByDow.Count ? load.ByDow[dow] : null,
            Rules = rules,
            ResetMapping = new JsMap<string, string?>((resetTasks ?? []).Select(t => (t.Name, t.SubTask))),
        };
    }

    /// <summary>labelPlanDay: the plan's tiles labelled (Label.LabelShifts).</summary>
    public static List<PlanShift> LabelPlanDay(IReadOnlyList<PlanShift> shifts, LabelContext ctx) => Label.LabelShifts(shifts, ctx);

    /// <summary>
    /// A roster day's tiles labelled from its held plan. The owner: the plan's
    /// sub-tasks carry to the roster (15/11/2026), on every day (26/11/2026), not
    /// only on a day whose plan a manager relabelled or edited. Every roster tile
    /// that comes unchanged from the plan keeps the plan's label (Carry), whether
    /// the rules or a manager gave it; the rest are labelled by the rules, which
    /// keep the carried labels and share out the others. (Left to themselves the
    /// rules hand sub-tasks out person by person over the roster's own tiles and
    /// order, so on many days they gave some tiles the roster kept other labels
    /// than the plan's: the plan's labels now win there.)
    /// </summary>
    public static List<Shift> LabelRosterDay(IReadOnlyList<CarryShift> plan, IReadOnlyList<Shift> roster, LabelContext ctx) =>
        Label.LabelShifts(Carry.CarryLabels(plan, roster), ctx);

    /// <inheritdoc cref="LabelRosterDay(IReadOnlyList{CarryShift}, IReadOnlyList{Shift}, LabelContext)"/>
    public static List<Shift> LabelRosterDay(IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> roster, LabelContext ctx) =>
        LabelRosterDay(plan.Select(CarryShift.Of).ToList(), roster, ctx);

    /// <inheritdoc cref="LabelRosterDay(IReadOnlyList{CarryShift}, IReadOnlyList{Shift}, LabelContext)"/>
    public static List<CarryShift> LabelRosterDay(IReadOnlyList<CarryShift> plan, IReadOnlyList<CarryShift> roster, LabelContext ctx) =>
        Label.LabelShifts(Carry.CarryLabels(plan, roster), ctx, s => s.Blocks, (s, b) => s with { Blocks = b });

    /// <summary>
    /// A roster day held or published before sub-tasks, labelled as it is read,
    /// the way a roster is labelled when it is built (LabelRosterDay): the held
    /// plan's labels on the tiles that come unchanged from it, the rules for the
    /// rest. Only a day with a tile of the four groups unlabelled is labelled; a
    /// labelled day is left exactly as held (the shifts themselves are returned).
    /// </summary>
    public static IReadOnlyList<Shift> WithDayLabels(IReadOnlyList<CarryShift> plan, IReadOnlyList<Shift> shifts, LabelContext ctx) =>
        Label.NeedsLabels(shifts) ? LabelRosterDay(plan, shifts, ctx) : shifts;

    /// <inheritdoc cref="WithDayLabels(IReadOnlyList{CarryShift}, IReadOnlyList{Shift}, LabelContext)"/>
    public static IReadOnlyList<Shift> WithDayLabels(IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> shifts, LabelContext ctx) =>
        WithDayLabels(plan.Select(CarryShift.Of).ToList(), shifts, ctx);

    /// <inheritdoc cref="WithDayLabels(IReadOnlyList{CarryShift}, IReadOnlyList{Shift}, LabelContext)"/>
    public static IReadOnlyList<CarryShift> WithDayLabels(IReadOnlyList<CarryShift> plan, IReadOnlyList<CarryShift> shifts, LabelContext ctx) =>
        Label.NeedsLabels(shifts.Select(s => s.Blocks)) ? LabelRosterDay(plan, shifts, ctx) : shifts;
}
