// Port of src/lib/plan/pageFigures.ts.
// ─── The held plan's fortnight in figures ────────────────────────────────────
// What the Plan page's fortnight cards and day chips show. Counted as the
// Review counts them (loadReview in lib/review/server.ts): worked hours
// against the benchmark file (owner decision R2), the shifts planned, and
// every flag a check graded by lib/review/checks.ts. Pure.

using System.Globalization;
using Roster.Engine.Core;

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
