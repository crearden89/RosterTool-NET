// Port of the pure part of src/lib/payroll/shiftCost.ts (the caller reads the
// rates, the team, the pay period's tiers, the settings and the day type).

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public sealed record CostableShift(string EmployeeId, string Role, double Start, double End, double BreakMinutes);

/// <summary>The parts of an employee row the cost reads.</summary>
public sealed record CostEmployee(string Id, string? PositionCode, double? SmYearBand);

public sealed record ShiftCosts(JsMap<string, double> CostByEmployee, double DayTotal);

public static class ShiftCost
{
    /// <summary>
    /// Per-shift labour cost for a store/date's roster, at the same $/hour/role
    /// rates used by Pay Rates and the Analysis payroll cost calc: SM/ASM are
    /// banded by pay year (employees.sm_year_band), RSM is paid the SA rate plus
    /// a per-hour sole-manager allowance for blocks where they're the only manager
    /// on the floor. Only the UNPAID portion of a break is deducted from cost (paid
    /// breaks stay on the clock), mirroring the roster grid's paid-Hrs column.
    /// <paramref name="tiers"/> are the pay period's tiers for the roster these
    /// shifts belong to (overtime and double time are PAY-PERIOD rules).
    /// </summary>
    public static ShiftCosts ComputeShiftCosts(
        DateOnly date,
        IReadOnlyList<CostableShift> shifts,
        IEnumerable<PayRateRow> payRates,
        IEnumerable<CostEmployee> employees,
        JsMap<string, ShiftTiers> tiers,
        PaySettings paySettings,
        string dayType)
    {
        if (shifts.Count == 0) return new ShiftCosts(new JsMap<string, double>(), 0);

        var rateMap = new JsMap<string, double>();
        foreach (var r in payRates) rateMap.Set($"{r.DayType}:{Js.NumberToString(r.Hour)}:{r.Role}", r.Rate);
        // A position code falls back through its coarser forms when its own row is
        // missing for an hour/day-type ("SM-45-3" → "SM-45" → "SM"), rather than
        // silently costing the block at $0 (which showed as shifts "with no cost").
        double RateFor(double hour, string position)
        {
            var parts = position.Split('-');
            for (var n = parts.Length; n > 0; n--)
                if (rateMap.TryGet($"{dayType}:{Js.NumberToString(hour)}:{string.Join("-", parts.Take(n))}", out var hit)) return hit;
            return 0;
        }
        var empById = new JsMap<string, CostEmployee>();
        foreach (var e in employees) empById.Set(e.Id, e);

        // Managers on the floor per block, so an RSM's sole-manager allowance
        // applies only where they're the only one (mirrors the Analysis calc).
        var mgrAtBlock = new JsMap<double, double>();
        foreach (var shift in shifts)
        {
            if (!Roles.IsManager(shift.Role)) continue;
            for (var m = shift.Start; m < shift.End; m += Grid.BlockMin) mgrAtBlock.Set(m, mgrAtBlock.GetOr(m, 0) + 1);
        }

        var costByEmployee = new JsMap<string, double>();
        double dayTotal = 0;
        foreach (var shift in shifts)
        {
            var emp = empById.Get(shift.EmployeeId);
            var rateRole = Positions.PayKeyFor(emp?.PositionCode, shift.Role, emp?.SmYearBand);
            // Pay every worked block, then deduct only the UNPAID break minutes at the
            // rate of the hour the break sits in (paid breaks remain on the clock).
            var brk = Breaks.BreakForRole(shift.Role, (shift.End - shift.Start) / 60);
            var windows = Display.BreakWindows(shift.Start, shift.End, brk);
            var breakHour = Math.Floor((windows.Count > 0 ? windows[0].Start : shift.Start) / 60);
            // Overtime / double time for this shift, decided across the whole pay
            // period; blocks not listed are ordinary time.
            var tier = PayTiers.TiersFor(tiers, shift.EmployeeId, date, shift.Start);
            double shiftCost = 0;
            for (var m = shift.Start; m < shift.End; m += Grid.BlockMin)
            {
                var hour = Math.Floor(m / 60);
                var mult = tier.MultByMinute.GetOr(m, 1);
                var blockCost = RateFor(hour, rateRole) * mult * (Grid.BlockMin / 60);
                if (shift.Role == Roles.RSM && mgrAtBlock.GetOr(m, 0) == 1) blockCost += paySettings.RsmAllowance * (Grid.BlockMin / 60);
                shiftCost += blockCost;
            }
            shiftCost -= RateFor(breakHour, rateRole) * (brk.UnpaidMinutes / 60);
            costByEmployee.Set(shift.EmployeeId, costByEmployee.GetOr(shift.EmployeeId, 0) + shiftCost);
            dayTotal += shiftCost;
        }

        return new ShiftCosts(costByEmployee, dayTotal);
    }
}
