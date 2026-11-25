// Port of src/lib/overview/storeRows.ts.
// ─── "Your stores": one row per store an area login looks after ─────────────
// Each store's plan, review and roster status for the fortnight, its plan days
// with an error, its waiting day-off requests and its efficiency month to
// date. Pure: the page service reads the statuses and the held plans' day
// results (never solving or generating anything) and hands them in.

using Roster.Engine.Core;

namespace Roster.Engine.Overview;

public sealed record Chip(ChipTone Tone, string Label);

/// <summary>The held plan: its version and each day's OK, in date order (null when the plan holds no such day).</summary>
public sealed record HeldPlanStatus(double Version, IReadOnlyList<bool?> Ok);

/// <summary>The review's row: its stored status and the plan it was made on ("plan:3").</summary>
public sealed record StoreReviewRow(string Status, string? SnapshotKey);

/// <param name="PublishedDays">Days of the fortnight published to the floor.</param>
/// <param name="FortnightDays">How many days the fortnight has.</param>
/// <param name="RosterBuilt">A roster has been built from the plan (held, maybe not published).</param>
public sealed record StoreStatusInput(
    HeldPlanStatus? Plan,
    StoreReviewRow? Review,
    int PublishedDays,
    int FortnightDays,
    bool RosterBuilt);

/// <param name="ErrorDays">Days the held plan marks as not OK, as the Plan page shows them; null without a plan.</param>
public sealed record StoreStatus(Chip Plan, Chip Review, Chip Roster, int? ErrorDays);

/// <summary>Where a store's review stands for the plan as it is now.</summary>
public enum ReviewState { NotStarted, InProgress, Submitted, Returned, Approved, Stale }

/// <summary>One actuals_days row, as the month-to-date efficiency reads it.</summary>
public sealed record StoreActualsRow(string StoreId, double? ActualHours100, double? UkgActualHours);

/// <summary>One non_op_entries row, as the month-to-date efficiency reads it.</summary>
public sealed record StoreNonOpRow(string StoreId, double? Minutes);

public static class StoreRows
{
    /// <summary>
    /// The review's state for the plan as it stands - lib/review's rule: an
    /// approval or submission made on an earlier version of the plan no longer
    /// counts.
    /// </summary>
    public static ReviewState ReviewStateOf(StoreReviewRow? review, HeldPlanStatus? plan)
    {
        if (review is null) return ReviewState.NotStarted;
        var current = plan is not null && review.SnapshotKey == $"plan:{Js.NumberToString(plan.Version)}";
        if (review.Status == "approved") return current ? ReviewState.Approved : ReviewState.InProgress;
        if (review.Status == "submitted") return current ? ReviewState.Submitted : ReviewState.Stale;
        if (review.Status == "returned") return ReviewState.Returned;
        return ReviewState.InProgress;
    }

    public static StoreStatus StoreStatusOf(StoreStatusInput s)
    {
        var review = ReviewStateOf(s.Review, s.Plan);
        var approved = review == ReviewState.Approved;
        var published = s.FortnightDays > 0 && s.PublishedDays >= s.FortnightDays;
        return new StoreStatus(
            s.Plan is not null ? new(ChipTone.Green, "Generated") : new(ChipTone.Red, "Not generated"),
            s.Plan is null
                ? new(ChipTone.Grey, "-")
                : approved
                    ? new(ChipTone.Green, "Approved")
                    : review == ReviewState.Submitted
                        ? new(ChipTone.Amber, "Submitted")
                        : review == ReviewState.Returned
                            ? new(ChipTone.Red, "Returned")
                            : review == ReviewState.Stale
                                ? new(ChipTone.Amber, "Plan changed")
                                : new(ChipTone.Soft, "Not submitted"),
            published
                ? new(ChipTone.Green, "Published")
                : approved && s.RosterBuilt
                    ? new(ChipTone.Amber, "Not published")
                    : approved
                        ? new(ChipTone.Soft, "To build")
                        : new(ChipTone.Grey, s.Plan is not null ? "Waiting" : "-"),
            s.Plan is not null ? s.Plan.Ok.Count(ok => ok == false) : null);
    }

    // ─── Efficiency month to date, per store ─────────────────────────────────────
    // The Dashboard's own OE, store by store: the actuals' hours at 100% (grossed
    // up by the paid-break allowance) over the hours paid less non-op, pooled over
    // the month to date (OverviewCompute sums the same columns per date and
    // divides once).

    public static JsMap<string, double?> OeByStore(
        IEnumerable<StoreActualsRow> actuals,
        IEnumerable<StoreNonOpRow> nonOp,
        double breakFactorPct)
    {
        var sums = new JsMap<string, double[]>(); // [ah, kh, nonOp]
        double[] At(string id)
        {
            if (!sums.TryGet(id, out var s)) sums.Set(id, s = [0, 0, 0]);
            return s;
        }
        foreach (var a in actuals)
        {
            var s = At(a.StoreId);
            s[0] += Metrics.WithPaidBreaks(a.ActualHours100 ?? 0, breakFactorPct);
            s[1] += a.UkgActualHours ?? 0;
        }
        foreach (var n in nonOp) At(n.StoreId)[2] += (n.Minutes ?? 0) / 60;
        var oe = new JsMap<string, double?>();
        foreach (var (id, s) in sums) oe.Set(id, Metrics.OperationalEfficiencyPct(s[0], s[1], s[2]));
        return oe;
    }
}
