// New: the Review page's service - what src/app/(app)/plan/review/page.tsx reads and the
// server actions in actions.ts do, as one interface the page calls. Every step works for the
// signed-in user and makes the TypeScript app's checks, in the same order, with the same
// words: the store prepares and submits, the EMSO decides, and the EMSO or above unlocks.

namespace Roster.Web.Features.Review;

public interface IReviewService
{
    /// <summary>
    /// page.tsx: the fortnight's review for the store the user asked for (or, for a reviewer
    /// opening Review from the menu, a store waiting for them), the reviewer's list of stores,
    /// what the user may do, and their unread notices for the review.
    /// </summary>
    /// <param name="requestedStore">The ?store= the page was opened with, if any.</param>
    /// <param name="periodStart">The fortnight's Monday (the page resolves ?period=).</param>
    Task<ReviewPageModel> LoadPageAsync(Guid? requestedStore, DateOnly periodStart, CancellationToken cancellationToken = default);

    // ── The store's side ──

    /// <summary>checkFlag: the store checks a flag. An error needs a note; a hard stop can't be checked.</summary>
    Task<ReviewResult> CheckFlagAsync(Guid storeId, DateOnly periodStart, string key, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>uncheckFlag: takes a check back; the result carries the note it had.</summary>
    Task<ReviewResult> UncheckFlagAsync(Guid storeId, DateOnly periodStart, string key, CancellationToken cancellationToken = default);

    /// <summary>replyToQuery: the store answers the EMSO's query on a day of a returned plan.</summary>
    Task<ReviewResult> ReplyToQueryAsync(Guid storeId, DateOnly periodStart, DateOnly date, string text, CancellationToken cancellationToken = default);

    /// <summary>submitReview: every flag checked (and every query answered), the plan goes to the EMSO.</summary>
    Task<ReviewResult> SubmitReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>withdrawReview: the store takes a submission back to make changes.</summary>
    Task<ReviewResult> WithdrawReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    // ── The EMSO's side ──

    /// <summary>setDayVerdict: the EMSO marks a day fine, or queries it saying what to look at.</summary>
    Task<ReviewResult> SetDayVerdictAsync(Guid storeId, DateOnly periodStart, DateOnly date, DayVerdict verdict, string? query = null, CancellationToken cancellationToken = default);

    /// <summary>markUnflaggedFine: every day with nothing flagged and no decision yet, marked fine.</summary>
    Task<ReviewResult> MarkUnflaggedFineAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>returnReview: the EMSO sends the plan back with a message (at least one day queried).</summary>
    Task<ReviewResult> ReturnReviewAsync(Guid storeId, DateOnly periodStart, string message, CancellationToken cancellationToken = default);

    /// <summary>approveReview: every day fine, the plan is approved (every day of it) and the roster unlocked.</summary>
    Task<ReviewResult> ApproveReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    // ── Unlocking an approved plan (owner decision R5) ──

    /// <summary>requestUnlock: the store asks for its approved plan to be unlocked, saying why.</summary>
    Task<ReviewResult> RequestUnlockAsync(Guid storeId, DateOnly periodStart, string reason, CancellationToken cancellationToken = default);

    /// <summary>unlockReview: the EMSO, a DSO or a super user unlocks it, saying why; it goes back to the store.</summary>
    Task<ReviewResult> UnlockReviewAsync(Guid storeId, DateOnly periodStart, string reason, CancellationToken cancellationToken = default);

    /// <summary>sendReminder: the EMSO reminds the store to check and submit its plan.</summary>
    Task<ReviewResult> SendReminderAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    // ── The bell ──

    /// <summary>markReviewNoticesRead: opening a review clears the user's unread notices for it.</summary>
    Task MarkReviewNoticesReadAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);
}
