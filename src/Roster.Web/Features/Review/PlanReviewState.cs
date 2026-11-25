// Port of the PlanReviewState type in src/app/(app)/plan/review/PlanReviewStatus.tsx
// (the state function is PlanReviewStatus.PlanReviewState, beside the component).

namespace Roster.Web.Features.Review;

/// <summary>Where a fortnight's plan review is up to, as the Plan and Roster pages show it.</summary>
public enum PlanReviewState
{
    /// <summary>"not_submitted": no submission, or an approval the plan has changed under since.</summary>
    NotSubmitted,

    /// <summary>"submitted": with the EMSO.</summary>
    Submitted,

    /// <summary>"returned": queried by the EMSO; the store has to act.</summary>
    Returned,

    /// <summary>"approved": approved on the plan as it stands, and so locked (R5).</summary>
    Approved,
}
