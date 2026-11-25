// Port of src/lib/analysis/figures.ts.
// ─── The Analysis page's figures, as text, and the shapes it draws them in ──
// Pure, so the page's numbers are testable: the loading half of
// lib/analysis/compute.ts is the page service.
//
// Costs are on ONE basis at a time, chosen by the Cost basis switch (owner,
// 15/11/2026: "correct to the stores own rate"): Raw wages carries no on-costs
// anywhere; Fully loaded is raw wages times the store's own on-cost factor -
// its super rate plus its state's payroll tax, from Pay Rates - applied once,
// to every cost on the page: the totals, the money bar, cost by position and
// the hourly rate.
//
// Hours against the benchmark come from the held plans: the planned hours are
// the Plan page's (lib/plan/plannedHours), and the groups, non-op and the time
// with no work in it add up to them; the groups' benchmark adds up to the
// benchmark, as on the Plan page's day table (lib/plan/benchmarkSplit).
//
// Takes the shape of AnalysisResult that it reads, so a test can build one by
// hand.

using Roster.Engine.Core;
using Roster.Engine.Overview;
using Roster.Engine.Planning.Plan;

namespace Roster.Engine.Analysis;

public enum Basis { Actual, Forecast, Mixed }

/// <summary>The store's on-costs: super and its state's payroll tax, from Pay Rates.</summary>
public sealed record OnCost(double SuperRate, double PayrollTaxRate, string? State);

/// <summary>One position's wages over the range (raw wages, no on-costs).</summary>
public sealed record RoleCost(string Role, double Cost, double Hours, int Shifts);

/// <summary>One labour group's share of the benchmark and its planned hours over the range.</summary>
public sealed record GroupHours(string Group, double Benchmark, double Planned);

/// <summary>The figures the Analysis page writes (the parts of AnalysisResult figures.ts reads).</summary>
public record AnalysisFigureInput
{
    public Basis Basis { get; init; } = Basis.Forecast;
    public int ActualDays { get; init; }
    public int ForecastDays { get; init; }
    public double Sales { get; init; }
    public double? Iplh { get; init; }
    public double? OePct { get; init; }
    /// <summary>Raw wages for the hours worked.</summary>
    public double TotalCost { get; init; }
    public double TotalLabourHours { get; init; }
    public double CostOfLeave { get; init; }
    public double MakeUpCost { get; init; }
    public double MakeUpHoursTotal { get; init; }
    public double ControllablesCost { get; init; }
    /// <summary>The store's own on-cost factor: 1 + super + its state's payroll tax.</summary>
    public double OncostMultiplier { get; init; } = 1;
    public OnCost Oncost { get; init; } = new(0, 0, null);
    public IReadOnlyList<RoleCost> CostByRole { get; init; } = [];
    public IReadOnlyList<GroupHours> HoursByGroup { get; init; } = [];
    public double NoWorkHours { get; init; }
    public double PlannedHours { get; init; }
    public double BenchmarkHours { get; init; }
    public int DaysWithoutPlan { get; init; }
}

public enum CostKey { TotalCost, WorkedPayroll, Leave, MakeUp, TotalPayroll, Controllables }

public sealed record CostFigure(CostKey Key, string Label, double Dollars, string Value, string Pct, string Sub);

public sealed record HeadlineFigure(string Label, string Value, string Sub);

public enum MoneyPartKey { Worked, Leave, MakeUp }

/// <param name="Dollars">The part on the chosen basis; the three add up to the total cost.</param>
/// <param name="Share">Its share of the total cost, 0-1: the width of its block in the bar.</param>
public sealed record MoneyPart(MoneyPartKey Key, string Label, double Dollars, string Value, string Pct, double Share);

/// <param name="Bar">Bar length, 0-1: the role's cost against the costliest role's.</param>
/// <param name="Empty">No shifts in the range: the page shows "-" for every figure.</param>
public sealed record RoleRow(string Role, string Shifts, string Hours, string Cost, string ShiftShare, double Bar, bool Empty);

/// <summary>Which half of the centred bar a difference is drawn in.</summary>
public enum BarSide { Above, Below, None }

/// <param name="Key">A labour group, "non_op", or "no_work" for the planned time with no work in it.</param>
/// <param name="Bar">Bar length either side of the centre line, 0-1 of one half.</param>
public sealed record HoursRow(
    string Key,
    double? Benchmark,
    double Planned,
    string BenchmarkText,
    string PlannedText,
    double Delta,
    string DeltaText,
    double Bar,
    BarSide Side);

public sealed record HoursTotal(string Benchmark, string Planned, string DeltaText);

public static class Figures
{
    /// <summary>
    /// What the Cost basis switch multiplies every cost by: the store's own on-cost
    /// factor when fully loaded, nothing when raw.
    /// </summary>
    public static double CostFactor(AnalysisFigureInput a, bool fullyLoaded) => fullyLoaded ? a.OncostMultiplier : 1;

    public static string MoneyText(double n) => Money.Money0(n);

    /// <summary>0.12 -> "12%", 0.0485 -> "4.85%".</summary>
    private static string RateText(double rate) => $"{Js.NumberToString(Js.Number(Js.ToFixed(rate * 100, 2)))}%";

    /// <summary>"super 12% and VIC payroll tax 4.85%": the store's on-costs, in words.</summary>
    public static string OnCostWords(OnCost oncost)
    {
        var tax = oncost.PayrollTaxRate > 0
            ? $" and {(oncost.State is { Length: > 0 } state ? state + " " : "")}payroll tax {RateText(oncost.PayrollTaxRate)}"
            : "";
        return $"super {RateText(oncost.SuperRate)}{tax}";
    }

    public static string OnCostWords(AnalysisFigureInput a) => OnCostWords(a.Oncost);

    /// <summary>What the costs on the page include: "no on-costs", or the store's own.</summary>
    public static string BasisNote(AnalysisFigureInput a, bool fullyLoaded) =>
        fullyLoaded ? $"incl. {OnCostWords(a)}" : "no on-costs";

    // Short word for the basis, used in every metric label so a reader always
    // knows whether a number happened or is expected to.
    public static string BasisWord(Basis b) => b == Basis.Actual ? "actual" : b == Basis.Forecast ? "forecast" : "blended";

    public static string SalesSub(Basis b) =>
        b == Basis.Actual ? "actual sales in range" : b == Basis.Forecast ? "forecast sales for the range" : "actual to date + forecast ahead";

    /// <summary>The basis as a short phrase, for the pill beside the title.</summary>
    public static string BasisLabel(AnalysisFigureInput a) =>
        a.Basis == Basis.Actual
            ? "Actual · these days have happened"
            : a.Basis == Basis.Forecast
                ? "Forecast · these days are still to come"
                : $"Mixed · {a.ActualDays} day{(a.ActualDays == 1 ? "" : "s")} actual, {a.ForecastDays} forecast";

    /// <summary>"1.9% of forecast sales", or the page's long-standing note when there are no sales.</summary>
    public static string PctOfSales(double dollars, AnalysisFigureInput a) =>
        a.Sales > 0
            ? $"{Js.ToFixed(dollars / a.Sales * 100, 1)}% of {BasisWord(a.Basis)} sales"
            : "— % of sales (no sales data)";

    /// <summary>The cost figures, every one on the chosen basis, each in dollars and as a share of sales.</summary>
    public static List<CostFigure> CostFigures(AnalysisFigureInput a, bool fullyLoaded)
    {
        var f = CostFactor(a, fullyLoaded);
        var rows = new (CostKey Key, string Label, double Dollars, string Sub)[]
        {
            (CostKey.TotalCost, "Total cost", (a.TotalCost + a.CostOfLeave + a.MakeUpCost) * f, "worked + leave + make-up"),
            (CostKey.WorkedPayroll, "Worked payroll", a.TotalCost * f, "wages for the hours worked"),
            (CostKey.Leave, "Leave", a.CostOfLeave * f, "paid leave in this range"),
            (CostKey.MakeUp, "Make-up pay", a.MakeUpCost * f, $"{Js.ToFixed(a.MakeUpHoursTotal, 1)} h under contract · whole fortnight"),
            (CostKey.TotalPayroll, "Total payroll", (a.TotalCost + a.CostOfLeave) * f, "worked + leave"),
            (CostKey.Controllables, "Controllables", a.ControllablesCost * f, "allowances, overtime & make-up pay"),
        };
        return rows.Select(r => new CostFigure(r.Key, r.Label, r.Dollars, MoneyText(r.Dollars), PctOfSales(r.Dollars, a), r.Sub)).ToList();
    }

    /// <summary>The four headline figures; the hourly rate is on the chosen cost basis.</summary>
    public static List<HeadlineFigure> HeadlineFigures(AnalysisFigureInput a, bool fullyLoaded)
    {
        var word = BasisWord(a.Basis);
        double? rate = a.TotalLabourHours > 0 ? (a.TotalCost + a.CostOfLeave) * CostFactor(a, fullyLoaded) / a.TotalLabourHours : null;
        return
        [
            new($"Sales · {word}", MoneyText(a.Sales), SalesSub(a.Basis)),
            new(
                "Avg hourly rate",
                rate is { } r ? "$" + Js.ToFixed(r, 2) : "-",
                $"total payroll incl. leave / hours worked · {(fullyLoaded ? "fully loaded" : "raw wages")}"),
            new($"IPLH · {word}", a.Iplh is { } iplh ? Js.ToFixed(iplh, 0) : "-", "items / labour hr"),
            new(
                $"OE · {word}",
                a.OePct is { } oe ? Js.ToFixed(oe, 1) + "%" : "-",
                a.Basis == Basis.Forecast ? "rostered vs target hours" : "worked vs target hours"),
        ];
    }

    // ─── Where the money goes ────────────────────────────────────────────────────

    /// <summary>The total cost split into its three parts, on the chosen basis.</summary>
    public static (double Total, List<MoneyPart> Parts) MoneyParts(AnalysisFigureInput a, bool fullyLoaded)
    {
        var f = CostFactor(a, fullyLoaded);
        var raw = new (MoneyPartKey Key, string Label, double Dollars)[]
        {
            (MoneyPartKey.Worked, "Worked payroll", a.TotalCost * f),
            (MoneyPartKey.Leave, "Leave", a.CostOfLeave * f),
            (MoneyPartKey.MakeUp, "Make-up pay", a.MakeUpCost * f),
        };
        var total = (a.TotalCost + a.CostOfLeave + a.MakeUpCost) * f;
        return (total, raw
            .Select(p => new MoneyPart(
                p.Key,
                p.Label,
                p.Dollars,
                MoneyText(p.Dollars),
                PctOfSales(p.Dollars, a),
                total > 0 ? Math.Max(0, p.Dollars) / total : 0))
            .ToList());
    }

    // ─── Cost by position ────────────────────────────────────────────────────────

    public static List<RoleRow> RoleRows(AnalysisFigureInput a, bool fullyLoaded)
    {
        var f = CostFactor(a, fullyLoaded);
        var sum = a.CostByRole.Sum(x => x.Shifts);
        var totalShifts = sum != 0 ? sum : 1;
        var maxCost = Js.Max(a.CostByRole.Where(r => r.Shifts != 0).Select(r => r.Cost).Prepend(0));
        return a.CostByRole
            .Select(r => new RoleRow(
                r.Role,
                r.Shifts != 0 ? r.Shifts.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-",
                r.Shifts != 0 ? Js.ToFixed(r.Hours, 1) : "-",
                r.Shifts != 0 ? MoneyText(r.Cost * f) : "-",
                r.Shifts != 0 ? Js.ToFixed((double)r.Shifts / totalShifts * 100, 0) + "%" : "-",
                // One factor on every role: the same picture on either basis.
                r.Shifts != 0 && maxCost > 0 ? Math.Max(0, r.Cost) / maxCost : 0,
                r.Shifts == 0))
            .ToList();
    }

    // ─── Hours against the benchmark ─────────────────────────────────────────────

    /// <summary>Signed hours, as the page has always written them: "+0.58", "-0.52".</summary>
    public static string SignedHours(double d) => (d >= 0 ? "+" : "") + Js.ToFixed(d, 2);

    public static List<HoursRow> HoursRows(AnalysisFigureInput a) => HoursRows(a.HoursByGroup, a.NoWorkHours, a.BenchmarkHours);

    /// <summary>
    /// One row per group (each against its share of the benchmark), non-op when there
    /// is any, and the planned time with no work in it (held for cover, the shortest
    /// legal shift, a manager's handover, a fixed roster), which has no benchmark of its
    /// own. The planned hours of the rows add up to the plan's, and the groups'
    /// benchmark, as shown, to the benchmark shown (owner, 15/11/2026): the shares are
    /// rounded to hundredths together, the largest remainders taking the hundredths
    /// left over (lib/plan/benchmarkSplit). Each row's difference is the one its
    /// figures show.
    /// </summary>
    public static List<HoursRow> HoursRows(IReadOnlyList<GroupHours> hoursByGroup, double noWorkHours, double benchmarkHours)
    {
        var parts = new JsMap<string, double>();
        foreach (var h in hoursByGroup) parts.Set(h.Group, h.Benchmark);
        var shown = BenchmarkSplit.ToHundredths(parts, benchmarkHours, hoursByGroup.Select(h => h.Group).ToList());
        var rows = hoursByGroup
            .Select(h => (Key: h.Group, Benchmark: (double?)(shown[h.Group] / 100), h.Planned))
            .ToList();
        if (Math.Abs(noWorkHours) >= 0.005) rows.Add(("no_work", null, noWorkHours));
        var deltas = rows.Select(r => (BenchmarkSplit.Hundredths(r.Planned) - (r.Benchmark is null ? 0 : shown[r.Key])) / 100).ToList();
        var maxAbs = Js.Max(deltas.Select(Math.Abs).Prepend(0));
        return rows
            .Select((r, i) =>
            {
                var d = deltas[i];
                // A difference that rounds to 0.00 draws no bar: it is on the line.
                var visible = Math.Abs(d) >= 0.005 && maxAbs > 0;
                return new HoursRow(
                    r.Key,
                    r.Benchmark,
                    r.Planned,
                    r.Benchmark is not null ? BenchmarkSplit.HoursText(shown[r.Key]) : "-",
                    Js.ToFixed(r.Planned, 2),
                    d,
                    SignedHours(d),
                    visible ? Math.Abs(d) / maxAbs : 0,
                    !visible ? BarSide.None : d > 0 ? BarSide.Above : BarSide.Below);
            })
            .ToList();
    }

    /// <summary>The range's planned hours and benchmark: the Plan page's for the same days.</summary>
    public static HoursTotal HoursTotalOf(AnalysisFigureInput a) =>
        new(Js.ToFixed(a.BenchmarkHours, 2), Js.ToFixed(a.PlannedHours, 2), SignedHours(a.PlannedHours - a.BenchmarkHours));
}
