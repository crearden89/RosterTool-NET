namespace Roster.Data.Entities;

// ─── Fortnight roster review (0045) ──────────────────────────────────────────
// The store works through every flag and submits; the EMSO marks each day ok or
// queries it, then approves or returns. "Approved" stays day_plans.roster_status.

public static class ReviewStatuses
{
    public const string InProgress = "in_progress", Submitted = "submitted", Returned = "returned", Approved = "approved";
    public static readonly string[] All = [InProgress, Submitted, Returned, Approved];
}

public class RosterReview
{
    public Guid StoreId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public string Status { get; set; } = ReviewStatuses.InProgress;
    /// <summary>plan:roster snapshot versions at submission; a regenerate makes it stale.</summary>
    public string? SnapshotKey { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    /// <summary>At most 2,000 characters.</summary>
    public string? ReturnMessage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A flag the store has checked; the key is derived from the day and the flag's wording.</summary>
public class RosterReviewCheck
{
    public Guid StoreId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public string CheckKey { get; set; } = "";
    /// <summary>At most 1,000 characters.</summary>
    public string? Note { get; set; }
    public Guid? CheckedBy { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}

public static class ReviewVerdicts
{
    public const string Ok = "ok", Query = "query";
}

/// <summary>The EMSO's decision on one day, the query and the store's reply.</summary>
public class RosterReviewDay
{
    public Guid StoreId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly Date { get; set; }
    public string? Verdict { get; set; }
    public string? Query { get; set; }
    public string? Reply { get; set; }
    public DateTimeOffset? RepliedAt { get; set; }
    public string? PrevQuery { get; set; }
    public string? PrevReply { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>The review's activity log, in order.</summary>
public class RosterReviewEvent
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? ActorTier { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>At most 3,000 characters.</summary>
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
