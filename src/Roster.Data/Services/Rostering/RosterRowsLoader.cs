// Port of the reads of src/lib/roster/server.ts (tillByTimeFor,
// computeRosterRows, computeFortnightCostDelta). The rows and the delta are the
// engine's RosterOps.Server.

using Roster.Data.Services.Pay;
using Roster.Data.Services.Planning;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;

namespace Roster.Data.Services.Rostering;

public interface IRosterRows
{
    /// <summary>tillByTimeFor: the till's MCO/SCO counts by block minute (the Registers/SCO split of Checkout tiles).</summary>
    Task<JsMap<double, (double Mco, double Sco)>> TillByTimeForAsync(Guid storeId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// computeRosterRows: the Roster view's named rows for a store-day with their costs, the
    /// ideal cost and the lock rows. <paramref name="precomputed"/> is the held roster's day
    /// (otherwise the fresh solve); <paramref name="stage1"/> the held plan's Stage 1 record
    /// (otherwise Stage 1 is solved again); <paramref name="tiers"/> the pay period's tiers.
    /// </summary>
    Task<RosterRows> ComputeRosterRowsAsync(
        Guid storeId, DateOnly date, RosterComputation? precomputed = null, Stage1Computation? stage1 = null,
        JsMap<string, ShiftTiers>? tiers = null, CancellationToken ct = default);

    /// <summary>
    /// computeFortnightCostDelta (spec §6.5): the theoretical ideal floor against the roster's
    /// actual cost over the period.
    /// </summary>
    Task<CostDelta> ComputeFortnightCostDeltaAsync(
        Guid storeId, JsMap<DateOnly, RosterComputation> byDate, IReadOnlyDictionary<DateOnly, Stage1Computation>? stage1ByDate = null,
        JsMap<string, ShiftTiers>? tiers = null, CancellationToken ct = default);
}

public sealed class RosterRowsLoader(IRosterLoader rosters, ITillLoader tills, IPayCosts costs) : IRosterRows
{
    internal static List<CostableShift> Costable(IEnumerable<Shift> shifts) =>
        // Unfilled (open) shifts have no employee and no wage - never costed.
        Server.CostedShifts(shifts).Select(s => new CostableShift(s.EmployeeId, s.Role, s.Start, s.End, s.BreakMinutes)).ToList();

    public async Task<JsMap<double, (double Mco, double Sco)>> TillByTimeForAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        Server.TillByTimeFor(await tills.ComputeTillForDateAsync(storeId, date, ct));

    public async Task<RosterRows> ComputeRosterRowsAsync(
        Guid storeId, DateOnly date, RosterComputation? precomputed = null, Stage1Computation? stage1 = null,
        JsMap<string, ShiftTiers>? tiers = null, CancellationToken ct = default)
    {
        var roster = precomputed ?? await rosters.ComputeRosterAsync(storeId, date, ct);
        if (!roster.HasData) return RosterRows.Empty();
        var till = await tills.ComputeTillForDateAsync(storeId, date, ct);
        var dayCosts = await costs.ComputeShiftCostsAsync(storeId, date, Costable(roster.Shifts), tiers, ct);
        var ideal = await costs.ComputeIdealDayCostAsync(storeId, date, stage1, ct);
        return Server.ComputeRosterRows(roster, till, dayCosts, ideal);
    }

    public async Task<CostDelta> ComputeFortnightCostDeltaAsync(
        Guid storeId, JsMap<DateOnly, RosterComputation> byDate, IReadOnlyDictionary<DateOnly, Stage1Computation>? stage1ByDate = null,
        JsMap<string, ShiftTiers>? tiers = null, CancellationToken ct = default)
    {
        var perDay = new List<(double DayIdeal, double DayTotal)>();
        foreach (var (date, r) in byDate)
        {
            if (!r.HasData) continue;
            var dayIdeal = await costs.ComputeIdealDayCostAsync(storeId, date, stage1ByDate is not null && stage1ByDate.TryGetValue(date, out var s1) ? s1 : null, ct);
            var dayTotal = (await costs.ComputeShiftCostsAsync(storeId, date, Costable(r.Shifts), tiers, ct)).DayTotal;
            perDay.Add((dayIdeal, dayTotal));
        }
        return Server.FortnightCostDelta(perDay);
    }
}

/// <summary>The fresh fortnight roster's shifts, as the pay period's tiers read them (getPeriodTiers).</summary>
public sealed class FreshRosterShifts(IRosterLoader rosters) : IFreshRosterShifts
{
    public async Task<IReadOnlyList<(DateOnly Date, IEnumerable<Shift> Shifts)>> FortnightShiftsAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var fortnight = await rosters.ComputeRosterFortnightAsync(storeId, periodStart, null, ct);
        return fortnight.ByDate.Select(kv => (kv.Key, (IEnumerable<Shift>)kv.Value.Shifts)).ToList();
    }
}
