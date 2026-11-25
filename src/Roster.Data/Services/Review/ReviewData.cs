// Port of src/lib/review/server.ts (the review's view types and its reads).

using Roster.Engine.Planning;

namespace Roster.Data.Services.Review;

// ─── The fortnight's plan review, read for one store and period ──────────────
// Plan -> Review -> Roster: the store checks the plan's flags and submits it,
// the EMSO approves it, and only then can the roster be built (from this very
// plan). Everything here comes from the HELD plan snapshot (what the Plan page
// shows - never a fresh solve) plus the review's own rows. Shared by the page
// and by the server actions, which re-read it to validate every step against
// the plan as it stands.

/// <summary>"in_progress", "submitted", "returned" or "approved" (the queue adds "not_started").</summary>
public static class ReviewStatus
{
    public const string InProgress = "in_progress", Submitted = "submitted", Returned = "returned", Approved = "approved";
    public const string NotStarted = "not_started";
}

/// <summary>
/// A review check (lib/review/checks ReviewCheck) with whether the store has
/// checked it and its note.
/// </summary>
public sealed record ReviewCheckView
{
    // Stable across a regenerate: the day plus the flag's own wording, so the
    // same flag keeps its check and a changed one (new hours, new times) needs
    // checking again.
    public string Key { get; init; } = "";
    /// <summary>Null = a fortnight-level flag (e.g. under contract).</summary>
    public DateOnly? Date { get; init; }
    public Severity Severity { get; init; } = Severity.Warning;
    // A legal breach a note can't clear (owner decision R3): fixed by an edit
    // or a re-plan, or the plan can't be submitted.
    public bool HardStop { get; init; }
    public string Message { get; init; } = "";
    /// <summary>The engine's "Why:" explanation, when it gave one.</summary>
    public string? Detail { get; init; }
    public bool Done { get; init; }
    public string? Note { get; init; }

    /// <summary>The check with the store's tick and note.</summary>
    public static ReviewCheckView Of(ReviewCheck c, bool done, string? note) => new()
    {
        Key = c.Key,
        Date = c.Date,
        Severity = c.Severity,
        HardStop = c.HardStop,
        Message = c.Message,
        Detail = c.Detail,
        Done = done,
        Note = note,
    };
}

public sealed record ReviewDayView
{
    public DateOnly Date { get; init; }
    /// <summary>"Thu".</summary>
    public string Dow { get; init; } = "";
    /// <summary>"19 Nov".</summary>
    public string DateLabel { get; init; } = "";
    /// <summary>"Thursday 19 November".</summary>
    public string LongLabel { get; init; } = "";
    public bool Closed { get; init; }
    public bool HasData { get; init; }
    /// <summary>The day's sales forecast ($), null with no benchmark row.</summary>
    public double? Forecast { get; init; }
    public double NonOp { get; init; }
    public double Benchmark { get; init; }
    public double Planned { get; init; }
    public double PlannedShifts { get; init; }
    public IReadOnlyList<ReviewCheckView> Checks { get; init; } = [];
    /// <summary>"ok", "query" or null.</summary>
    public string? Verdict { get; init; }
    public string? Query { get; init; }
    public string? Reply { get; init; }
    public string? PrevQuery { get; init; }
    public string? PrevReply { get; init; }
}

public sealed record ReviewEventView
{
    /// <summary>Formatted, store time (ReviewFormat.Stamp).</summary>
    public string At { get; init; } = "";
    public string? ActorName { get; init; }
    public string? ActorTier { get; init; }
    public string Kind { get; init; } = "";
    public string Body { get; init; } = "";
}

/// <summary>The store's request to unlock the approved plan, while it waits (R5).</summary>
public sealed record UnlockRequest(string? By, string At, string Reason);

public sealed record ReviewData
{
    public Guid StoreId { get; init; }
    public string StoreCode { get; init; } = "";
    public string StoreName { get; init; } = "";
    public Guid? AreaId { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    /// <summary>"Mon 16 Nov – Sun 29 Nov 2026".</summary>
    public string PeriodTitle { get; init; } = "";
    /// <summary>"Wed 11 Nov, 6:00 pm".</summary>
    public string DueLabel { get; init; } = "";
    public bool HasSnapshot { get; init; }
    /// <summary>When the held plan was generated, formatted (ReviewFormat.Stamp).</summary>
    public string? GeneratedAt { get; init; }
    public string SnapshotKey { get; init; } = "";
    public string Status { get; init; } = ReviewStatus.InProgress;
    // A submission the plan has changed under since (regenerated or edited):
    // the EMSO would be reviewing something the store no longer has.
    public bool Stale { get; init; }
    public string? SubmittedAt { get; init; }
    public string? SubmittedBy { get; init; }
    public string? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? ReturnMessage { get; init; }
    public UnlockRequest? UnlockRequest { get; init; }
    public IReadOnlyList<ReviewDayView> Days { get; init; } = [];
    public IReadOnlyList<ReviewEventView> Events { get; init; } = [];
    /// <summary>The store's area EMSOs, sorted.</summary>
    public IReadOnlyList<string> ReviewerNames { get; init; } = [];
    /// <summary>No EMSO for the area: super users review it.</summary>
    public bool ReviewerFallback { get; init; }

    /// <summary>allChecks: every check in the review.</summary>
    public IEnumerable<ReviewCheckView> AllChecks() => Days.SelectMany(d => d.Checks);
}

/// <summary>A store on the reviewer's list.</summary>
public sealed record QueueStore(Guid StoreId, string Code, string Name, string Status, string? When);

/// <summary>Who is asking for the reviewer's store list: their tier key ("emso", "super_user", ...) and area.</summary>
public sealed record ReviewQueueUser(string Tier, Guid? AreaId);

public interface IReviewReader
{
    /// <summary>loadReview: the review for one store and fortnight; null when the store is not visible.</summary>
    Task<ReviewData?> LoadReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>
    /// loadReviewQueue: an EMSO sees every store in their area; a super user (who
    /// reviews the stores no EMSO covers) sees every store with a review under way.
    /// Everyone else gets an empty list.
    /// </summary>
    Task<List<QueueStore>> LoadReviewQueueAsync(ReviewQueueUser user, DateOnly periodStart, CancellationToken ct = default);
}
