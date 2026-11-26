// New: lib/analysis/compute.ts had no unit test (server-only); these cover the pure half
// ported to AnalysisCompute, on an invented two-day range.

using Roster.Engine.Analysis;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Tests.Overview;

namespace Roster.Engine.Tests.Analysis;

public class AnalysisComputeTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");
    private static readonly DateOnly Mon = D("2026-11-23"), Tue = D("2026-11-24");

    private static Shift S(string emp, string role, double start, double end, bool unfilled = false) =>
        new() { EmployeeId = emp, Role = role, Start = start, End = end, Unfilled = unfilled };

    private static Stage1Computation Stage1(params (string Group, double Allocated, double Time, double Headcount)[] rows) => new()
    {
        HasData = true,
        Allocations = rows.Select(r => new GroupAllocation { Group = r.Group, AllocatedHours = r.Allocated, BenchmarkHours = r.Allocated }).ToList(),
        Blocks = rows.Select(r => new PlannedBlock(r.Group, r.Time, r.Headcount)).ToList(),
        TotalBenchmark = rows.Sum(r => r.Allocated),
    };

    private static AnalysisInputs Inputs() => new()
    {
        Days = [Mon, Tue],
        PerDay =
        [
            new(Mon, DayTypes.Weekday,
                new RosterComputation
                {
                    HasData = true,
                    Shifts =
                    [
                        S("sm", "SM", 9 * 60, 17 * 60),
                        S("rsm", "RSM", 13 * 60, 21 * 60),
                        S("sa", "SA", 9 * 60, 13 * 60),
                        S("", "SA", 17 * 60, 21 * 60, unfilled: true),
                    ],
                },
                Stage1(("load_running", 30, 540, 2), ("checkout", 20, 555, 3)),
                new JsMap<string, ShiftTiers>()),
            new(Tue, DayTypes.Weekday, new RosterComputation(), Stage1(("checkout", 40, 540, 1)), new JsMap<string, ShiftTiers>()),
        ],
        Employees =
        [
            new("sm", "SM", null, 1, null, 90),
            new("rsm", "RSM", null, null, null, 76),
            new("sa", "SA", null, null, null, 76),
        ],
        PayRates = Enumerable.Range(0, 24).SelectMany(h => new[]
        {
            new PayRateRow(DayTypes.Weekday, h, "SA-FF", 30),
            new PayRateRow(DayTypes.Weekday, h, "SM-45-1", 40),
        }).ToList(),
        StoreState = "NSW",
        PayrollTaxRates = new Dictionary<string, double> { ["NSW"] = 0.0545 },
        Leave =
        [
            new("sa", Tue, D("2026-11-28"), "AL", null), // Tue costs; Sat is a weekend
            new("sm", Mon, Tue, "UNP", 8), // unpaid: nothing
        ],
        PeriodOutcomes =
        [
            [
                new ContractOutcome { EmployeeId = "sa", Role = "SA", ContractPeriodHours = 76, PaidHours = 50, CreditedHours = 7.6 },
                new ContractOutcome { EmployeeId = "sm", Role = "SM", ContractPeriodHours = 90, PaidHours = 10, CreditedHours = 0 },
            ],
        ],
        Actuals = [new(Mon, 40, 25_000, 100_000)],
        Bench = [new(Mon, 95_000, 24_000), new(Tue, 120_000, 30_000)],
    };

    [Fact]
    public void Costs_each_quarter_hour_at_its_rate_less_the_unpaid_break()
    {
        var a = AnalysisCompute.Compute(Inputs());
        var sm = a.CostByRole.First(r => r.Role == "SM");
        var smBreak = Breaks.BreakForRole("SM", 8).UnpaidMinutes;
        Close.To(32 * 40 * 0.25 - 40 * smBreak / 60, sm.Cost, 9);
        Close.To(8 - smBreak / 60, sm.Hours, 9);
        Assert.Equal(1, sm.Shifts);
        // The open shift has no employee and no wage.
        Assert.Equal(1, a.CostByRole.First(r => r.Role == "SA").Shifts);
        Assert.Equal(["SM", "ASM", "DSM", "RSM", "SA", "JSA"], a.CostByRole.Select(r => r.Role));
    }

    [Fact]
    public void Pays_the_rsm_the_store_assistant_rate_plus_the_allowance_only_while_they_are_the_only_manager()
    {
        var a = AnalysisCompute.Compute(Inputs());
        // The owner's rule: an RSM's hours cost the Store Assistant rate for the hour and
        // day type (SA-FF, $30 here), less the unpaid break, plus the allowance for
        // 17:00-21:00 alone: 16 blocks at $9/h.
        var rsm = a.CostByRole.First(r => r.Role == "RSM");
        var rsmBreak = Breaks.BreakForRole("RSM", 8).UnpaidMinutes;
        Close.To(32 * 30 * 0.25 - 30 * rsmBreak / 60 + 16 * 9 * 0.25, rsm.Cost, 9);
        // Controllables: the allowance and the make-up pay (no overtime here).
        Close.To(36 + 18.4 * 30, a.ControllablesCost, 9);
    }

    // ─── Port of src/lib/analysis/compute.test.ts ───────────────────────────────
    // Analysis prices an RSM shift as the Roster page does (Pay/ShiftCost): the Store
    // Assistant rate for each hour, plus the Pay Rates "RSM allowance" per hour in the
    // quarter-hours the RSM is the only manager on the floor. The TypeScript test runs the
    // real computeAnalysis and computeShiftCosts with the day's roster, Stage 1 (no data),
    // the pay period's tiers (all ordinary time) and the day type stood in for; here the
    // same inputs go to their pure halves.

    private static double H(double hh) => hh * 60;

    // The SM opens; a DSM covers the afternoon handover; the RSM closes, alone from 17:00 -
    // the open DSM slot from 18:00 has nobody on it.
    private static readonly List<Shift> RsmDay =
    [
        new() { EmployeeId = "e-sm", Name = "e-sm", Role = "SM", Start = H(6), End = H(14) },
        new() { EmployeeId = "e-sa", Name = "e-sa", Role = "SA", Start = H(8), End = H(16) },
        new() { EmployeeId = "e-dsm", Name = "e-dsm", Role = "DSM", Start = H(13), End = H(17) },
        new() { EmployeeId = "e-rsm", Name = "e-rsm", Role = "RSM", Start = H(14), End = H(22) },
        new() { EmployeeId = "", Name = "", Role = "DSM", Start = H(18), End = H(22), Unfilled = true },
    ];
    private const double SoleManagerHours = 5; // 17:00-22:00

    private static IEnumerable<PayRateRow> Rates(string position, double day, double evening) =>
        Enumerable.Range(0, 24).Select(h => new PayRateRow(DayTypes.Weekday, h, position, h >= 18 ? evening : day));

    // Invented rates. RSM has no row of its own: it is paid the SA-FF rate.
    private static readonly List<PayRateRow> RsmDayRates = [.. Rates("SA-FF", 31.4, 37.68), .. Rates("SM-45-1", 52.1, 52.1), .. Rates("DSM", 40.25, 48.3)];

    private static readonly List<AnalysisEmployee> RsmDayTeam =
    [
        new("e-sm", "SM", "SM-45-1", 1, null, 90),
        new("e-sa", "SA", "SA-FF", null, null, 60),
        new("e-dsm", "DSM", "DSM", null, null, 76),
        new("e-rsm", "RSM", "RSM", null, null, 76),
    ];

    // The setting is the owner's to edit on Pay Rates; any value is priced.
    private static PaySettings Settings(double rsmAllowance) => PaySettings.Default with { RsmAllowance = rsmAllowance };

    private static AnalysisResult AnalyseRsmDay(double rsmAllowance) => AnalysisCompute.Compute(new AnalysisInputs
    {
        Days = [Tue],
        PerDay = [new(Tue, DayTypes.Weekday, new RosterComputation { HasData = true, Shifts = RsmDay }, new Stage1Computation(), new JsMap<string, ShiftTiers>())],
        Employees = RsmDayTeam,
        PayRates = RsmDayRates,
        PaySettings = Settings(rsmAllowance),
        StoreState = "NSW",
    });

    private static ShiftCosts RsmDayShiftCosts(double rsmAllowance) => ShiftCost.ComputeShiftCosts(
        Tue,
        RsmDay.Where(s => !s.Unfilled).Select(s => new CostableShift(s.EmployeeId, s.Role, s.Start, s.End, s.BreakMinutes)).ToList(),
        RsmDayRates,
        RsmDayTeam.Select(e => new CostEmployee(e.Id, e.PositionCode, e.SmYearBand)),
        new JsMap<string, ShiftTiers>(),
        Settings(rsmAllowance),
        DayTypes.Weekday);

    [Fact]
    public void Prices_the_rsms_hours_at_the_store_assistant_rate_plus_the_allowance_where_they_are_the_sole_manager()
    {
        var a = AnalyseRsmDay(7.25);
        var shiftCosts = RsmDayShiftCosts(7.25);
        var rsm = a.CostByRole.First(r => r.Role == "RSM");
        Assert.Equal(1, rsm.Shifts);
        Assert.True(rsm.Cost > 7.25 * SoleManagerHours, $"{rsm.Cost}"); // more than the allowance alone
        Close.To(shiftCosts.CostByEmployee.Get("e-rsm"), rsm.Cost, 9);
        // Every other position too: one costing of a shift, on both pages.
        foreach (var (role, id) in new[] { ("SM", "e-sm"), ("SA", "e-sa"), ("DSM", "e-dsm") })
            Close.To(shiftCosts.CostByEmployee.Get(id), a.CostByRole.First(r => r.Role == role).Cost, 9);
        Close.To(shiftCosts.DayTotal, a.TotalCost, 9);
    }

    [Fact]
    public void Adds_the_pay_rates_allowance_as_set_for_each_hour_the_rsm_is_the_only_manager_on_the_floor()
    {
        var withAllowance = AnalyseRsmDay(7.25).CostByRole.First(r => r.Role == "RSM").Cost;
        var without = AnalyseRsmDay(0).CostByRole.First(r => r.Role == "RSM").Cost;
        Assert.True(without > 0, $"{without}");
        Close.To(7.25 * SoleManagerHours, withAllowance - without, 9);
        Close.To(RsmDayShiftCosts(0).CostByEmployee.Get("e-rsm"), without, 9);
    }

    [Fact]
    public void Costs_leave_on_weekdays_only_and_never_unpaid_leave()
    {
        // Tuesday at contract / 10 = 7.6 h at the weekday 9am rate.
        Close.To(7.6 * 30, AnalysisCompute.Compute(Inputs()).CostOfLeave, 9);
    }

    [Fact]
    public void Owes_make_up_pay_to_contracted_staff_left_short_but_not_salaried_managers()
    {
        var a = AnalysisCompute.Compute(Inputs());
        Close.To(18.4, a.MakeUpHoursTotal, 9);
        Close.To(18.4 * 30, a.MakeUpCost, 9);
    }

    [Fact]
    public void Blends_actuals_to_date_with_the_forecast_ahead()
    {
        var a = AnalysisCompute.Compute(Inputs());
        Assert.Equal(Basis.Mixed, a.Basis);
        Assert.Equal((1, 1), (a.ActualDays, a.ForecastDays));
        Assert.Equal(220_000, a.Sales);
        // Actual hours: Monday's 40 and Tuesday's rostered hours (none); against Stage 1's 90.
        Close.To(40.0 / 90 * 100, a.OePct, 9);
        Close.To(55_000 / a.TotalLabourHours, a.Iplh, 9);
    }

    [Fact]
    public void Sums_the_density_over_the_range_and_counts_its_days()
    {
        var a = AnalysisCompute.Compute(Inputs());
        Assert.Equal(2, a.DensityDays);
        Assert.Equal([540.0, 555.0], a.Density.Select(d => d.Time));
        Assert.Equal(2 + 1, a.Density[0].Groups.GetOr("load_running", 0) + a.Density[0].Groups.GetOr("checkout", 0));
        Assert.True(a.HasData);
    }

    [Fact]
    public void Adds_the_stores_own_on_costs_and_lists_every_group()
    {
        var a = AnalysisCompute.Compute(Inputs());
        Close.To(1 + 0.12 + 0.0545, a.OncostMultiplier, 12);
        Assert.Equal(new OnCost(0.12, 0.0545, "NSW"), a.Oncost);
        Assert.Equal(["checkout", "load_running", "reset", "admin", "specials", "cleaning"], a.HoursByGroup.Select(h => h.Group));
        // No held plan: both days count no planned hours and Stage 1's benchmark.
        Assert.Equal(2, a.DaysWithoutPlan);
        Assert.Equal(0, a.PlannedHours);
        Assert.Equal(90, a.BenchmarkHours);
    }

    [Fact]
    public void Caps_the_range_at_31_days()
    {
        Assert.Equal(31, AnalysisCompute.RangeDays(D("2026-01-01"), D("2026-03-01")).Count);
        Assert.Empty(AnalysisCompute.RangeDays(Tue, Mon));
    }
}
