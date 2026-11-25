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
    /// A day's tiles with their labels, labelling only when some tile of the four
    /// groups has none (a plan or roster held or published before sub-tasks).
    /// Labels the tiles already carry are kept; the rest are given by the normal
    /// rules. Returns the shifts themselves when nothing needs a label.
    /// </summary>
    public static IReadOnlyList<PlanShift> WithDayLabels(IReadOnlyList<PlanShift> shifts, LabelContext ctx) =>
        Label.NeedsLabels(shifts) ? Label.LabelShifts(shifts, ctx) : shifts;

    /// <inheritdoc cref="WithDayLabels(IReadOnlyList{PlanShift}, LabelContext)"/>
    public static IReadOnlyList<Shift> WithDayLabels(IReadOnlyList<Shift> shifts, LabelContext ctx) =>
        Label.NeedsLabels(shifts) ? Label.LabelShifts(shifts, ctx) : shifts;

    /// <inheritdoc cref="WithDayLabels(IReadOnlyList{PlanShift}, LabelContext)"/>
    public static IReadOnlyList<IReadOnlyList<DisplayBlock>> WithDayLabels(IReadOnlyList<IReadOnlyList<DisplayBlock>> shifts, LabelContext ctx) =>
        Label.NeedsLabels(shifts) ? Label.LabelBlocks(shifts, ctx) : shifts;

    /// <summary>
    /// A roster day's tiles labelled from its held plan (owner, 15/11/2026: the
    /// plan's sub-tasks carry to the roster). Where the plan's labels are not the
    /// ones the rules give its tiles - a manager relabelled a tile, or edited the
    /// day - every roster tile that comes unchanged from the plan keeps the plan's
    /// label (Carry), and the rest are labelled by the rules. A plan the rules
    /// labelled gives the roster the rules' labels, as before: the rules hand
    /// sub-tasks out person by person over the roster's own tiles and order, and
    /// on about half the days of the test fortnights that gives some tiles the
    /// roster kept other labels than the plan's, so carrying them would change
    /// those rosters.
    /// </summary>
    public static List<Shift> LabelRosterDay(IReadOnlyList<CarryShift> plan, IReadOnlyList<Shift> roster, LabelContext ctx)
    {
        var relabelled = plan.Count > 0 &&
            !Carry.SameLabels(Label.LabelShifts(Carry.WithoutLabels(plan), ctx, s => s.Blocks, (s, b) => s with { Blocks = b }), plan);
        return Label.LabelShifts(relabelled ? Carry.CarryLabels(plan, roster) : roster, ctx);
    }

    /// <inheritdoc cref="LabelRosterDay(IReadOnlyList{CarryShift}, IReadOnlyList{Shift}, LabelContext)"/>
    public static List<Shift> LabelRosterDay(IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> roster, LabelContext ctx) =>
        LabelRosterDay(plan.Select(CarryShift.Of).ToList(), roster, ctx);
}
