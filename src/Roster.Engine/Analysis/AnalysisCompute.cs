// Port of src/lib/analysis/compute.ts: AnalysisResult and the pure half of computeAnalysis
// (everything after the reads). The reads - the fortnight rosters, Stage 1, day types, pay
// tiers, pay rates and settings, leave, actuals, benchmarks and the held plans - are the
// Analysis page's service.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Fortnight;
using HeldPlanDay = Roster.Engine.Planning.Plan.HeldPlanDay;

namespace Roster.Engine.Analysis;

/// <summary>One 15-minute block of labour density: headcount by group, summed over the range.</summary>
public sealed record DensityBlock(double Time, JsMap<string, double> Groups);

/// <summary>computeAnalysis's result: the figures and, for the chart, the density.</summary>
public sealed record AnalysisResult : AnalysisFigureInput
{
    public IReadOnlyList<DateOnly> Days { get; init; } = [];
    /// <summary>
    /// Per-15-min-block group headcount, SUMMED across every day in the range. Divided by
    /// DensityDays it becomes AVERAGE staff on the floor at that time of day.
    /// </summary>
    public IReadOnlyList<DensityBlock> Density { get; init; } = [];
    /// <summary>Days that actually contributed blocks (closed / no-data days excluded).</summary>
    public int DensityDays { get; init; }
    public bool HasData { get; init; }
}

/// <summary>One day of the range as read: its pay day type, roster, Stage 1 and its fortnight's pay tiers.</summary>
public sealed record AnalysisDay(DateOnly Date, string DayType, RosterComputation Roster, Stage1Computation Stage1, JsMap<string, ShiftTiers> Tiers);

/// <summary>A team member as the costs read them (refdata's employee row).</summary>
public sealed record AnalysisEmployee(string Id, string Role, string? PositionCode, double? SmYearBand, double? NotionalHours, double ContractHours);

/// <summary>An employee_leave row overlapping the range.</summary>
public sealed record AnalysisLeave(string EmployeeId, DateOnly StartDate, DateOnly EndDate, string LeaveType, double? DayHours);

/// <summary>An actuals_days row of the range.</summary>
public sealed record AnalysisActuals(DateOnly Date, double ActualHours100, double ActualItems, double ActualSales);

/// <summary>A benchmark_days row of the range.</summary>
public sealed record AnalysisBench(DateOnly Date, double SalesForecast, double Items);

/// <summary>Everything computeAnalysis reads, for <see cref="AnalysisCompute.Compute"/>.</summary>
public sealed record AnalysisInputs
{
    public IReadOnlyList<DateOnly> Days { get; init; } = [];
    /// <summary>One per day of <see cref="Days"/>, in the same order.</summary>
    public IReadOnlyList<AnalysisDay> PerDay { get; init; } = [];
    public IReadOnlyList<AnalysisEmployee> Employees { get; init; } = [];
    public IReadOnlyList<PayRateRow> PayRates { get; init; } = [];
    public PaySettings PaySettings { get; init; } = PaySettings.Default;
    public string? StoreState { get; init; }
    public IReadOnlyDictionary<string, double> PayrollTaxRates { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<AnalysisLeave> Leave { get; init; } = [];
    /// <summary>Each fortnight the range falls in (in order) and where its people landed against contract.</summary>
    public IReadOnlyList<IReadOnlyList<ContractOutcome>> PeriodOutcomes { get; init; } = [];
    public IReadOnlyList<AnalysisActuals> Actuals { get; init; } = [];
    public IReadOnlyList<AnalysisBench> Bench { get; init; } = [];
    /// <summary>The held plans' days of the range, by date.</summary>
    public IReadOnlyDictionary<DateOnly, HeldPlanDay> Held { get; init; } = new Dictionary<DateOnly, HeldPlanDay>();
}

public static class AnalysisCompute
{
    private static readonly string[] GroupsOrder = ["checkout", "load_running", "reset", "admin", "specials", "cleaning"];
    private static readonly string[] RoleOrder = ["SM", "ASM", "DSM", "RSM", "SA", "JSA"];
    private static readonly string[] Managers = ["SM", "ASM", "DSM", "RSM"];

    /// <summary>
    /// The (inclusive) date range, capped at 31 days so a fortnight or a full month can be
    /// analysed in one go.
    /// </summary>
    public static List<DateOnly> RangeDays(DateOnly start, DateOnly end)
    {
        var days = new List<DateOnly>();
        for (var d = start; d <= end && days.Count < 31; d = d.AddDays(1)) days.Add(d);
        return days;
    }

    public static AnalysisResult Compute(AnalysisInputs input)
    {
        var days = input.Days;
        var rateMap = new JsMap<string, double>();
        foreach (var r in input.PayRates) rateMap.Set(RateGrid.RateKey(r.DayType, r.Hour, r.Role), r.Rate);
        double RateFor(string dt, double hour, string role) => rateMap.GetOr(RateGrid.RateKey(dt, hour, role), 0);

        var empById = new JsMap<string, AnalysisEmployee>();
        foreach (var e in input.Employees) empById.Set(e.Id, e);
        // Rates key off the employee's POSITION (SM-45-3, SA-LR, JSA-17 ...), which is the
        // pay_rates column. Employees predating positions fall back to their role + tenure band.
        string PayRoleFor(string role, string employeeId)
        {
            var emp = empById.Get(employeeId);
            return Positions.PayKeyFor(emp?.PositionCode, role, emp?.SmYearBand);
        }

        var costByRole = new JsMap<string, double[]>(); // [cost, hours, shifts]
        var allocatedByGroup = new JsMap<string, double>();
        var density = new JsMap<double, JsMap<string, double>>();
        var rosteredHoursByDate = new JsMap<DateOnly, double>();

        double totalCost = 0, totalLabourHours = 0, rsmAllowanceTotal = 0, otPremiumTotal = 0;
        var densityDays = 0;
        var anyData = false;
        const double Block = Grid.BlockMin;

        foreach (var day in input.PerDay)
        {
            var (date, dt, roster, stage1, tiers) = (day.Date, day.DayType, day.Roster, day.Stage1, day.Tiers);
            if (roster.HasData || stage1.HasData) anyData = true;

            // Managers on the floor per 15-min block, so an RSM's sole-manager allowance can
            // be applied only for the blocks where they're the only one.
            var mgrAtBlock = new JsMap<double, double>();
            foreach (var shift in roster.Shifts)
            {
                if (!Managers.Contains(shift.Role)) continue;
                for (var m = shift.Start; m < shift.End; m += Block) mgrAtBlock.Set(m, mgrAtBlock.GetOr(m, 0) + 1);
            }

            // Per-day rostered hours, so a FUTURE day (no actuals yet) can stand in its own
            // rostered hours where a past day uses what was really worked.
            var hoursBeforeDay = totalLabourHours;

            foreach (var shift in roster.Shifts)
            {
                if (shift.Unfilled) continue; // open shifts have no employee - no wage cost
                // RSMs have no rate of their own - paid the Store Assistant rate, plus a $/hr
                // allowance only for blocks where they're the sole manager.
                var rateRole = shift.Role == "RSM" ? "SA" : PayRoleFor(shift.Role, shift.EmployeeId);
                if (!costByRole.TryGet(shift.Role, out var cur)) cur = [0, 0, 0];
                cur[2] += 1;
                // Overtime / double time for this shift, decided across the whole pay
                // period. Blocks not listed are ordinary time.
                var tier = PayTiers.TiersFor(tiers, shift.EmployeeId, date, shift.Start);
                for (var m = shift.Start; m < shift.End; m += Block)
                {
                    var hour = Math.Floor(m / 60);
                    var mult = tier.MultByMinute.GetOr(m, 1);
                    var ordinary = RateFor(dt, hour, rateRole) * (Block / 60);
                    var premium = ordinary * (mult - 1);
                    var blockCost = ordinary + premium;
                    otPremiumTotal += premium;
                    if (shift.Role == "RSM" && mgrAtBlock.GetOr(m, 0) == 1)
                    {
                        var allowance = input.PaySettings.RsmAllowance * (Block / 60);
                        blockCost += allowance;
                        rsmAllowanceTotal += allowance;
                    }
                    cur[0] += blockCost;
                    cur[1] += Block / 60;
                    totalCost += blockCost;
                    totalLabourHours += Block / 60;
                }
                // Deduct the UNPAID break at the rate of the hour it sits in - the loop above
                // pays every block on the clock. (Paid rest breaks stay on the clock.) This
                // mirrors lib/payroll/shiftCost, which the roster grid uses.
                var brk = Breaks.BreakForRole(shift.Role, (shift.End - shift.Start) / 60);
                var bw = Display.BreakWindows(shift.Start, shift.End, brk);
                var breakHour = Math.Floor((bw.Count > 0 ? bw[0].Start : shift.Start) / 60);
                var unpaidCost = RateFor(dt, breakHour, rateRole) * (brk.UnpaidMinutes / 60);
                cur[0] -= unpaidCost;
                cur[1] -= brk.UnpaidMinutes / 60;
                totalCost -= unpaidCost;
                totalLabourHours -= brk.UnpaidMinutes / 60;
                costByRole.Set(shift.Role, cur);
            }
            rosteredHoursByDate.Set(date, totalLabourHours - hoursBeforeDay);

            // OE's target and the density come from Stage 1. Density accumulates across
            // EVERY day of the range - each block's value is the total headcount seen at
            // that time-of-day over the whole range.
            foreach (var a in stage1.Allocations) allocatedByGroup.Set(a.Group, allocatedByGroup.GetOr(a.Group, 0) + a.AllocatedHours);
            if (stage1.Blocks.Count > 0) densityDays++;
            foreach (var b in stage1.Blocks)
            {
                if (!density.TryGet(b.Time, out var rec)) density.Set(b.Time, rec = new JsMap<string, double>());
                rec.Set(b.Group, rec.GetOr(b.Group, 0) + b.Headcount);
            }
        }

        // Cost of leave: each leave WEEKDAY in range costs the entry's per-day hours - or
        // the employee's notional (default contract/10) for a full contract day - at their
        // ordinary weekday base rate (leave has no shift time of its own to key a penalty
        // rate off). Weekends never carry leave, closed days credit nothing, and unpaid
        // (UNP) leave costs nothing.
        var closedDates = new HashSet<DateOnly>(input.PerDay.Where(d => d.Stage1.Closed || !d.Stage1.HasData).Select(d => d.Date));
        double costOfLeave = 0;
        var leaveDaySeen = new HashSet<string>();
        foreach (var l in input.Leave)
        {
            if (l.LeaveType == "UNP") continue;
            var emp = empById.Get(l.EmployeeId);
            if (emp is null) continue;
            var payRole = PayRoleFor(emp.Role, l.EmployeeId);
            var baseRate = RateFor(DayTypes.Weekday, 9, payRole);
            var dailyHours = l.DayHours ?? emp.NotionalHours ?? emp.ContractHours / 10;
            foreach (var date in days)
            {
                if (date < l.StartDate || date > l.EndDate) continue;
                if (Period.DowMon0(date) >= 5 || closedDates.Contains(date)) continue;
                if (!leaveDaySeen.Add($"{l.EmployeeId}:{date.ToIso()}")) continue;
                costOfLeave += dailyHours * baseRate;
            }
        }

        // Make-up pay: an employee who finishes the PAY PERIOD under contract is paid the
        // difference - money spent for no labour. The salaried SM/ASM are excluded.
        // Deliberately period-scoped: reported next to the range figures, not mixed in.
        double makeUpCost = 0, makeUpHoursTotal = 0;
        foreach (var outcomes in input.PeriodOutcomes)
        {
            foreach (var o in outcomes)
            {
                var hours = PayTiers.MakeUpHours(o.Role, o.ContractPeriodHours, o.PaidHours, o.CreditedHours);
                if (hours <= 0) continue;
                makeUpHoursTotal += hours;
                makeUpCost += hours * RateFor(DayTypes.Weekday, 9, PayRoleFor(o.Role, o.EmployeeId));
            }
        }

        // Controllables Cost - premium spend above ordinary base-rate hours: the RSM
        // sole-manager allowance plus every overtime / double-time premium, plus make-up
        // pay - all of it is spend a better roster could have avoided.
        var controllablesCost = rsmAllowanceTotal + otPremiumTotal + makeUpCost;

        // ── Actual vs forecast, decided PER DAY ──────────────────────────────────
        // A day that has already happened (its actuals row has landed) reports what really
        // happened; a day still to come reports its forecast: benchmark sales/items and the
        // hours the roster commits. A row of pure zeroes is treated as not-yet-landed.
        var actualByDate = new JsMap<DateOnly, AnalysisActuals>();
        foreach (var a in input.Actuals.Where(a => a.ActualSales > 0 || a.ActualItems > 0 || a.ActualHours100 > 0))
            actualByDate.Set(a.Date, a);
        var benchByDate = new JsMap<DateOnly, AnalysisBench>();
        foreach (var b in input.Bench) benchByDate.Set(b.Date, b);

        double actualHours100 = 0, actualItems = 0, sales = 0;
        int actualDays = 0, forecastDays = 0;
        foreach (var date in days)
        {
            if (actualByDate.TryGet(date, out var a))
            {
                actualDays++;
                actualHours100 += a.ActualHours100;
                actualItems += a.ActualItems;
                sales += a.ActualSales;
                continue;
            }
            if (!benchByDate.TryGet(date, out var b)) continue; // no data either way
            forecastDays++;
            actualHours100 += rosteredHoursByDate.GetOr(date, 0); // the roster IS the forecast
            actualItems += b.Items;
            sales += b.SalesForecast;
        }
        var basis = actualDays > 0 && forecastDays > 0 ? Basis.Mixed : actualDays > 0 ? Basis.Actual : Basis.Forecast;

        // Non-Op is additive to the target and already in Stage 1's allocations (the non_op
        // group), so totalAllocated is the full target here. NOT the Dashboard's OE: this
        // divides actual hours by the hours Stage 1 gives the labour groups - how the roster
        // performed against its target. Different denominators by design.
        var totalAllocated = allocatedByGroup.Values().Aggregate(0.0, (x, y) => x + y);
        double? oePct = totalAllocated > 0 ? actualHours100 / totalAllocated * 100 : null;
        double? iplh = totalLabourHours > 0 && actualItems > 0 ? actualItems / totalLabourHours : null;

        // ── Hours against the benchmark, from the HELD plans ─────────────────────
        // A day no plan covers has no planned hours, and the benchmark its Stage 1 gives it.
        var daysWithoutPlan = input.PerDay.Count(d => !input.Held.ContainsKey(d.Date));
        var range = HeldDayHours.RangeHours(input.PerDay.Select(d =>
            input.Held.TryGetValue(d.Date, out var held) ? held : new HeldPlanDay(BenchmarkParts.Of(d.Stage1), null)));

        // On-costs at the store's own rate: super plus its state's payroll tax, from Pay
        // Rates (owner, 15/11/2026: "correct to the stores own rate").
        var payrollTaxRate = input.StoreState is { Length: > 0 } st ? input.PayrollTaxRates.GetValueOrDefault(st, 0) : 0;
        var oncostMultiplier = 1 + input.PaySettings.SuperRate + payrollTaxRate;

        // The six groups always; non-op and anything else only when it has hours.
        var extraGroups = Js.Sorted(
            range.Groups.Keys()
                .Where(g => !GroupsOrder.Contains(g))
                .Where(g => range.Groups[g].Benchmark > 0 || range.Groups[g].Planned > 0),
            (a, b) => a == "non_op" ? -1 : b == "non_op" ? 1 : Js.LocaleCompare(a, b));

        return new AnalysisResult
        {
            Days = days,
            TotalCost = totalCost,
            TotalLabourHours = totalLabourHours,
            // Every role always appears, JSA included - a role with no shifts in the range
            // renders as "-" rather than silently vanishing from the table.
            CostByRole = RoleOrder
                .Select(role => costByRole.TryGet(role, out var c) ? new RoleCost(role, c[0], c[1], (int)c[2]) : new RoleCost(role, 0, 0, 0))
                .ToList(),
            HoursByGroup = GroupsOrder.Concat(extraGroups)
                .Select(g => range.Groups.TryGet(g, out var h) ? new GroupHours(g, h.Benchmark, h.Planned) : new GroupHours(g, 0, 0))
                .ToList(),
            NoWorkHours = range.NoWork,
            PlannedHours = range.Planned,
            BenchmarkHours = range.Benchmark,
            DaysWithoutPlan = daysWithoutPlan,
            OePct = oePct,
            Iplh = iplh,
            Basis = basis,
            ActualDays = actualDays,
            ForecastDays = forecastDays,
            Density = density.Entries().OrderBy(e => e.Key).Select(e => new DensityBlock(e.Key, e.Value)).ToList(),
            DensityDays = densityDays,
            HasData = anyData,
            Sales = sales,
            CostOfLeave = costOfLeave,
            MakeUpCost = makeUpCost,
            MakeUpHoursTotal = makeUpHoursTotal,
            ControllablesCost = controllablesCost,
            OncostMultiplier = oncostMultiplier,
            Oncost = new OnCost(input.PaySettings.SuperRate, payrollTaxRate, input.StoreState),
        };
    }
}
