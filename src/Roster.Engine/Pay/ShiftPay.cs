// Port of src/lib/payroll/shiftPay.ts.
// ─── What one worked shift costs ─────────────────────────────────────────────
// The single costing of a shift, used by the Roster page's costs
// (Pay/ShiftCost) and by Analysis (Analysis/AnalysisCompute), so the two
// price every shift - an RSM's included - the same way. Pure.
//
//   - The rate is the employee's position's (PayKeyFor). An RSM has no rate of
//     its own: it is paid the Store Assistant rate (RsmRatePosition), plus
//     the Pay Rates "RSM allowance" per hour for each quarter-hour in which it
//     is the only manager on the floor.
//   - A position with no row of its own for an hour and day type falls back
//     through its coarser forms ("SM-45-3" -> "SM-45" -> "SM"); with none at
//     all the block costs $0.
//   - Every quarter-hour on the clock is paid at its hour's rate times its
//     overtime / double-time multiplier (Pay/PayTiers); only the UNPAID part
//     of the break is deducted, at the rate of the hour the break sits in
//     (paid breaks stay on the clock).

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

/// <summary>The rate of an hour for a pay key on one day type, with the coarser-form fallback.</summary>
public delegate double RateFor(string dayType, double hour, string payKey);

/// <summary>What a shift costs (shiftPay's ShiftPay).</summary>
/// <param name="Cost">The shift's wages: every block on the clock, the RSM allowance, less the unpaid break.</param>
/// <param name="Premium">The overtime and double-time premium within it, above the ordinary rate.</param>
/// <param name="Allowance">The RSM sole-manager allowance within it.</param>
/// <param name="PaidHours">Hours paid: on the clock, less the unpaid break.</param>
public sealed record ShiftPayResult(double Cost, double Premium, double Allowance, double PaidHours);

public static class ShiftPay
{
    public static readonly IReadOnlyList<string> ManagerRoles = [Roles.SM, Roles.ASM, Roles.DSM, Roles.RSM];

    /// <summary>rateLookup: the pay-rate grid's rows as a <see cref="RateFor"/>.</summary>
    public static RateFor RateLookup(IEnumerable<PayRateRow> rows)
    {
        var rates = new JsMap<string, double>();
        foreach (var r in rows) rates.Set($"{r.DayType}:{Js.NumberToString(r.Hour)}:{r.Role}", r.Rate);
        return (dayType, hour, payKey) =>
        {
            var parts = payKey.Split('-');
            for (var n = parts.Length; n > 0; n--)
                if (rates.TryGet($"{dayType}:{Js.NumberToString(hour)}:{string.Join("-", parts.Take(n))}", out var hit)) return hit;
            return 0;
        };
    }

    /// <summary>
    /// Managers on the floor in each quarter-hour, from the shifts people work: an
    /// open shift has nobody on it, so callers leave unfilled shifts out.
    /// </summary>
    public static JsMap<double, double> ManagersOnFloor(IEnumerable<(string Role, double Start, double End)> shifts)
    {
        var out_ = new JsMap<double, double>();
        foreach (var s in shifts)
        {
            if (!ManagerRoles.Contains(s.Role)) continue;
            for (var m = s.Start; m < s.End; m += Grid.BlockMin) out_.Set(m, out_.GetOr(m, 0) + 1);
        }
        return out_;
    }

    /// <summary>
    /// shiftPay: what one shift costs for the employee working it (their position, and the
    /// SM/ASM band for rows predating positions; null for nobody known).
    /// </summary>
    /// <param name="multByMinute">Overtime / double-time multiplier by block start (absent = ordinary time).</param>
    /// <param name="managers">Managers on the floor by block start (<see cref="ManagersOnFloor"/>).</param>
    /// <param name="rsmAllowance">Pay Rates' RSM allowance, $ per hour.</param>
    public static ShiftPayResult PayFor(
        string role,
        double start,
        double end,
        string? positionCode,
        double? smYearBand,
        string dayType,
        RateFor rateFor,
        JsMap<double, double> multByMinute,
        JsMap<double, double> managers,
        double rsmAllowance)
    {
        var key = Positions.PayKeyFor(positionCode, role, smYearBand);
        var brk = Breaks.BreakForRole(role, (end - start) / 60);
        var windows = Display.BreakWindows(start, end, brk);
        var breakHour = Math.Floor((windows.Count > 0 ? windows[0].Start : start) / 60);
        double cost = 0, premium = 0, allowance = 0, onClock = 0;
        for (var m = start; m < end; m += Grid.BlockMin)
        {
            var rate = rateFor(dayType, Math.Floor(m / 60), key);
            var mult = multByMinute.GetOr(m, 1);
            var blockCost = rate * mult * (Grid.BlockMin / 60);
            premium += rate * (Grid.BlockMin / 60) * (mult - 1);
            if (role == Roles.RSM && managers.GetOr(m, 0) == 1)
            {
                var a = rsmAllowance * (Grid.BlockMin / 60);
                blockCost += a;
                allowance += a;
            }
            cost += blockCost;
            onClock += Grid.BlockMin / 60;
        }
        cost -= rateFor(dayType, breakHour, key) * (brk.UnpaidMinutes / 60);
        return new ShiftPayResult(cost, premium, allowance, onClock - brk.UnpaidMinutes / 60);
    }
}
