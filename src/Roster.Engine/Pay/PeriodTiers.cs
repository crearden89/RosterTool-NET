// Port of the pure part of src/lib/payroll/periodTiers.ts.
// Pay tiers for a whole fortnight.
//
// Three of the four pay rules cannot be decided from a single day - the 76h
// period threshold needs the running total, and the short-break double time
// needs the PREVIOUS day's finish - so every costing surface has to tier the
// whole pay period even when it only wants one day out of it.
//
// A page showing a HELD roster tiers that roster (TierRosterPeriod): its own
// shifts, manual edits and locked shifts included, without solving the whole
// fortnight again. (getPeriodTiers - the roster the engine builds from fresh
// slots - is the roster engine's call followed by this.)

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public static class PeriodTiers
{
    /// <summary>The settings tierPayPeriod applies to one role.</summary>
    public static PayRuleSettings RulesFor(string role, IReadOnlyDictionary<string, RolePayRule> roleRules, PaySettings paySettings)
    {
        roleRules.TryGetValue(role, out var r);
        return new PayRuleSettings
        {
            DailyOtAfterPaidHours = r?.DailyOtAfterPaidHours,
            PeriodOtAfterPaidHours = r?.PeriodOtAfterPaidHours,
            ShortBreakHours = r?.ShortBreakHours,
            SundayPremiumFromMin = paySettings.SundayOtFromHour * 60,
            SundayPremiumToMin = paySettings.SundayOtToHour * 60,
            Ot15Multiplier = paySettings.Overtime15Multiplier,
            Ot19Multiplier = paySettings.Overtime19Multiplier,
            DoubleTimeMultiplier = paySettings.DoubleTimeMultiplier,
        };
    }

    /// <summary>Pay tiers for the given fortnight of rostered days (open shifts have no wage).</summary>
    public static JsMap<string, ShiftTiers> TierRosterPeriod(
        IEnumerable<(DateOnly Date, IEnumerable<Shift> Shifts)> byDate,
        IReadOnlyDictionary<string, RolePayRule> roleRules,
        PaySettings paySettings)
    {
        var shifts = new List<TierShift>();
        foreach (var (date, roster) in byDate)
        {
            foreach (var s in roster)
            {
                if (s.Unfilled) continue; // an open slot has no employee and no wage
                shifts.Add(new TierShift
                {
                    EmployeeId = s.EmployeeId,
                    Role = s.Role,
                    Date = date,
                    Start = s.Start,
                    End = s.End,
                    UnpaidBreakMin = Breaks.BreakForRole(s.Role, (s.End - s.Start) / 60).UnpaidMinutes,
                });
            }
        }
        return PayTiers.TierPayPeriod(shifts, role => RulesFor(role, roleRules, paySettings));
    }
}
