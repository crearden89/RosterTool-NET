// Port of the public surface of src/lib/plan/approval.ts and src/lib/roster/approval.ts.

namespace Roster.Data.Services.Approval;

/// <summary>The outcome of an approval write: ok, or the database's error.</summary>
public sealed record ApprovalResult(bool Ok, string? Error = null)
{
    public static readonly ApprovalResult Success = new(true);
}

/// <summary>approvePlanDays's outcome: also how many days it approved.</summary>
public sealed record PlanApprovalResult(bool Ok, int Approved, string? Error = null);

/// <summary>
/// Approving and withdrawing a plan, day by day - the work behind the EMSO's
/// approval on the review page (every day of the fortnight at once) and the
/// Plan page's single-day actions. Callers authorise first and refresh the
/// pages after; these only do the writes.
/// </summary>
public interface IPlanApproval
{
    /// <summary>
    /// approvePlanDays: approve the HELD plan (what the store and EMSO looked at)
    /// for each day: persist its Stage 1 planned_blocks and mark the day's plan
    /// approved. A day with nothing to plan (closed, no data) is skipped, not failed.
    /// </summary>
    Task<PlanApprovalResult> ApprovePlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// withdrawPlanDays: clear the persisted planned_blocks and reset each day's
    /// plan status so it no longer counts as approved.
    /// </summary>
    Task<ApprovalResult> WithdrawPlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Publishing and withdrawing a fortnight's roster - the work behind the
/// Roster page's Approve/Withdraw and the EMSO's decision on the review page.
/// Callers authorise first (the store write check and their own role rules)
/// and refresh the pages after; these only do the writes.
/// </summary>
public interface IRosterApproval
{
    /// <summary>
    /// publishRoster: persist the HELD roster's named shifts and task tiles for
    /// every day given (approve_roster_day, one transaction per day) and mark
    /// each day's roster approved - what the Floor Display reads. A day with an
    /// open shift or a cover gap needs <paramref name="comment"/>. Closed days are
    /// signed off with the rest; days with no roster are skipped.
    /// </summary>
    Task<ApprovalResult> PublishRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, string? comment = null, CancellationToken ct = default);

    /// <summary>
    /// unpublishRoster: remove the persisted shifts (so the Floor Display stops
    /// showing them) and reset each day's roster status.
    /// </summary>
    Task<ApprovalResult> UnpublishRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default);
}
