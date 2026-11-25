// Port of src/lib/plan/pageFigures.ts.
// ─── The held plan's fortnight in figures ────────────────────────────────────
// What the Plan page's fortnight cards and day chips show. Counted as the
// Review counts them (loadReview in lib/review/server.ts): worked hours
// against the benchmark file (owner decision R2), the shifts planned, and
// every flag a check graded by lib/review/checks.ts. Pure.

using System.Globalization;
using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;

namespace Roster.Web.Features.Plan;

/// <summary>One day of the held plan in figures.</summary>
public sealed record PlanDayFigures
{
    public required DateOnly Date { get; init; }
    public bool Closed { get; init; }

    /// <summary>A plan was made for the day (not closed, inputs there).</summary>
    public bool Planned { get; init; }

    /// <summary>The held day's status: no error, hours met (or closed).</summary>
    public bool Ok { get; init; }

    // Every day counts towards the fortnight's hours, as on the Review.
    public double Benchmark { get; init; }
    public double Worked { get; init; }

    /// <summary>worked - benchmark, hours: the day's variance once it is planned.</summary>
    public double Variance { get; init; }

    public int Shifts { get; init; }
    public int Errors { get; init; }
    public int Warnings { get; init; }
}

/// <summary>The held plan's fortnight in figures: the cards above the day chips.</summary>
public sealed record PlanFortnightFigures
{
    public IReadOnlyList<PlanDayFigures> Days { get; init; } = [];
    public int OkDays { get; init; }
    public int ErrorDays { get; init; }
    public int PlannedDays { get; init; }

    /// <summary>Planned days whose hours are within the tolerance of the benchmark.</summary>
    public int WithinDays { get; init; }

    public double Benchmark { get; init; }
    public double Worked { get; init; }
    public double NonOp { get; init; }
    public int Shifts { get; init; }
    public int Errors { get; init; }
    public int Warnings { get; init; }
}

public static partial class PageFigures
{
    public static PlanFortnightFigures PlanFortnightFigures(IReadOnlyList<PlanSnapshotDay> days)
    {
        var out_ = days.Select(d =>
        {
            var (result, plan) = (d.Result, d.Plan);
            var closed = result.Closed;
            var planned = !closed && result.HasData && plan?.HasData == true;
            // The one definition of planned hours every page reads (HeldDayHours).
            var held = new HeldPlanDay(BenchmarkParts.Of(result), plan);
            var benchmark = HeldDayHours.BenchmarkHours(held);
            var worked = HeldDayHours.PlannedHours(held);
            var checks = planned ? ReviewChecks.FlagsToChecks(d.Date, [.. result.Flags, .. plan?.Flags ?? []]) : [];
            return new PlanDayFigures
            {
                Date = d.Date,
                Closed = closed,
                Planned = planned,
                Ok = d.Ok,
                Benchmark = benchmark,
                Worked = worked,
                Variance = worked - benchmark,
                Shifts = plan?.Shifts.Count ?? 0,
                Errors = checks.Count(c => c.Severity == Severity.Error),
                Warnings = checks.Count(c => c.Severity == Severity.Warning),
            };
        }).ToList();
        double Sum(Func<PlanDayFigures, double> f) => out_.Aggregate(0.0, (n, d) => n + f(d));
        var plannedDays = out_.Where(d => d.Planned).ToList();
        return new PlanFortnightFigures
        {
            Days = out_,
            OkDays = out_.Count(d => d.Ok),
            ErrorDays = out_.Count(d => !d.Ok),
            PlannedDays = plannedDays.Count,
            WithinDays = plannedDays.Count(d => Math.Abs(d.Variance) <= ReviewChecks.HoursTolerance + 1e-9),
            Benchmark = Sum(d => d.Benchmark),
            Worked = Sum(d => d.Worked),
            NonOp = days.Aggregate(0.0, (n, d) => n + (d.Result.Allocations?.FirstOrDefault(a => a.Group == "non_op")?.BenchmarkHours ?? 0)),
            Shifts = (int)Sum(d => d.Shifts),
            Errors = (int)Sum(d => d.Errors),
            Warnings = (int)Sum(d => d.Warnings),
        };
    }

    /// <summary>"+0.09" or "−0.13": a signed figure, two decimals (the minus is U+2212).</summary>
    public static string Signed(double n)
    {
        var r = Math.Abs(n) < 0.005 ? 0 : n;
        return $"{(r < 0 ? "−" : "+")}{Js.ToFixed(Math.Abs(r), 2)}";
    }

    /// <summary>"Tuesday 24 November" from 2026-11-24 (en-AU's long weekday and month, commas removed).</summary>
    public static string LongDayLabel(DateOnly date)
    {
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        return $"{names.GetDayName(date.DayOfWeek)} {date.Day.ToString(CultureInfo.InvariantCulture)} {names.GetMonthName(date.Month)}";
    }
}
