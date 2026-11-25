// New: the Review page's view models. They have the shape of the TypeScript app's review
// types (src/lib/review/server.ts: ReviewStatus, ReviewCheckView, ReviewDayView,
// ReviewEventView, ReviewData, QueueStore; src/lib/review/checks.ts: Severity), field for
// field, so the page reads like ReviewView.tsx. The data layer reads the review; the page's
// service (IReviewService) hands it over in these.

namespace Roster.Web.Features.Review;

/// <summary>Where a fortnight's review is up to (ReviewStatus).</summary>
public enum ReviewStatus
{
    /// <summary>"in_progress": with the store, not submitted (or withdrawn, or unlocked).</summary>
    InProgress,

    /// <summary>"submitted": with the EMSO.</summary>
    Submitted,

    /// <summary>"returned": the EMSO queried it; back with the store.</summary>
    Returned,

    /// <summary>"approved": the plan is signed off and locked; the roster can be built.</summary>
    Approved,
}

/// <summary>The stored values of <see cref="ReviewStatus"/> (roster_reviews.status).</summary>
public static class ReviewStatusKeys
{
    public const string InProgress = "in_progress", Submitted = "submitted", Returned = "returned", Approved = "approved";

    public static string Key(this ReviewStatus status) => status switch
    {
        ReviewStatus.InProgress => InProgress,
        ReviewStatus.Submitted => Submitted,
        ReviewStatus.Returned => Returned,
        ReviewStatus.Approved => Approved,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>The status a stored value names; null for anything else (no row reads as in progress).</summary>
    public static ReviewStatus? FromKey(string? key) => key switch
    {
        InProgress => ReviewStatus.InProgress,
        Submitted => ReviewStatus.Submitted,
        Returned => ReviewStatus.Returned,
        Approved => ReviewStatus.Approved,
        _ => null,
    };
}

/// <summary>A check's severity (Severity): an error the store must explain, or a warning it only looks at.</summary>
public enum CheckSeverity
{
    Error,
    Warning,
}

/// <summary>The EMSO's decision on one day ("ok" | "query").</summary>
public enum DayVerdict
{
    Ok,
    Query,
}

/// <summary>The stored values of <see cref="DayVerdict"/> (roster_review_days.verdict).</summary>
public static class DayVerdictKeys
{
    public const string Ok = "ok", Query = "query";

    public static string Key(this DayVerdict verdict) => verdict == DayVerdict.Ok ? Ok : Query;

    public static DayVerdict? FromKey(string? key) => key switch
    {
        Ok => DayVerdict.Ok,
        Query => DayVerdict.Query,
        _ => null,
    };
}

/// <summary>
/// One check of the fortnight (ReviewCheckView): a flag the plan raised, with whether the store
/// has checked it and the note it gave.
/// </summary>
public sealed record ReviewCheckView
{
    /// <summary>Stable across a regenerate: the day plus the flag's own wording.</summary>
    public required string Key { get; init; }

    /// <summary>The day; null for a fortnight-level flag.</summary>
    public DateOnly? Date { get; init; }

    public required CheckSeverity Severity { get; init; }

    /// <summary>A legal breach a note can't clear (owner decision R3): fixed in the plan, or no submitting.</summary>
    public bool HardStop { get; init; }

    public required string Message { get; init; }

    /// <summary>The engine's "Why:" explanation, when it gave one.</summary>
    public string? Detail { get; init; }

    /// <summary>Checked by the store.</summary>
    public bool Done { get; init; }

    /// <summary>The store's note, once checked.</summary>
    public string? Note { get; init; }
}

/// <summary>One day of the fortnight under review (ReviewDayView).</summary>
public sealed record ReviewDayView
{
    public required DateOnly Date { get; init; }

    /// <summary>"Thu".</summary>
    public string Dow { get; init; } = "";

    /// <summary>"19 Nov".</summary>
    public string DateLabel { get; init; } = "";

    /// <summary>"Thursday 19 November".</summary>
    public string LongLabel { get; init; } = "";

    public bool Closed { get; init; }
    public bool HasData { get; init; }

    /// <summary>The sales forecast in dollars; null when there is none.</summary>
    public double? Forecast { get; init; }

    public double NonOp { get; init; }
    public double Benchmark { get; init; }
    public double Planned { get; init; }
    public int PlannedShifts { get; init; }

    /// <summary>The day's checks, errors first (the review's own order).</summary>
    public IReadOnlyList<ReviewCheckView> Checks { get; init; } = [];

    public DayVerdict? Verdict { get; init; }
    public string? Query { get; init; }
    public string? Reply { get; init; }

    /// <summary>A query answered before a resubmission, kept in the day's history.</summary>
    public string? PrevQuery { get; init; }

    public string? PrevReply { get; init; }
}

/// <summary>One line of the review's activity log (ReviewEventView).</summary>
/// <param name="At">Formatted in the stores' time, e.g. "Tue 10 Nov, 9:42 am".</param>
public sealed record ReviewEventView(string At, string? ActorName, string? ActorTier, string Kind, string Body);

/// <summary>The store's request to unlock its approved plan, while it waits (R5).</summary>
/// <param name="At">Formatted in the stores' time.</param>
public sealed record UnlockRequest(string? By, string At, string Reason);

/// <summary>The fortnight's plan review, read for one store and period (ReviewData).</summary>
public sealed record ReviewData
{
    public required Guid StoreId { get; init; }
    public required string StoreCode { get; init; }
    public required string StoreName { get; init; }
    public Guid? AreaId { get; init; }
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }

    /// <summary>"Mon 16 Nov – Sun 29 Nov 2026".</summary>
    public required string PeriodTitle { get; init; }

    /// <summary>"Wed 11 Nov, 6:00 pm".</summary>
    public required string DueLabel { get; init; }

    public bool HasSnapshot { get; init; }

    /// <summary>When the held plan was generated, formatted; null with no plan.</summary>
    public string? GeneratedAt { get; init; }

    /// <summary>What a submission and an approval are recorded against ("plan:3").</summary>
    public required string SnapshotKey { get; init; }

    public ReviewStatus Status { get; init; } = ReviewStatus.InProgress;

    /// <summary>A submission the plan has changed under since (regenerated or edited).</summary>
    public bool Stale { get; init; }

    public string? SubmittedAt { get; init; }
    public string? SubmittedBy { get; init; }
    public string? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? ReturnMessage { get; init; }
    public UnlockRequest? UnlockRequest { get; init; }
    public IReadOnlyList<ReviewDayView> Days { get; init; } = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ReviewEventView> Events { get; init; } = [];

    /// <summary>The store's area EMSOs, by name.</summary>
    public IReadOnlyList<string> ReviewerNames { get; init; } = [];

    /// <summary>No EMSO for the area: the super users review it.</summary>
    public bool ReviewerFallback { get; init; }

    /// <summary>Every check in the review (allChecks).</summary>
    public IEnumerable<ReviewCheckView> AllChecks() => Days.SelectMany(d => d.Checks);
}

/// <summary>Where one store of the reviewer's list is up to (QueueStore's status).</summary>
public enum QueueStatus
{
    Submitted,
    Returned,
    InProgress,
    NotStarted,
    Approved,
}

/// <summary>One store of the reviewer's list (QueueStore), with the link to its review.</summary>
/// <param name="When">When it was submitted, or decided; formatted.</param>
public sealed record QueueStore(Guid StoreId, string Code, string Name, QueueStatus Status, string? When, string Href);

/// <summary>A store in the Review page's store picker: "G101 · Northside".</summary>
public sealed record ReviewStoreOption(Guid Id, string Label);

/// <summary>Everything the Review page shows (what page.tsx reads before it renders).</summary>
public sealed record ReviewPageModel
{
    /// <summary>The fortnight on the page.</summary>
    public required DateOnly PeriodStart { get; init; }

    /// <summary>"Mon 23 Nov – Sun 6 Dec 2026", for the header when no store is chosen.</summary>
    public required string PeriodTitle { get; init; }

    /// <summary>The store on the page; null when the user sees none.</summary>
    public Guid? StoreId { get; init; }

    /// <summary>The stores the user sees, for the picker.</summary>
    public IReadOnlyList<ReviewStoreOption> Stores { get; init; } = [];

    /// <summary>Someone with no store of their own who sees more than one store.</summary>
    public bool ShowStorePicker { get; init; }

    /// <summary>The review; null when there is no store to show ("Choose a store to review.").</summary>
    public ReviewData? Data { get; init; }

    /// <summary>A reviewer's stores for the fortnight, with links (empty for anyone else).</summary>
    public IReadOnlyList<QueueStore> Queue { get; init; } = [];

    /// <summary>May act as the store: a store login or a super user, with write access to the store.</summary>
    public bool CanStore { get; init; }

    /// <summary>May act as the reviewer: an EMSO or a super user, with write access to the store.</summary>
    public bool CanReview { get; init; }

    /// <summary>May unlock an approved plan: the area's EMSO, a DSO or a super user (R5).</summary>
    public bool CanUnlock { get; init; }

    /// <summary>The user's unread notices that link to this review.</summary>
    public int UnreadNotices { get; init; }
}

/// <summary>What a review step returns (the actions' Result): done, or why not.</summary>
/// <param name="Note">Unchecking a flag hands its note back to edit.</param>
public sealed record ReviewResult(bool Ok, string? Error = null, string? Note = null)
{
    public static ReviewResult Success { get; } = new(true);

    public static ReviewResult Fail(string error) => new(false, error);
}
