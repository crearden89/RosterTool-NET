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
    public void Pays_the_rsm_allowance_only_while_they_are_the_only_manager()
    {
        var a = AnalysisCompute.Compute(Inputs());
        // 17:00-21:00 alone: 16 blocks at $9/h. RSMs price at the "SA" key, which has no
        // rate of its own, so the allowance is their whole cost.
        var rsm = a.CostByRole.First(r => r.Role == "RSM");
        Close.To(16 * 9 * 0.25, rsm.Cost, 9);
        // Controllables: the allowance and the make-up pay (no overtime here).
        Close.To(36 + 18.4 * 30, a.ControllablesCost, 9);
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
