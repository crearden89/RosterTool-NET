// Port of src/lib/analysis/figures.test.ts.

using Roster.Engine.Analysis;
using Roster.Engine.Core;
using Roster.Engine.Tests.Overview;
using static Roster.Engine.Analysis.Figures;

namespace Roster.Engine.Tests.Analysis;

public class FiguresTests
{
    // A forecast fortnight like the test store's (VIC: super 12% + payroll tax
    // 4.85% = x1.1685): the shape computeAnalysis returns, figures made up.
    private static readonly AnalysisFigureInput Forecast = new()
    {
        Basis = Basis.Forecast,
        ActualDays = 0,
        ForecastDays = 14,
        Sales = 1_659_000,
        Iplh = null,
        OePct = 119.04,
        TotalCost = 20_307.4,
        TotalLabourHours = 554.5,
        CostOfLeave = 930.2,
        MakeUpCost = 5_972.35,
        MakeUpHoursTotal = 188.61,
        ControllablesCost = 5_972.35,
        OncostMultiplier = 1.1685,
        Oncost = new OnCost(0.12, 0.0485, "VIC"),
        CostByRole =
        [
            new("SM", 3_516.2, 73.25, 10),
            new("ASM", 2_814.1, 65, 10),
            new("DSM", 4_589.4, 100.75, 20),
            new("RSM", 0, 36.25, 10),
            new("SA", 9_389.7, 279.5, 79),
            new("JSA", 0, 0, 0),
        ],
        // The test store's held plan (lib/plan/plannedHours): 622.50 h of work by
        // group and non-op, and 2.75 h with no work in it, make 625.25 h planned.
        // The groups' benchmark adds up to the benchmark (lib/plan/benchmarkSplit):
        // Checkout's share is what the other groups leave of 622.06 h.
        HoursByGroup =
        [
            new("checkout", 212.87, 212.5),
            new("load_running", 174.02, 173.5),
            new("reset", 83.4, 83.75),
            new("admin", 86.21, 86.75),
            new("specials", 41.78, 41.75),
            new("cleaning", 20.53, 21),
            new("non_op", 3.25, 3.25),
        ],
        NoWorkHours = 2.75,
        PlannedHours = 625.25,
        BenchmarkHours = 622.06,
        DaysWithoutPlan = 0,
    };

    private static readonly AnalysisFigureInput NoSales = Forecast with { Basis = Basis.Actual, ActualDays = 14, ForecastDays = 0, Sales = 0, OePct = null };
    private static readonly AnalysisFigureInput Nsw = Forecast with { OncostMultiplier = 1.1745, Oncost = new OnCost(0.12, 0.0545, "NSW") };

    private static Dictionary<string, double> Dollars(AnalysisFigureInput a, bool loaded)
    {
        var d = CostFigures(a, loaded).ToDictionary(c => c.Key.ToString(), c => c.Dollars);
        foreach (var p in MoneyParts(a, loaded).Parts) d[$"part:{p.Key}"] = p.Dollars;
        d["hourly"] = (a.TotalCost + a.CostOfLeave) * CostFactor(a, loaded) / a.TotalLabourHours;
        return d;
    }

    // ── on-costs at the store's own rate (owner, 15/11/2026) ──

    [Fact]
    public void Leaves_every_cost_raw_on_raw_wages()
    {
        var raw = CostFigures(Forecast, false).ToDictionary(c => c.Key, c => c.Dollars);
        Assert.Equal(20_307.4 + 930.2 + 5_972.35, raw[CostKey.TotalCost]);
        Assert.Equal(20_307.4, raw[CostKey.WorkedPayroll]);
        Assert.Equal(930.2, raw[CostKey.Leave]);
        Assert.Equal(5_972.35, raw[CostKey.MakeUp]);
        Assert.Equal(20_307.4 + 930.2, raw[CostKey.TotalPayroll]);
        Assert.Equal(5_972.35, raw[CostKey.Controllables]);
        Assert.Equal("$" + Js.ToFixed((20_307.4 + 930.2) / 554.5, 2), HeadlineFigures(Forecast, false)[1].Value);
        Assert.Equal("$3,516", RoleRows(Forecast, false)[0].Cost);
        Assert.Equal(["$20,307", "$930", "$5,972"], MoneyParts(Forecast, false).Parts.Select(p => p.Value));
    }

    [Fact]
    public void Multiplies_every_cost_by_the_stores_own_factor_once_on_fully_loaded()
    {
        foreach (var a in new[] { Forecast, Nsw })
        {
            var raw = Dollars(a, false);
            var loaded = Dollars(a, true);
            foreach (var k in raw.Keys) Close.To(a.OncostMultiplier, loaded[k] / raw[k], 12);
            Assert.Equal("$" + Js.ToLocaleString(Js.Round(3_516.2 * a.OncostMultiplier)), RoleRows(a, true)[0].Cost);
        }
        // Never the flat 1.235 the page used to apply.
        Assert.Equal("$4,109", RoleRows(Forecast, true)[0].Cost);
        Assert.NotEqual("$" + Js.ToLocaleString(Js.Round(3_516.2 * 1.235)), RoleRows(Forecast, true)[0].Cost);
    }

    [Fact]
    public void Adds_the_on_costs_to_the_hourly_rate_once()
    {
        var raw = (20_307.4 + 930.2) / 554.5;
        Assert.Equal("$38.30", HeadlineFigures(Forecast, false)[1].Value);
        Assert.Equal("$" + Js.ToFixed(raw * 1.1685, 2), HeadlineFigures(Forecast, true)[1].Value);
        Assert.Equal("$44.75", HeadlineFigures(Forecast, true)[1].Value);
    }

    [Fact]
    public void Splits_the_total_cost_into_parts_that_add_up_to_it()
    {
        foreach (var loaded in new[] { false, true })
        {
            var (total, parts) = MoneyParts(Forecast, loaded);
            var totalCost = CostFigures(Forecast, loaded).First(c => c.Key == CostKey.TotalCost);
            Close.To(totalCost.Dollars, total, 9);
            Close.To(total, parts.Aggregate(0.0, (s, p) => s + p.Dollars), 9);
            Close.To(1, parts.Aggregate(0.0, (s, p) => s + p.Share), 12);
        }
    }

    [Fact]
    public void Says_which_basis_the_costs_are_on_and_the_stores_rates()
    {
        Assert.Equal("super 12% and VIC payroll tax 4.85%", OnCostWords(Forecast));
        Assert.Equal("no on-costs", BasisNote(Forecast, false));
        Assert.Equal("incl. super 12% and NSW payroll tax 5.45%", BasisNote(Nsw, true));
        Assert.Equal("super 12%", OnCostWords(new OnCost(0.12, 0, null)));
    }

    [Fact]
    public void Keeps_the_share_of_sales_note_when_there_are_no_sales()
    {
        Assert.Equal("— % of sales (no sales data)", CostFigures(NoSales, true)[0].Pct);
    }

    // ── what changes for the test fortnight ──
    // The page as it stood before the fix, formula for formula: the seven cost
    // cards mixed bases (on-costs on four, none on three, whatever the switch),
    // the hourly rate carried on-costs before the switch added a flat 1.235, and
    // cost by position used that 1.235.

    private static Dictionary<string, string> BeforeFix(AnalysisFigureInput a, bool loaded)
    {
        static string M(double n) => "$" + Js.ToLocaleString(n, 0);
        var oc = a.OncostMultiplier;
        var flat = loaded ? 1.235 : 1;
        var hourlyWithOncosts = (a.TotalCost + a.CostOfLeave) * oc / a.TotalLabourHours;
        return new()
        {
            ["Avg hourly rate"] = "$" + Js.ToFixed(hourlyWithOncosts * flat, 2),
            ["Total cost"] = M((a.TotalCost + a.CostOfLeave + a.MakeUpCost) * oc),
            ["Worked payroll"] = M(a.TotalCost * oc),
            ["Total payroll"] = M((a.TotalCost + a.CostOfLeave) * oc),
            ["Leave"] = M(a.CostOfLeave),
            ["Make-up pay"] = M(a.MakeUpCost),
            ["Controllables"] = M(a.ControllablesCost),
            ["Bar: leave"] = M(a.CostOfLeave * oc),
            ["Bar: make-up pay"] = M(a.MakeUpCost * oc),
            ["SM cost"] = M(a.CostByRole[0].Cost * flat),
        };
    }

    private static Dictionary<string, string> AfterFix(AnalysisFigureInput a, bool loaded)
    {
        var c = CostFigures(a, loaded).ToDictionary(x => x.Label, x => x.Value);
        var parts = MoneyParts(a, loaded).Parts;
        return new()
        {
            ["Avg hourly rate"] = HeadlineFigures(a, loaded)[1].Value,
            ["Total cost"] = c["Total cost"],
            ["Worked payroll"] = c["Worked payroll"],
            ["Total payroll"] = c["Total payroll"],
            ["Leave"] = c["Leave"],
            ["Make-up pay"] = c["Make-up pay"],
            ["Controllables"] = c["Controllables"],
            ["Bar: leave"] = parts[1].Value,
            ["Bar: make-up pay"] = parts[2].Value,
            ["SM cost"] = RoleRows(a, loaded)[0].Cost,
        };
    }

    private static Dictionary<string, string> Changes(bool loaded)
    {
        var before = BeforeFix(Forecast, loaded);
        var after = AfterFix(Forecast, loaded);
        return before.Keys.Where(k => before[k] != after[k]).ToDictionary(k => k, k => $"{before[k]} -> {after[k]}");
    }

    [Fact]
    public void On_raw_wages_the_on_costs_come_off_the_four_figures_that_carried_them()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Avg hourly rate"] = "$44.75 -> $38.30",
                ["Total cost"] = "$31,795 -> $27,210",
                ["Worked payroll"] = "$23,729 -> $20,307",
                ["Total payroll"] = "$24,816 -> $21,238",
                ["Bar: leave"] = "$1,087 -> $930",
                ["Bar: make-up pay"] = "$6,979 -> $5,972",
            },
            Changes(false));
    }

    [Fact]
    public void On_fully_loaded_the_stores_own_rate_once_on_every_cost()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Avg hourly rate"] = "$55.27 -> $44.75",
                ["Leave"] = "$930 -> $1,087",
                ["Make-up pay"] = "$5,972 -> $6,979",
                ["Controllables"] = "$5,972 -> $6,979",
                ["SM cost"] = "$4,343 -> $4,109",
            },
            Changes(true));
    }

    // ── hours against the benchmark, from the held plan ──

    [Fact]
    public void Adds_the_groups_non_op_and_the_time_with_no_work_up_to_the_planned_hours()
    {
        var rows = HoursRows(Forecast);
        Assert.Equal(["checkout", "load_running", "reset", "admin", "specials", "cleaning", "non_op", "no_work"], rows.Select(r => r.Key));
        Close.To(Forecast.PlannedHours, rows.Aggregate(0.0, (s, r) => s + r.Planned), 9);
        Assert.Equal(new HoursTotal("622.06", "625.25", "+3.19"), HoursTotalOf(Forecast));
    }

    [Fact]
    public void Adds_the_groups_benchmark_as_shown_up_to_the_benchmark_shown()
    {
        var shown = HoursRows(Forecast).Where(r => r.Benchmark is not null);
        Assert.Equal(62206, shown.Aggregate(0.0, (n, r) => n + Js.Round(Js.Number(r.BenchmarkText) * 100)));
        Assert.Equal("622.06", HoursTotalOf(Forecast).Benchmark);
        // Shares a hundredth apart from their total once each is rounded: the
        // largest remainder takes the hundredth, so the column still adds up.
        var thirds = HoursRows(new[] { "reset", "admin", "cleaning" }.Select(g => new GroupHours(g, 10.0 / 3, 3.25)).ToList(), 0, 10);
        Assert.Equal(["3.34", "3.33", "3.33"], thirds.Select(r => r.BenchmarkText));
        // Each row's difference is the one its figures show.
        Assert.Equal(["-0.09", "-0.08", "-0.08"], thirds.Select(r => r.DeltaText));
    }

    [Fact]
    public void Shows_the_time_with_no_work_as_its_own_part_with_no_benchmark()
    {
        var noWork = HoursRows(Forecast).First(r => r.Key == "no_work");
        Assert.Equal(((double?)null, "-", "2.75", "+2.75", BarSide.Above), (noWork.Benchmark, noWork.BenchmarkText, noWork.PlannedText, noWork.DeltaText, noWork.Side));
        Assert.DoesNotContain(HoursRows(Forecast with { NoWorkHours = 0 }), r => r.Key == "no_work");
    }

    [Fact]
    public void Puts_each_groups_difference_on_its_side_of_the_centre_line()
    {
        var rows = HoursRows(Forecast).ToDictionary(r => r.Key);
        Assert.Equal(1, rows["no_work"].Bar);
        Assert.Equal((BarSide.Below, "-0.52"), (rows["load_running"].Side, rows["load_running"].DeltaText));
        Assert.Equal((BarSide.Above, "+0.54"), (rows["admin"].Side, rows["admin"].DeltaText));
        // The tills planned under Checkout's share of the benchmark.
        Assert.Equal((BarSide.Below, "-0.37"), (rows["checkout"].Side, rows["checkout"].DeltaText));
        Assert.Equal((BarSide.None, 0.0, "+0.00"), (rows["non_op"].Side, rows["non_op"].Bar, rows["non_op"].DeltaText));
        Close.To(0.35 / 2.75, rows["reset"].Bar, 9);
    }

    [Fact]
    public void Draws_no_bars_when_every_row_is_on_its_benchmark()
    {
        var flat = HoursRows([new GroupHours("reset", 10, 10)], 0, 10);
        Assert.Equal((BarSide.None, 0.0), (flat[0].Side, flat[0].Bar));
    }

    // ── cost by position bars ──

    [Fact]
    public void Measures_each_role_against_the_costliest_one()
    {
        var raw = RoleRows(Forecast, false);
        Assert.Equal(1, raw.First(r => r.Role == "SA").Bar);
        Close.To(3_516.2 / 9_389.7, raw.First(r => r.Role == "SM").Bar, 9);
        Assert.Equal(raw.Select(r => r.Bar), RoleRows(Forecast, true).Select(r => r.Bar));
    }

    [Fact]
    public void Keeps_a_role_with_no_shifts_with_no_figures_and_no_bar()
    {
        var jsa = RoleRows(Forecast, true).First(r => r.Role == "JSA");
        Assert.Equal(new RoleRow("JSA", "-", "-", "-", "-", 0, true), jsa);
    }
}
