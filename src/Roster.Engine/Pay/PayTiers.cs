// Port of src/lib/payroll/payTiers.ts.
// ─── Pay tiering: which minutes of a fortnight are paid above the base rate ──
// Pure (costing, tests, future payroll export). Four rules, applied to a whole
// PAY PERIOD at once because three of them cannot be decided from a single
// shift in isolation:
//
//   1. 1.5x  - paid hours beyond the role's daily threshold (9 paid hours).
//   2. 1.5x  - every paid hour beyond the period threshold (76 paid hours a
//              fortnight) for the hourly floor roles. Needs the running total
//              across the period, so shifts must be tiered in date order.
//   3. 1.9x  - any of the overtime hours above that fall on a SUNDAY between
//              midnight and 09:00. A time-of-day band, not a longer tail: the
//              same overtime hour is 1.5x on a Tuesday and 1.9x at 05:00 on a
//              Sunday.
//   4. 2.0x  - a shift that starts less than 10 hours after the previous one
//              ended is paid at double time in full, and so is every shift
//              after it until the employee finally gets a 10-hour break.
//              Double time SUPERSEDES overtime; the hours are never stacked.
//
// The Stage 2 engine already caps shifts at the role's paid maximum and
// enforces an 11-hour rest gap, so on a cleanly generated roster rules 1 and 4
// should cost nothing. They earn their keep on MANUAL edits and locked shifts,
// where the roster no longer has to obey the engine's own limits - which is
// exactly when a store needs to see the money.

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public sealed record PayRuleSettings
{
    // Paid hours in ONE shift before the 1.5x tier starts. null = never (the
    // salaried SM/ASM).
    public double? DailyOtAfterPaidHours { get; init; }
    // Paid hours in the whole PAY PERIOD before every further hour is 1.5x.
    public double? PeriodOtAfterPaidHours { get; init; }
    // Overtime inside this Sunday band is paid at the 1.9x multiplier instead.
    public double SundayPremiumFromMin { get; init; }
    public double SundayPremiumToMin { get; init; }
    // A gap shorter than this before a shift makes the whole shift double time.
    // null = the rule does not apply to this role.
    public double? ShortBreakHours { get; init; }
    public double Ot15Multiplier { get; init; }
    public double Ot19Multiplier { get; init; }
    public double DoubleTimeMultiplier { get; init; }
}

public sealed record TierShift
{
    public string EmployeeId { get; init; } = "";
    public string Role { get; init; } = "";
    public DateOnly Date { get; init; }
    public double Start { get; init; } // minutes from midnight
    public double End { get; init; }
    public double UnpaidBreakMin { get; init; }
}

public sealed class ShiftTiers
{
    // Multiplier for each 15-minute block START minute. Only blocks above the
    // base rate appear; anything missing is ordinary time.
    public JsMap<double, double> MultByMinute { get; init; } = new();
    public double Ot15Hours { get; set; }
    public double Ot19Hours { get; set; }
    public double DoubleHours { get; set; }
}

public static class PayTiers
{
    public static string ShiftKey(string employeeId, DateOnly date, double start) =>
        $"{employeeId}:{date.ToIso()}:{Js.NumberToString(start)}";

    private static double DayNumber(DateOnly date) => Period.ToEpochDay(date);

    private static readonly ShiftTiers Empty = new();

    // Tier one pay period's shifts. `rulesFor` supplies the settings per role, so
    // salaried and hourly cohorts can differ. Shifts may arrive in any order;
    // they are sorted per employee before the running totals are walked.
    public static JsMap<string, ShiftTiers> TierPayPeriod(IEnumerable<TierShift> shifts, Func<string, PayRuleSettings> rulesFor)
    {
        var out_ = new JsMap<string, ShiftTiers>();
        var byEmp = new JsMap<string, List<TierShift>>();
        foreach (var s in shifts)
        {
            if (!byEmp.TryGet(s.EmployeeId, out var arr)) byEmp.Set(s.EmployeeId, arr = new List<TierShift>());
            arr.Add(s);
        }

        foreach (var list in byEmp.Values())
        {
            Js.Sort(list, (a, b) => (DayNumber(a.Date) - DayNumber(b.Date)).Or(a.Start - b.Start));
            double periodPaidHours = 0;
            double? prevEndAbs = null;

            foreach (var s in list)
            {
                var r = rulesFor(s.Role);
                var span = s.End - s.Start;
                var paidHours = Math.Max(0, span - s.UnpaidBreakMin) / 60;
                var startAbs = DayNumber(s.Date) * 1440 + s.Start;
                var endAbs = DayNumber(s.Date) * 1440 + s.End;

                // Rule 4 - short break. Judged shift by shift against the PREVIOUS
                // shift's end, so a run of tight turnarounds stays on double time until
                // one of them is finally preceded by a full break.
                var gapHours = prevEndAbs is null ? double.PositiveInfinity : (startAbs - prevEndAbs.Value) / 60;
                var doubleTime = r.ShortBreakHours is not null && gapHours < r.ShortBreakHours.Value - 1e-9;

                var tiers = new ShiftTiers();

                if (doubleTime)
                {
                    for (var m = s.Start; m < s.End; m += Grid.BlockMin) tiers.MultByMinute.Set(m, r.DoubleTimeMultiplier);
                    tiers.DoubleHours = span / 60;
                }
                else
                {
                    // Rules 1 + 2 - whichever overtime tail is longer. They are both
                    // measured from the END of the shift, so taking the larger tail
                    // applies the stronger rule without ever paying the same hour twice.
                    var daily = r.DailyOtAfterPaidHours is null ? 0 : Math.Max(0, paidHours - r.DailyOtAfterPaidHours.Value);
                    var period = r.PeriodOtAfterPaidHours is null
                        ? 0
                        : Math.Max(0, periodPaidHours + paidHours - Math.Max(periodPaidHours, r.PeriodOtAfterPaidHours.Value));
                    var otHours = Math.Min(paidHours, Math.Max(daily, period));

                    if (otHours > 0)
                    {
                        // The overtime is the tail of the shift's CLOCK time.
                        var otBlocks = Js.Round(otHours * 60 / Grid.BlockMin);
                        var otStart = s.End - otBlocks * Grid.BlockMin;
                        var isSunday = Period.DowMon0(s.Date) == 6;
                        for (var m = Math.Max(s.Start, otStart); m < s.End; m += Grid.BlockMin)
                        {
                            // Rule 3 - the Sunday early-morning band lifts 1.5x to 1.9x.
                            var sundayBand = isSunday && m >= r.SundayPremiumFromMin && m < r.SundayPremiumToMin;
                            tiers.MultByMinute.Set(m, sundayBand ? r.Ot19Multiplier : r.Ot15Multiplier);
                            if (sundayBand) tiers.Ot19Hours += Grid.BlockMin / 60;
                            else tiers.Ot15Hours += Grid.BlockMin / 60;
                        }
                    }
                }

                out_.Set(ShiftKey(s.EmployeeId, s.Date, s.Start), tiers);
                periodPaidHours += paidHours;
                prevEndAbs = endAbs;
            }
        }
        return out_;
    }

    public static ShiftTiers TiersFor(JsMap<string, ShiftTiers> map, string employeeId, DateOnly date, double start) =>
        map.GetOr(ShiftKey(employeeId, date, start), Empty);

    // ─── Make-up pay ─────────────────────────────────────────────────────────
    // A team member who finishes the period under their contracted hours is paid
    // the difference. It is real money the store spends for no labour, so it is
    // the sharpest possible signal that a roster under-used someone. The salaried
    // SM/ASM are excluded - their pay does not move with rostered hours.
    public static double MakeUpHours(string role, double contractPeriodHours, double paidHours, double creditedHours,
        IReadOnlyList<string>? exemptRoles = null)
    {
        if ((exemptRoles ?? [Roles.SM, Roles.ASM]).Contains(role)) return 0;
        return Math.Max(0, contractPeriodHours - paidHours - creditedHours);
    }
}
