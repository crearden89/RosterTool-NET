// Port of src/lib/review/server.ts (loadReview, loadReviewQueue).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;

namespace Roster.Data.Services.Review;

public sealed class ReviewReaderService(IRosterDbContextFactory contexts, IHeldPlans heldPlans) : IReviewReader
{
    public async Task<ReviewData?> LoadReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var dates = Period.PeriodDatesFrom(periodStart);
        await using var db = contexts.Create();
        var store = await db.Stores.AsNoTracking().Where(s => s.Id == storeId)
            .Select(s => new { s.Id, s.Code, s.Name, s.AreaId }).FirstOrDefaultAsync(ct);
        var plan = await heldPlans.ReadPlanAsync(storeId, periodStart, ct);
        var bench = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == storeId && dates.Contains(b.Date))
            .Select(b => new { b.Date, b.SalesForecast }).ToListAsync(ct);
        var row = await db.RosterReviews.AsNoTracking().Where(r => r.StoreId == storeId && r.PeriodStart == periodStart)
            .Select(r => new { r.Status, r.SnapshotKey, r.SubmittedAt, r.DecidedAt, r.ReturnMessage }).FirstOrDefaultAsync(ct);
        var checkRows = await db.RosterReviewChecks.AsNoTracking().Where(c => c.StoreId == storeId && c.PeriodStart == periodStart)
            .Select(c => new { c.CheckKey, c.Note }).ToListAsync(ct);
        var verdictRows = await db.RosterReviewDays.AsNoTracking().Where(v => v.StoreId == storeId && v.PeriodStart == periodStart)
            .Select(v => new { v.Date, v.Verdict, v.Query, v.Reply, v.PrevQuery, v.PrevReply }).ToListAsync(ct);
        var eventRows = await db.RosterReviewEvents.AsNoTracking().Where(e => e.StoreId == storeId && e.PeriodStart == periodStart)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(200)
            .Select(e => new { e.CreatedAt, e.ActorName, e.ActorTier, e.Kind, e.Body }).ToListAsync(ct);
        if (store is null) return null;

        // The area's EMSOs review; with none set up, the super users do. app_users
        // is self-read under the store scope, so other people's names need the system read.
        List<string> reviewerNames = [];
        if (store.AreaId is not null)
        {
            await using var admin = contexts.CreateSystem("loadReview: the names of the area's EMSOs");
            reviewerNames = await admin.AppUsers.AsNoTracking().Where(u => u.Tier == Tier.Emso && u.AreaId == store.AreaId)
                .Select(u => u.Username).ToListAsync(ct);
        }
        reviewerNames = Js.SortDefault(reviewerNames);

        var planByDate = new Dictionary<DateOnly, PlanSnapshotDay>();
        foreach (var d in plan?.Days ?? []) planByDate[d.Date] = d;
        var forecastByDate = new Dictionary<DateOnly, double>();
        foreach (var b in bench) forecastByDate[b.Date] = b.SalesForecast;
        var done = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var c in checkRows) done[c.CheckKey] = c.Note;
        var verdicts = verdictRows.GroupBy(v => v.Date).ToDictionary(g => g.Key, g => g.Last());
        ReviewCheckView View(ReviewCheck c) => ReviewCheckView.Of(c, done.ContainsKey(c.Key), done.GetValueOrDefault(c.Key));

        var days = dates.Select(date =>
        {
            var planDay = planByDate.GetValueOrDefault(date);
            // Every generated day is reviewed. `ok` is the day's "no flags" status, so
            // gating on it hid exactly the days a reviewer needs to see.
            var result = planDay?.Result;
            var dayPlan = planDay?.Plan;
            var closed = result?.Closed == true;
            var hasData = result?.HasData == true && dayPlan?.HasData == true;
            // One hours calculation for the status, the Plan page, the Review, the
            // Dashboard and Analysis (owner decision R2): planned hours against the
            // benchmark file.
            var hours = planDay is null
                ? new HeldDayFigures(0, 0, 0)
                : HeldDayHours.DayHours(new HeldPlanDay(BenchmarkParts.Of(planDay.Result), planDay.Plan));

            List<ReviewCheck> checks = [];
            if (hasData && !closed)
            {
                // The day checker's findings - the hours and every window included -
                // are already among the plan's flags.
                checks = ReviewChecks.SortChecks(ReviewChecks.FlagsToChecks(date, [.. result!.Flags, .. dayPlan!.Flags]));
            }
            var v = verdicts.GetValueOrDefault(date);
            return new ReviewDayView
            {
                Date = date,
                Dow = Period.DayLabels[Period.DowMon0(date)],
                DateLabel = ReviewFormat.DayMonth(date),
                LongLabel = ReviewFormat.LongDay(date),
                Closed = closed,
                HasData = hasData,
                Forecast = forecastByDate.TryGetValue(date, out var f) ? f : null,
                NonOp = result?.Allocations.FirstOrDefault(a => a.Group == "non_op")?.BenchmarkHours ?? 0,
                Benchmark = hours.Benchmark,
                Planned = hours.Planned,
                PlannedShifts = hours.Shifts,
                Checks = checks.Select(View).ToList(),
                Verdict = v?.Verdict,
                Query = v?.Query,
                Reply = v?.Reply,
                PrevQuery = v?.PrevQuery,
                PrevReply = v?.PrevReply,
            };
        }).ToList();

        // Submissions and approvals hold only for the plan they were made on. A
        // changed plan sends the review back round as it changes
        // (LapseReviewOnPlanChangeAsync); this is the same rule read at load time.
        var snapshotKey = ReviewFormat.PlanKey(plan?.Version);
        var stored = row?.Status ?? ReviewStatus.InProgress;
        var current = row?.SnapshotKey == snapshotKey;
        var status = stored == ReviewStatus.Approved && !current ? ReviewStatus.InProgress : stored;
        var stale = status == ReviewStatus.Submitted && !current;

        var events = eventRows.Select(e => new ReviewEventView
        {
            At = ReviewFormat.Stamp(e.CreatedAt),
            ActorName = e.ActorName,
            ActorTier = e.ActorTier,
            Kind = e.Kind,
            Body = e.Body,
        }).ToList();
        // The newest event of these kinds (the rows are newest first).
        string[] Kinds(params string[] k) => k;
        var lastBy = (string[] kinds) => eventRows.FirstOrDefault(e => kinds.Contains(e.Kind));
        // A request is waiting while it is the latest word on an approved plan.
        var lastLock = lastBy(Kinds("unlock_requested", "unlocked", "approved"));
        var unlockRequest = status == ReviewStatus.Approved && lastLock?.Kind == "unlock_requested"
            ? new UnlockRequest(lastLock.ActorName, ReviewFormat.Stamp(lastLock.CreatedAt), lastLock.Body)
            : null;

        return new ReviewData
        {
            StoreId = storeId,
            StoreCode = store.Code,
            StoreName = store.Name,
            AreaId = store.AreaId,
            PeriodStart = periodStart,
            PeriodEnd = Period.PeriodEndFrom(periodStart),
            PeriodTitle = ReviewFormat.PeriodTitle(periodStart),
            DueLabel = ReviewFormat.DueLabel(periodStart),
            HasSnapshot = plan is not null,
            GeneratedAt = plan is not null ? ReviewFormat.Stamp(plan.GeneratedAt) : null,
            SnapshotKey = snapshotKey,
            Status = status,
            Stale = stale,
            SubmittedAt = row?.SubmittedAt is { } sa ? ReviewFormat.Stamp(sa) : null,
            SubmittedBy = lastBy(Kinds("submitted", "resubmitted"))?.ActorName,
            DecidedAt = row?.DecidedAt is { } da ? ReviewFormat.Stamp(da) : null,
            DecidedBy = lastBy(Kinds("returned", "approved"))?.ActorName,
            ReturnMessage = row?.ReturnMessage,
            UnlockRequest = unlockRequest,
            Days = days,
            Events = events,
            ReviewerNames = reviewerNames,
            ReviewerFallback = reviewerNames.Count == 0,
        };
    }

    // An EMSO sees every store in their area; a super user (who reviews the
    // stores no EMSO covers) sees every store with a review under way.
    public async Task<List<QueueStore>> LoadReviewQueueAsync(ReviewQueueUser user, DateOnly periodStart, CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        var stores = new List<(Guid Id, string Code, string Name)>();
        if (user.Tier == Entities.Tiers.EmsoKey && user.AreaId is not null)
        {
            var rows = await db.Stores.AsNoTracking().Where(s => s.AreaId == user.AreaId).OrderBy(s => s.Code)
                .Select(s => new { s.Id, s.Code, s.Name }).ToListAsync(ct);
            stores = rows.Select(s => (s.Id, s.Code, s.Name)).ToList();
        }
        else if (user.Tier == Entities.Tiers.SuperUserKey)
        {
            var rows = await db.RosterReviews.AsNoTracking().Where(r => r.PeriodStart == periodStart)
                .Join(db.Stores, r => r.StoreId, s => s.Id, (r, s) => new { s.Id, s.Code, s.Name })
                .OrderBy(s => s.Code).Take(200).ToListAsync(ct);
            stores = Js.Sorted(rows.Select(s => (s.Id, s.Code, s.Name)), (a, b) => Js.LocaleCompare(a.Code, b.Code));
        }
        if (stores.Count == 0) return [];
        var ids = stores.Select(s => s.Id).ToList();
        var reviews = await db.RosterReviews.AsNoTracking().Where(r => r.PeriodStart == periodStart && ids.Contains(r.StoreId))
            .Select(r => new { r.StoreId, r.Status, r.SubmittedAt, r.DecidedAt }).ToListAsync(ct);
        var byStore = reviews.GroupBy(r => r.StoreId).ToDictionary(g => g.Key, g => g.Last());
        return stores.Select(s =>
        {
            var r = byStore.GetValueOrDefault(s.Id);
            var status = r?.Status ?? ReviewStatus.NotStarted;
            var ts = status == ReviewStatus.Submitted ? r?.SubmittedAt
                : status is ReviewStatus.Returned or ReviewStatus.Approved ? r?.DecidedAt
                : null;
            return new QueueStore(s.Id, s.Code, s.Name, status, ts is { } t ? ReviewFormat.Stamp(t) : null);
        }).ToList();
    }
}
