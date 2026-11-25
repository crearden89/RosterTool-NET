// Port of the reads of src/lib/payroll/shiftCost.ts, idealCost.ts and
// periodTiers.ts. The costing itself is the engine's ShiftCost, IdealCost and
// PeriodTiers.

using Roster.Data.Services.Cache;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.Pay;

public interface IPayCosts
{
    /// <summary>
    /// computeShiftCosts: each shift's labour cost for a store-day, at the pay-rate grid's
    /// rates, with the pay period's overtime and double time. <paramref name="periodTiers"/>
    /// are the tiers of the roster these shifts belong to (TierRosterPeriodAsync on a held
    /// roster); without them the period is tiered from the roster the engine builds afresh.
    /// </summary>
    Task<ShiftCosts> ComputeShiftCostsAsync(
        Guid storeId, DateOnly date, IReadOnlyList<CostableShift> shifts, JsMap<string, ShiftTiers>? periodTiers = null, CancellationToken ct = default);

    /// <summary>
    /// computeIdealDayCost: the day's theoretical labour-cost floor (spec §6.5), priced from
    /// <paramref name="precomputed"/> (the held plan's Stage 1 record) or a fresh Stage 1.
    /// </summary>
    Task<double> ComputeIdealDayCostAsync(Guid storeId, DateOnly date, Stage1Computation? precomputed = null, CancellationToken ct = default);

    /// <summary>tierRosterPeriod: pay tiers for a fortnight of rostered days (open shifts have no wage).</summary>
    Task<JsMap<string, ShiftTiers>> TierRosterPeriodAsync(IEnumerable<(DateOnly Date, IEnumerable<Shift> Shifts)> byDate, CancellationToken ct = default);

    /// <summary>
    /// getPeriodTiers: pay tiers for the fortnight the engine rosters from fresh slots
    /// (what the Analysis pages show), once per store and period per request.
    /// </summary>
    Task<JsMap<string, ShiftTiers>> GetPeriodTiersAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);
}

/// <summary>The fortnight roster the engine builds from fresh slots, as the pay tiers read it.</summary>
public interface IFreshRosterShifts
{
    /// <summary>computeRosterFortnight(store, period).byDate, each day's shifts.</summary>
    Task<IReadOnlyList<(DateOnly Date, IEnumerable<Shift> Shifts)>> FortnightShiftsAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);
}

public sealed class PayCostsLoader(IRefData refData, IPayData payData, IStage1Loader stage1, IFreshRosterShifts freshRoster) : IPayCosts
{
    // All reference inputs are request-cached - this runs for up to 14 dates per
    // render with no queries of its own.
    public async Task<ShiftCosts> ComputeShiftCostsAsync(
        Guid storeId, DateOnly date, IReadOnlyList<CostableShift> shifts, JsMap<string, ShiftTiers>? periodTiers = null, CancellationToken ct = default)
    {
        if (shifts.Count == 0) return new ShiftCosts(new JsMap<string, double>(), 0);
        var payRates = await payData.GetPayRatesAsync(ct);
        var emps = await refData.GetEmployeesAsync(storeId, ct);
        // Overtime and double time are PAY-PERIOD rules (a 76h fortnight total, a
        // break measured against yesterday's finish), so the whole period is
        // tiered even to cost one day.
        var tiers = periodTiers ?? await GetPeriodTiersAsync(storeId, Period.PeriodStartFor(date), ct);
        var paySettings = await payData.GetPaySettingsAsync(ct);
        var dayType = await payData.DayTypeAsync(storeId, date, ct);
        return ShiftCost.ComputeShiftCosts(
            date, shifts, payRates, emps.Select(e => new CostEmployee(e.Id.ToString("D"), e.PositionCode, e.SmYearBand)), tiers, paySettings, dayType);
    }

    public Task<double> ComputeIdealDayCostAsync(Guid storeId, DateOnly date, Stage1Computation? precomputed = null, CancellationToken ct = default) =>
        RequestMemo.Cached("payroll.computeIdealDayCost", [storeId, date, precomputed], async () =>
        {
            var s1 = precomputed ?? await stage1.ComputeStage1Async(storeId, date, ct);
            if (!s1.HasData) return 0.0;
            var payRates = await payData.GetPayRatesAsync(ct);
            var dayType = await payData.DayTypeAsync(storeId, date, ct);
            return IdealCost.ComputeIdealDayCost(
                s1.HasData, s1.Blocks.Select(b => (b.Group, b.Time, b.Headcount)), s1.StaffOpen, s1.StaffClose, payRates, dayType);
        });

    public async Task<JsMap<string, ShiftTiers>> TierRosterPeriodAsync(IEnumerable<(DateOnly Date, IEnumerable<Shift> Shifts)> byDate, CancellationToken ct = default)
    {
        var roleRules = await refData.GetRolePayRulesAsync(ct);
        var paySettings = await payData.GetPaySettingsAsync(ct);
        return PeriodTiers.TierRosterPeriod(byDate, roleRules, paySettings);
    }

    // This one tiers the roster the engine builds from fresh slots - what the
    // Analysis pages show. A page showing a HELD roster tiers that roster instead
    // (TierRosterPeriodAsync): its own shifts, manual edits and locked shifts
    // included, without solving the whole fortnight again.
    public Task<JsMap<string, ShiftTiers>> GetPeriodTiersAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) =>
        RequestMemo.Cached("payroll.getPeriodTiers", [storeId, periodStart], async () =>
            await TierRosterPeriodAsync(await freshRoster.FortnightShiftsAsync(storeId, periodStart, ct), ct));
}
