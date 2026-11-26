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
    /// Per-shift labour cost for a store/date's roster, at the Pay Rates grid's
    /// $/hour by position, priced by Pay/ShiftPay exactly as Analysis prices
    /// them: RSM is paid the SA rate plus a per-hour sole-manager allowance for
    /// blocks where they're the only manager on the floor; only the UNPAID
    /// portion of a break is deducted (paid breaks stay on the clock), mirroring
    /// the roster grid's paid-Hrs column. Callers pass the shifts people work.
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

        // A position code falls back through its coarser forms when its own row is
        // missing for an hour/day-type, rather than silently costing the block at
        // $0 (which showed as shifts "with no cost").
        var rateFor = ShiftPay.RateLookup(payRates);
        var empById = new JsMap<string, CostEmployee>();
        foreach (var e in employees) empById.Set(e.Id, e);
        // Managers on the floor per block, so an RSM's sole-manager allowance
        // applies only where they're the only one.
        var managers = ShiftPay.ManagersOnFloor(shifts.Select(s => (s.Role, s.Start, s.End)));

        var costByEmployee = new JsMap<string, double>();
        double dayTotal = 0;
        foreach (var shift in shifts)
        {
            var emp = empById.Get(shift.EmployeeId);
            var cost = ShiftPay.PayFor(shift.Role, shift.Start, shift.End, emp?.PositionCode, emp?.SmYearBand, dayType, rateFor,
                // Overtime / double time for this shift, decided across the whole pay
                // period; blocks not listed are ordinary time.
                PayTiers.TiersFor(tiers, shift.EmployeeId, date, shift.Start).MultByMinute,
                managers,
                paySettings.RsmAllowance).Cost;
            costByEmployee.Set(shift.EmployeeId, costByEmployee.GetOr(shift.EmployeeId, 0) + cost);
            dayTotal += cost;
        }

        return new ShiftCosts(costByEmployee, dayTotal);
    }
}
