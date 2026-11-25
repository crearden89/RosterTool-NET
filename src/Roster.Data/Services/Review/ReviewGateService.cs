// Port of src/lib/review/gate.ts (rosterGate, rosterChangeRefusal, planLocked,
// lapseReviewOnPlanChange).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Approval;
using Roster.Data.Snapshots;
using Roster.Engine.Core;

namespace Roster.Data.Services.Review;

public sealed class ReviewGateService(
    IRosterDbContextFactory contexts,
    IPlanSnapshotStore snapshots,
    IPlanApproval planApproval,
    IReviewNotices notices,
    TimeProvider clock) : IReviewGate
{
    public async Task<RosterGate> RosterGateAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var dates = Period.PeriodDatesFrom(periodStart);
        await using var db = contexts.Create();
        var row = await db.RosterReviews.AsNoTracking()
            .Where(r => r.StoreId == storeId && r.PeriodStart == periodStart)
            .Select(r => new { r.Status, r.SnapshotKey, r.DecidedAt })
            .FirstOrDefaultAsync(ct);
        var approved = await db.DayPlans.AsNoTracking()
            .Where(d => d.StoreId == storeId && dates.Contains(d.Date) && d.RosterStatus == DayStatuses.Approved)
            .Select(d => d.Date).ToListAsync(ct);
        // The held snapshots' versions and times are all the gate reads of them.
        var held = await snapshots.ReadAsync(storeId, periodStart, ct);
        return RosterGates.GateFrom(new RosterGateInput
        {
            ReviewStatus = row?.Status,
            ReviewKey = row?.SnapshotKey,
            PlanVersion = held.Plan?.Version,
            Dates = dates,
            ApprovedDates = approved.ToHashSet(),
            DecidedAt = row?.DecidedAt,
            RosterGeneratedAt = held.Roster?.GeneratedAt,
        });
    }

    // Why the roster can't be changed or published now, if it can't: its plan
    // isn't approved, or it was built from an earlier version of the plan.
    public async Task<string?> RosterChangeRefusalAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var gate = await RosterGateAsync(storeId, periodStart, ct);
        if (!gate.Unlocked) return RosterGates.RosterLocked;
        if (gate.OutOfDate) return RosterGates.RosterOutOfDate;
        return null;
    }

    public async Task<bool> PlanLockedAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) =>
        (await RosterGateAsync(storeId, periodStart, ct)).PlanApproved;

    // The plan changed after it was submitted or approved: what the EMSO saw is no
    // longer the plan, so the review starts again - back with the store, every day
    // for the EMSO to look at afresh, the plan approval withdrawn - and the EMSO
    // hears about it. A returned review is already with the store, so changing
    // the plan there is just the store doing its work.
    public async Task LapseReviewOnPlanChangeAsync(Guid storeId, DateOnly periodStart, ReviewActor actor, string what, CancellationToken ct = default)
    {
        await using var admin = contexts.CreateSystem("lapse a review whose plan changed");
        var was = await admin.RosterReviews.AsNoTracking()
            .Where(r => r.StoreId == storeId && r.PeriodStart == periodStart).Select(r => r.Status).FirstOrDefaultAsync(ct);
        if (was != ReviewStatus.Submitted && was != ReviewStatus.Approved) return;

        var now = clock.GetUtcNow();
        await admin.RosterReviews.Where(r => r.StoreId == storeId && r.PeriodStart == periodStart)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ReviewStatus.InProgress)
                .SetProperty(r => r.SnapshotKey, (string?)null)
                .SetProperty(r => r.DecidedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.DecidedBy, (Guid?)null)
                .SetProperty(r => r.UpdatedAt, now), ct);
        await admin.RosterReviewDays.Where(d => d.StoreId == storeId && d.PeriodStart == periodStart)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.Verdict, (string?)null)
                .SetProperty(d => d.Query, (string?)null)
                .SetProperty(d => d.Reply, (string?)null)
                .SetProperty(d => d.RepliedAt, (DateTimeOffset?)null), ct);
        if (was == ReviewStatus.Approved) await planApproval.WithdrawPlanDaysAsync(storeId, Period.PeriodDatesFrom(periodStart), actor.Id, ct);
        admin.RosterReviewEvents.Add(new RosterReviewEvent
        {
            Id = Guid.NewGuid(),
            StoreId = storeId,
            PeriodStart = periodStart,
            ActorId = actor.Id,
            ActorName = actor.Username,
            ActorTier = actor.Tier,
            Kind = "lapsed",
            Body = $"{what} after it was {was}, so it needs checking and submitting again.",
            CreatedAt = clock.GetUtcNow(),
        });
        await admin.SaveChangesAsync(ct);
        var store = await admin.Stores.AsNoTracking().Where(s => s.Id == storeId)
            .Select(s => new { s.Code, s.Name, s.AreaId }).FirstOrDefaultAsync(ct);
        await notices.NotifyReviewAsync(
            await notices.ReviewerIdsAsync(store?.AreaId, ct),
            actor.Id,
            ReviewNoticeKinds.Withdrawn,
            $"{store?.Name ?? "A store"} ({store?.Code ?? ""}) changed its plan for {ReviewFormat.PeriodTitle(periodStart)} after it was {was}. It will come back to you for review.",
            ReviewLinks.ReviewLink(storeId, periodStart),
            ct);
    }
}
