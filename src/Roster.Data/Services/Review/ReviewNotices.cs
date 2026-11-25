// Port of src/lib/review/notify.ts.

namespace Roster.Data.Services.Review;

// ─── Review notifications (the bell) ─────────────────────────────────────────
// A review moves between the store and its reviewers; each move tells the
// other side on the bell, linking straight to the review. One notice per
// person per review: a newer one replaces their unread older one, so the
// bell counts reviews waiting on you, not every click along the way.

public static class ReviewNoticeKinds
{
    public const string Submitted = "review_submitted";
    public const string Resubmitted = "review_resubmitted";
    public const string Withdrawn = "review_withdrawn";
    public const string Returned = "review_returned";
    public const string Approved = "review_approved";
    public const string Reopened = "review_reopened";
    public const string Reminder = "review_reminder";
    public const string UnlockRequested = "unlock_requested";
    public const string PlanUnlocked = "plan_unlocked";

    /// <summary>REVIEW_NOTICE_KINDS, in the TypeScript's order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Submitted, Resubmitted, Withdrawn, Returned, Approved, Reopened, Reminder, UnlockRequested, PlanUnlocked];
}

public static class ReviewLinks
{
    /// <summary>"/plan/review?store={id}&amp;period={yyyy-mm-dd}".</summary>
    public static string ReviewLink(Guid storeId, DateOnly periodStart) =>
        $"/plan/review?store={storeId:D}&period={periodStart:yyyy-MM-dd}";
}

/// <summary>Who hears about a review, and the bell notices themselves.</summary>
public interface IReviewNotices
{
    /// <summary>
    /// reviewerIds: who reviews a store's fortnight - its area's EMSOs, or, where
    /// the area has none set up yet, the super users, so a submission never goes unseen.
    /// </summary>
    Task<List<Guid>> ReviewerIdsAsync(Guid? areaId, CancellationToken ct = default);

    /// <summary>storeUserIds: the store's own logins.</summary>
    Task<List<Guid>> StoreUserIdsAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>
    /// notifyReview: one notice to each of <paramref name="userIds"/> except the
    /// actor, replacing their unread review notice for the same link. Never
    /// throws: a failed notice must not undo the review step it reports - the
    /// step is recorded, and the review page shows the state either way. The
    /// body is cut to 500 characters.
    /// </summary>
    Task NotifyReviewAsync(IEnumerable<Guid> userIds, Guid actorId, string kind, string body, string link, CancellationToken ct = default);
}
