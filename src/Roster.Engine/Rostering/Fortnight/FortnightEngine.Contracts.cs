// Port of src/lib/stage2/fortnight.ts (contract credit, the contract flags and
// the contract outcomes).

using Roster.Engine.Core;

namespace Roster.Engine.Rostering.Fortnight;

public static partial class FortnightEngine
{
    // Hourly adults are paid to contract regardless, so an under-rostered one is
    // burnt money - THE resourcing signal in an over-resourced store (interview).
    private static bool UnderContractRole(string role) =>
        role is Roles.SM or Roles.ASM or Roles.DSM or Roles.RSM or Roles.SA;

    // The value of one "day" toward contract for this employee - the full-day
    // leave credit and the public-holiday credit for non-workers. Defaults to a
    // tenth of the fortnight contract (the standard day-equivalent) unless a notional
    // override is set on the Team Members page.
    public static double NotionalHoursOf(FortnightEmp emp) => NotionalHoursOf(emp.ContractHours, emp.NotionalHours);

    /// <summary>notionalHoursOf for anything with a fortnight contract and an optional notional override.</summary>
    public static double NotionalHoursOf(double contractHours, double? notionalHours) => notionalHours ?? contractHours / 10;

    // Leave hours credited to an employee for one Mon-Sun week: each leave weekday
    // credits the entry's per-day hours, or the employee's notional hours for a
    // full contract day. A full week of leave therefore credits exactly the weekly
    // contract, and a full fortnight lands on contract instead of reading as 100%
    // under-rostered. Days the store is CLOSED are skipped - leave over a closed
    // day leaves the person short (client decision) - and public holidays are
    // skipped because the PH notional credit covers them instead.
    public static double LeaveHoursInWeek(FortnightEmp emp, IReadOnlyList<DayContext> days, int week)
    {
        double hours = 0;
        foreach (var day in days)
        {
            if (day.WeekIndex != week) continue;
            if (day.Closed || day.PublicHoliday) continue;
            var a = day.Avail.Get(emp.Id);
            if (a is null || !a.OnLeave) continue;
            if (Period.DowMon0(DateOnlyExtensions.ParseIso(day.Date)) >= 5) continue; // leave accrues across the working week
            hours += a.LeaveDayHours ?? NotionalHoursOf(emp);
        }
        return hours;
    }

    // The same credit across the whole fortnight.
    public static double LeaveHoursFor(FortnightEmp emp, IReadOnlyList<DayContext> days) =>
        LeaveHoursInWeek(emp, days, 0) + LeaveHoursInWeek(emp, days, 1);

    // Public-holiday notional credit KNOWABLE BEFORE the roster is built, used to
    // reduce the week's fill/repair target: PH days this employee cannot work
    // anyway (store closed, or they're unavailable). An AVAILABLE employee on an
    // open PH keeps their full target - the engine may roster them (penalty rates
    // are a costing concern, not a coverage one) - and if they end up not working
    // it the outcome-side credit below still makes their fortnight whole.
    public static double PhCreditInWeek(FortnightEmp emp, IReadOnlyList<DayContext> days, int week)
    {
        double hours = 0;
        foreach (var day in days)
        {
            if (day.WeekIndex != week || !day.PublicHoliday) continue;
            var a = day.Avail.Get(emp.Id);
            if (day.Closed || a is null || !a.Available) hours += NotionalHoursOf(emp);
        }
        return hours;
    }

    // Outcome-side public-holiday credit for final contract accounting: every PH
    // day the employee did NOT work credits their notional hours (workers earn
    // paid penalty-rate hours instead, never both).
    public static double PhCreditFor(FortnightEmp emp, IReadOnlyList<DayContext> days, Func<int, bool> workedDay)
    {
        double hours = 0;
        for (var di = 0; di < days.Count; di++)
        {
            if (days[di].PublicHoliday && !workedDay(di)) hours += NotionalHoursOf(emp);
        }
        return hours;
    }

    // The most PAID hours this role can physically be given in one Mon-Sun week:
    // five workable days (R.maxWorkedPerWeek) at the role's per-shift PAID cap. A
    // contract above this can never be met no matter how the roster is ordered,
    // and saying so is far more useful than reporting the same shortfall every
    // fortnight.
    public static double MaxPaidHoursPerWeek(BreakRule rule) => R.MaxWorkedPerWeek * rule.MaxShiftHours;

    private static List<string> UnderContractFlags(
        IReadOnlyList<FortnightEmp> employees,
        JsMap<string, Ledger> ledgers,
        IReadOnlyList<DayContext> days,
        IReadOnlyDictionary<string, BreakRule> breakRules)
    {
        var flags = new List<string>();
        foreach (var emp in employees)
        {
            if (!UnderContractRole(emp.Role)) continue;
            var led = ledgers[emp.Id];
            var paid = led.PaidHoursByWeek[0] + led.PaidHoursByWeek[1];
            var phCredit = PhCreditFor(emp, days, di => led.DaysWorked.Has(di));
            var shortfall = emp.ContractHours - paid - LeaveHoursFor(emp, days) - phCredit;
            if (shortfall <= 0.5) continue;
            var ceiling = MaxPaidHoursPerWeek(breakRules[emp.Role]);
            // Distinguish "the roster ran out of hours" from "this contract cannot be
            // rostered at all" - the second is a contract or award-rule problem and no
            // amount of re-ordering will shift it.
            var note =
                WeeklyContractOf(emp) > ceiling + 1e-9
                    ? $" - unreachable: {Js.ToFixed(emp.ContractHours, 2)}h/fortnight needs more than {N(R.MaxWorkedPerWeek)} shifts a week at the {emp.Role} maximum ({Js.ToFixed(ceiling * 2, 2)}h/fortnight ceiling)"
                    : "";
            flags.Add($"{emp.Name} is {Js.ToFixed(shortfall, 2)}h under contract this fortnight{note}");
        }
        return flags;
    }

    // Hours above contract are information, not a fault: a store whose plans need
    // more hours than its team's contracts hold rosters people above contract
    // (within every legal limit), and one with more contract than its plans need
    // leaves people short (paid the difference). Each person above contract is
    // named, and one line says which way the store's team is out, in plain words.
    private static List<string> OverContractFlags(
        IReadOnlyList<FortnightEmp> employees,
        JsMap<string, Ledger> ledgers,
        IReadOnlyList<DayContext> days)
    {
        var flags = new List<string>();
        double contract = 0;
        double given = 0;
        double over = 0;
        var overPeople = 0;
        var shortPeople = 0;
        foreach (var emp in employees)
        {
            if (!UnderContractRole(emp.Role)) continue;
            var led = ledgers[emp.Id];
            var paid = led.PaidHoursByWeek[0] + led.PaidHoursByWeek[1];
            var credited = LeaveHoursFor(emp, days) + PhCreditFor(emp, days, di => led.DaysWorked.Has(di));
            contract += emp.ContractHours;
            given += paid + credited;
            var diff = paid + credited - emp.ContractHours;
            if (diff > 0.5)
            {
                over += diff;
                overPeople++;
                flags.Add($"{emp.Name} is {Js.ToFixed(diff, 2)}h above contract this fortnight");
            }
            else if (diff < -0.5) shortPeople++;
        }
        if (overPeople > 0 && given > contract + 0.5)
            flags.Insert(
                0,
                $"The plans need more hours than the team's contracts hold: {N(overPeople)} {(overPeople == 1 ? "person is" : "people are")} rostered {Js.ToFixed(over, 2)}h above contract this fortnight");
        else if (shortPeople > 0 && contract > given + 0.5)
            flags.Insert(
                0,
                $"The team's contracts hold {Js.ToFixed(contract - given, 2)}h more than the plans need this fortnight: {N(shortPeople)} {(shortPeople == 1 ? "person is" : "people are")} short of contract and paid the difference");
        return flags;
    }

    // Where each person landed against contract for the fortnight.
    public static List<ContractOutcome> ContractOutcomes(
        IReadOnlyList<FortnightEmp> employees,
        JsMap<string, Ledger> ledgers,
        IReadOnlyList<DayContext> days) =>
        employees.Select(emp =>
        {
            var led = ledgers.Get(emp.Id);
            var paidHours = (led?.PaidHoursByWeek[0] ?? 0) + (led?.PaidHoursByWeek[1] ?? 0);
            var creditedHours =
                LeaveHoursFor(emp, days) +
                PhCreditFor(emp, days, di => led?.DaysWorked.Has(di) ?? false);
            var contractPeriodHours = emp.ContractHours;
            return new ContractOutcome
            {
                EmployeeId = emp.Id,
                Name = emp.Name,
                Role = emp.Role,
                ContractPeriodHours = contractPeriodHours,
                PaidHours = paidHours,
                CreditedHours = creditedHours,
                ShortfallHours = Math.Max(0, contractPeriodHours - paidHours - creditedHours),
            };
        }).ToList();

    // Long-run warning. The salaried SM/ASM have no hard consecutive-day cap by
    // default (maxConsecDaysSalaried = the whole fortnight), so an unbroken run is
    // legal but not necessarily what the store intended. This surfaces the longest
    // run per person once it passes the threshold; it is ADVISORY - nothing in the
    // engine refuses a roster because of it.
    public static List<string> LongRunFlags(
        IReadOnlyList<FortnightEmp> employees,
        JsMap<string, Ledger> ledgers,
        IReadOnlyList<DayContext> days)
    {
        var warnAt = R.LongRunWarnDays;
        if (!(warnAt > 0)) return [];
        var flags = new List<string>();
        foreach (var emp in employees)
        {
            var led = ledgers.Get(emp.Id);
            if (led is null) continue;
            // Longest unbroken run of worked date-indices.
            var runStart = -1;
            (int Len, int From, int To) best = (0, -1, -1);
            for (var di = 0; di <= days.Count; di++)
            {
                var worked = di < days.Count && led.DaysWorked.Has(di);
                if (worked && runStart < 0) runStart = di;
                if (!worked && runStart >= 0)
                {
                    if (di - runStart > best.Len) best = (di - runStart, runStart, di - 1);
                    runStart = -1;
                }
            }
            if (best.Len > warnAt)
            {
                flags.Add(
                    $"{emp.Name} works {N(best.Len)} days in a row ({Ddmm(days[best.From].Date)} - " +
                    $"{Ddmm(days[best.To].Date)}) - above the {N(warnAt)}-day run warning");
            }
        }
        return flags;
    }
}
