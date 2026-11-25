// Port of the pure parts of src/lib/roster/server.ts. Its reads - the held
// roster, the day's till, the shift costs and the ideal day cost - are the data
// layer's; it hands their results to these.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Till;

namespace Roster.Engine.RosterOps;

/// <summary>A rostered shift as the Roster page's lock/edit controls read it.</summary>
public sealed record LockRow(string EmployeeId, string Name, string Role, double Start, double End, bool Locked);

/// <summary>Fully-decorated, named roster rows for a store/date (Roster view), with their costs.</summary>
public sealed record RosterRows(
    bool HasData,
    List<RosterRow> Rows,
    List<string> Flags,
    JsMap<string, double> CostByKey,
    double DayTotalCost,
    double IdealCost,
    List<LockRow> LockRows)
{
    public static RosterRows Empty() => new(false, [], [], new JsMap<string, double>(), 0, 0, []);
}

/// <summary>Fortnight cost delta (spec §6.5): the theoretical ideal floor vs the real roster's actual cost.</summary>
public sealed record CostDelta(double Ideal, double Actual, double Delta);

public static class Server
{
    /// <summary>
    /// Till mco/sco counts keyed by block-time minutes - the basis for the
    /// display-only Registers/SCO split of Checkout tiles.
    /// </summary>
    public static JsMap<double, (double Mco, double Sco)> TillByTimeFor(TillComputation till) =>
        new(till.Blocks.Select(b => (Grid.TimeToMin(b.BlockTime), (b.Mco, b.Sco))));

    /// <summary>The shifts the costs are priced on: unfilled (open) shifts have no employee and no wage.</summary>
    public static List<Shift> CostedShifts(IEnumerable<Shift> shifts) => shifts.Where(s => !s.Unfilled).ToList();

    /// <summary>
    /// The Roster view's rows for a day: <paramref name="roster"/> is the day's held roster,
    /// <paramref name="till"/> its till, <paramref name="costs"/> the costs of its <see cref="CostedShifts"/> and
    /// <paramref name="idealCost"/> the ideal day cost. HasData false (and nothing else) when
    /// Stage 2 could not produce a roster.
    /// </summary>
    public static RosterRows ComputeRosterRows(RosterDay roster, TillComputation? till, ShiftCosts? costs, double idealCost)
    {
        if (!roster.HasData) return RosterRows.Empty();
        var tillByTime = till is null ? new JsMap<double, (double Mco, double Sco)>() : TillByTimeFor(till);
        var rows = Display.BuildRosterRows(
            roster.Shifts.Select(s => new RawShift(
                s.EmployeeId, s.Name, s.Role, s.Start, s.End, s.BreakMinutes,
                s.Blocks.Select(b => new RawBlock(b.Time, b.Group, b.Sub)).ToList())).ToList(),
            tillByTime);
        return new RosterRows(
            true,
            rows,
            roster.Flags.ToList(),
            costs?.CostByEmployee ?? new JsMap<string, double>(),
            costs?.DayTotal ?? 0,
            idealCost,
            // Open shifts can't be locked/edited - fill them via Add.
            roster.Shifts.Where(s => !s.Unfilled).Select(s => new LockRow(s.EmployeeId, s.Name, s.Role, s.Start, s.End, s.Locked)).ToList());
    }

    /// <inheritdoc cref="ComputeRosterRows(RosterDay, TillComputation?, ShiftCosts?, double)"/>
    public static RosterRows ComputeRosterRows(RosterComputation roster, TillComputation? till, ShiftCosts? costs, double idealCost) =>
        ComputeRosterRows(RosterDay.Of(roster), till, costs, idealCost);

    /// <summary>
    /// The fortnight's ideal floor against its actual cost, summed across the
    /// days that have a roster: each day's ideal cost and the day total of its
    /// costed shifts.
    /// </summary>
    public static CostDelta FortnightCostDelta(IEnumerable<(double DayIdeal, double DayTotal)> perDay)
    {
        var days = perDay.ToList();
        var ideal = days.Aggregate(0.0, (s, d) => s + d.DayIdeal);
        var actual = days.Aggregate(0.0, (s, d) => s + d.DayTotal);
        return new CostDelta(ideal, actual, actual - ideal);
    }

    /// <summary>
    /// The persisted task-block group for each shift, with Checkout resolved to
    /// the one checkout group - what the Floor Display reads back so it renders
    /// identically without re-running the Till engine.
    /// </summary>
    public static List<List<string>> ResolvePersistableBlocks(IEnumerable<Shift> shifts, object? tillByTime = null) =>
        Display.SplitCheckout(shifts.Select(s => s.Blocks), b => b.Group, tillByTime);
}
