// Port of src/lib/overview/fortnight.ts.
// ─── The fortnight's way through Plan -> Review -> Approval -> Roster ───────
// What the Dashboard's fortnight card says: the four steps with when each
// happened, a status pill, the button to the next step, and the fortnight's
// figures. Pure: the page reads the statuses and the held plan (never solving
// anything) and hands them in.

using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Engine.Overview;

/// <summary>
/// A pill's colour, as the TypeScript app's <c>PillTone</c> names them; the page maps it
/// onto the design system's pill one to one.
/// </summary>
public enum ChipTone { Navy, Soft, Violet, Gold, Amber, Green, Red, Grey }

/// <summary>The review's status ("in_progress" | "submitted" | "returned" | "approved").</summary>
public enum ReviewStatus { InProgress, Submitted, Returned, Approved }

public static class ReviewStatuses
{
    /// <summary>The stored value ("in_progress", …) as a status; anything unknown reads as in progress.</summary>
    public static ReviewStatus Parse(string? stored) => stored switch
    {
        "submitted" => ReviewStatus.Submitted,
        "returned" => ReviewStatus.Returned,
        "approved" => ReviewStatus.Approved,
        _ => ReviewStatus.InProgress,
    };

    public static string Key(this ReviewStatus status) => status switch
    {
        ReviewStatus.InProgress => "in_progress",
        ReviewStatus.Submitted => "submitted",
        ReviewStatus.Returned => "returned",
        ReviewStatus.Approved => "approved",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>The held plan, with when it was generated ("Sun 15 Nov, 7:48 am").</summary>
public sealed record FortnightPlan(string? GeneratedAt);

/// <param name="Stale">Submitted, but the plan has changed since.</param>
public sealed record FortnightReview(
    ReviewStatus Status,
    bool Stale,
    string? SubmittedAt,
    string? DecidedAt,
    string? ReturnMessage);

/// <param name="Published">Every day of the fortnight published to the floor.</param>
/// <param name="BuiltAt">When the held roster was built, if it has been.</param>
public sealed record FortnightRoster(bool Published, string? BuiltAt);

/// <param name="HasForecast">Benchmark files are loaded for the fortnight, so a plan can be made.</param>
public sealed record FortnightState(
    string StoreId,
    DateOnly PeriodStart,
    DateOnly Today,
    bool HasForecast,
    FortnightPlan? Plan,
    FortnightReview Review,
    FortnightRoster Roster);

/// <summary>A step's state, as the step track draws it.</summary>
public enum StepStatus { Done, Current, Attention, Todo }

/// <summary>One of the four steps: what it is and, under it, when it happened or what it waits for.</summary>
public sealed record FortnightStep(string Key, string Title, StepStatus State, string Detail);

/// <param name="Lock">The plan is approved and locked.</param>
public sealed record Headline(ChipTone Tone, string Text, bool Lock);

/// <summary>A button: its words and where it goes.</summary>
public sealed record LinkAction(string Label, string Href);

public sealed record NextActions(LinkAction Primary, LinkAction? Secondary);

public sealed record FortnightLinks(string Plan, string Review, string Roster);

public sealed record FortnightFigures(double? Forecast, double? Benchmark, double? Planned, double? Shifts);

/// <summary>A day of the fortnight as the Review page counts it.</summary>
public sealed record FortnightFigureDay(double? Forecast, double Benchmark, double Planned, double PlannedShifts);

public static class Fortnight
{
    /// <summary>"Mon 23 Nov", read from the ISO date itself (no time zone involved).</summary>
    public static string DayLabel(DateOnly date) => Dates.AuDayLabel(date);

    /// <summary>"23 Nov – 6 Dec"</summary>
    public static string RangeLabel(DateOnly start, DateOnly end)
    {
        static string Dm(DateOnly d) => DayLabel(d)[4..];
        return $"{Dm(start)} – {Dm(end)}";
    }

    // JavaScript's \s and trim(): the ECMAScript WhiteSpace and LineTerminator
    // characters (.NET's \s and Trim() differ on U+0085 and U+FEFF).
    private const string JsSpace = "\t\n\v\f\r                  　﻿";
    private static readonly Regex Spaces = new("[\t\n\v\f\r    -     　﻿]+", RegexOptions.CultureInvariant);

    private static string Shorten(string text, int max)
    {
        var t = Spaces.Replace(text.Trim(JsSpace.ToCharArray()), " ");
        return t.Length > max ? t[..(max - 1)].TrimEnd(JsSpace.ToCharArray()) + "…" : t;
    }

    public static List<FortnightStep> FortnightSteps(FortnightState f)
    {
        var (plan, review, roster) = (f.Plan, f.Review, f.Roster);
        var approved = plan is not null && review.Status == ReviewStatus.Approved;
        var submitted = plan is not null && (review.Status == ReviewStatus.Submitted || approved);
        return
        [
            plan is not null
                ? new("plan", "Plan generated", StepStatus.Done, plan.GeneratedAt ?? "Held")
                : f.HasForecast
                    ? new("plan", "Plan generated", StepStatus.Current, "Not generated yet")
                    : new("plan", "Plan generated", StepStatus.Todo, "Waiting for the forecast"),
            plan is null
                ? new("review", "Store review", StepStatus.Todo, "After the plan")
                : review.Status == ReviewStatus.Returned
                    ? new("review", "Store review", StepStatus.Attention,
                        review.ReturnMessage is { Length: > 0 } message
                            ? $"Returned: “{Shorten(message, 70)}”"
                            : "Returned by the EMSO")
                    : review.Stale
                        ? new("review", "Store review", StepStatus.Attention, "The plan changed after it was submitted")
                        : submitted
                            ? new("review", "Store review", StepStatus.Done,
                                review.SubmittedAt is { Length: > 0 } at ? $"Submitted {at}" : "Submitted")
                            : new("review", "Store review", StepStatus.Current, "Not submitted yet"),
            approved
                ? new("approval", "EMSO approved", StepStatus.Done, review.DecidedAt ?? "Approved")
                : submitted && !review.Stale
                    ? new("approval", "EMSO approved", StepStatus.Current, "With the EMSO to approve")
                    : new("approval", "EMSO approved", StepStatus.Todo, "After the store's review"),
            roster.Published
                ? new("roster", "Roster published", StepStatus.Done,
                    $"On the floor {(f.Today >= f.PeriodStart ? "since" : "from")} {DayLabel(f.PeriodStart)}")
                : approved
                    ? new("roster", "Roster published", StepStatus.Current,
                        roster.BuiltAt is { Length: > 0 } built ? $"Built {built}, not published yet" : "Ready to build")
                    : new("roster", "Roster published", StepStatus.Todo, "After approval"),
        ];
    }

    public static Headline FortnightHeadline(FortnightState f)
    {
        var approved = f.Plan is not null && f.Review.Status == ReviewStatus.Approved;
        if (f.Roster.Published && approved) return new(ChipTone.Green, "Approved · roster published", true);
        if (f.Roster.Published) return new(ChipTone.Amber, "Roster published · plan changed since", false);
        if (approved) return new(ChipTone.Green, "Plan approved · locked", true);
        if (f.Plan is null) return new(ChipTone.Grey, "Not started", false);
        if (f.Review.Status == ReviewStatus.Returned) return new(ChipTone.Red, "Returned to the store", false);
        if (f.Review.Status == ReviewStatus.Submitted && !f.Review.Stale) return new(ChipTone.Soft, "Waiting for approval", false);
        return new(ChipTone.Soft, "Plan in review", false);
    }

    /// <summary>The fortnight has started and its roster still isn't on the floor.</summary>
    public static bool IsOverdue(FortnightState f) => !f.Roster.Published && f.Today >= f.PeriodStart;

    public static FortnightLinks FortnightLinksFor(string storeId, DateOnly periodStart)
    {
        var q = $"store={storeId}&period={periodStart.ToIso()}";
        return new($"/plan/generate?{q}", $"/plan/review?{q}", $"/plan/roster?{q}");
    }

    /// <summary>
    /// The button to the next step, and a quieter one beside it. Someone who can't
    /// act (a read-only login, or a store waiting for its EMSO) gets the page to
    /// look at instead.
    /// </summary>
    public static NextActions NextActionsFor(FortnightState f, bool canWrite, bool isReviewer)
    {
        var links = FortnightLinksFor(f.StoreId, f.PeriodStart);
        var openPlan = new LinkAction("Open the plan", links.Plan);
        var approved = f.Plan is not null && f.Review.Status == ReviewStatus.Approved;
        if (f.Plan is null)
        {
            // Nothing can be generated before the forecast is loaded.
            var label = canWrite && f.HasForecast ? "Generate the plan" : "Open the plan";
            return new(new(label, links.Plan), null);
        }
        if (f.Roster.Published) return new(new("Open the roster", links.Roster), openPlan);
        if (approved)
            return new(new(canWrite ? "Build and publish the roster" : "Open the roster", links.Roster), openPlan);
        if (f.Review.Status == ReviewStatus.Submitted && !f.Review.Stale)
            return new(new(canWrite && isReviewer ? "Review and approve" : "Open the review", links.Review), openPlan);
        return new(new(canWrite ? "Check and submit the plan" : "Open the review", links.Review), openPlan);
    }

    /// <summary>
    /// The fortnight's figures as the Review page counts them, day by day: the
    /// forecast from the benchmark file, and the benchmark, planned hours and
    /// shifts from the held plan. Without a plan only the forecast is known.
    /// </summary>
    public static FortnightFigures FortnightFiguresFor(IReadOnlyList<FortnightFigureDay> days, bool hasPlan)
    {
        var withForecast = days.Where(d => d.Forecast is not null).ToList();
        return new(
            withForecast.Count > 0 ? withForecast.Aggregate(0.0, (s, d) => s + (d.Forecast ?? 0)) : null,
            hasPlan ? days.Aggregate(0.0, (s, d) => s + d.Benchmark) : null,
            hasPlan ? days.Aggregate(0.0, (s, d) => s + d.Planned) : null,
            hasPlan ? days.Aggregate(0.0, (s, d) => s + d.PlannedShifts) : null);
    }
}
