// Port of src/app/(app)/plan/review/actions.ts, with the loading half of page.tsx
// (LoadPageAsync).
// ─── Fortnight plan review: the store's and the EMSO's steps ─────────────────
// Plan -> Review -> Roster. Every step re-reads the review from the HELD plan
// before it writes, so a check, a submission or an approval is always judged
// against the plan the store has now; the EMSO's approval approves that plan
// and unlocks the roster, which is then built from it. Writes go through the
// system context (the TypeScript app's service role) behind DenyStoreWriteAsync
// (the same guard as every roster change) plus the role rule for the step: the
// store prepares and submits, the EMSO decides. A super user may do either - they
// stand in for any area without an EMSO.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Approval;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Db = Roster.Data.Services.Review;

namespace Roster.Web.Features.Review;

public sealed class ReviewService(
    IStoreScope scope,
    IStoreDirectory directory,
    IRememberedStore remembered,
    Db.IReviewReader reader,
    Db.IReviewNotices notices,
    IPlanApproval planApproval,
    IRosterDbContextFactory contexts,
    TimeProvider clock) : IReviewService
{
    private static readonly Tier[] StoreTiers = [Tier.Store, Tier.SuperUser];
    private static readonly Tier[] ReviewerTiers = [Tier.Emso, Tier.SuperUser];

    private const string StoreLocked = "This plan is with the EMSO. Withdraw it first to make changes.";
    private const string HardStop =
        "This is a hard stop: someone alone, a junior without three adults, or no till while trading. Fix it in the plan - an edit or a re-plan. A note can't clear it.";
    private const string NotForReview = "There's nothing to decide: the store hasn't submitted this plan (or changed it since).";

    private sealed record Ctx(ScopedUser User, Db.ReviewData Data);

    // ─── The page (page.tsx) ─────────────────────────────────────────────────

    public async Task<ReviewPageModel> LoadPageAsync(Guid? requestedStore, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        var user = scope.User;
        // The stores the user sees (the stores table under row-level security), by code.
        var readable = await scope.ReadableStoreIdsAsync(cancellationToken);
        var stores = (await directory.ListAsync(cancellationToken)).Where(s => readable is null || readable.Contains(s.Id)).ToList();
        if (user is null)
            return new ReviewPageModel { PeriodStart = periodStart, PeriodTitle = Db.ReviewFormat.PeriodTitle(periodStart) };

        var isReviewer = user.Tier is Tier.Emso or Tier.SuperUser;
        var queue = isReviewer
            ? await reader.LoadReviewQueueAsync(new Db.ReviewQueueUser(user.Tier.Key(), user.AreaId), periodStart, cancellationToken)
            : [];
        // A reviewer opening Review from the menu lands on a store that's waiting
        // for them, not whichever store happens to come first.
        var waiting = requestedStore is null && user.StoreId is null
            ? queue.FirstOrDefault(q => q.Status == Db.ReviewStatus.Submitted) ?? queue.FirstOrDefault()
            : null;
        var storeId = user.StoreId ?? waiting?.StoreId ?? StoreScope.ResolveStoreId(requestedStore, [.. stores.Select(s => s.Id)], remembered.StoreId);

        Db.ReviewData? data = null;
        var unread = 0;
        if (storeId is { } sid)
        {
            data = await reader.LoadReviewAsync(sid, periodStart, cancellationToken);
            var link = Db.ReviewLinks.ReviewLink(sid, periodStart);
            await using var db = contexts.Create();
            unread = await db.Notifications.AsNoTracking().CountAsync(n => n.Link == link && n.ReadAt == null, cancellationToken);
        }

        var storeRow = stores.FirstOrDefault(s => s.Id == storeId);
        var access = storeRow is null ? StoreAccess.Read : AccessMatrix.StoreAccessFor(user, storeRow.Id, storeRow.AreaId);
        var canWrite = access == StoreAccess.Write;
        return new ReviewPageModel
        {
            PeriodStart = periodStart,
            PeriodTitle = Db.ReviewFormat.PeriodTitle(periodStart),
            StoreId = storeId,
            Stores = [.. stores.Select(s => new ReviewStoreOption(s.Id, $"{s.Code} · {s.Name}"))],
            ShowStorePicker = user.StoreId is null && stores.Count > 1,
            Data = data is null ? null : ToView(data),
            Queue = [.. queue.Select(q => new QueueStore(q.StoreId, q.Code, q.Name, QueueStatusOf(q.Status), q.When,
                $"/plan/review?store={q.StoreId}&period={ReviewQuery.Iso(periodStart)}"))],
            CanStore = canWrite && user.Tier is Tier.Store or Tier.SuperUser,
            CanReview = canWrite && isReviewer,
            // Unlocking an approved plan (R5): the area's EMSO or a super user,
            // and a DSO - whose login is otherwise read-only.
            CanUnlock = (canWrite && isReviewer) || user.Tier == Tier.Director,
            UnreadNotices = unread,
        };
    }

    // ─── The steps' common start ─────────────────────────────────────────────

    private async Task<(Ctx? Ctx, string? Error)> Begin(Guid storeId, DateOnly periodStart, bool storeSide, bool needRoster, CancellationToken ct)
    {
        if (!Period.IsPeriodStart(periodStart)) return (null, "That isn't the start of a fortnight.");
        var denied = await scope.DenyStoreWriteAsync(storeId, ct);
        if (denied is not null) return (null, denied);
        if (scope.User is not { } user) return (null, ScopeMessages.DeniedAuth);
        if (!(storeSide ? StoreTiers : ReviewerTiers).Contains(user.Tier))
            return (null, storeSide ? "Only the store can do this." : "Only the store's EMSO can do this.");
        var data = await reader.LoadReviewAsync(storeId, periodStart, ct);
        if (data is null) return (null, "That store isn't available to you.");
        if (needRoster && !data.HasSnapshot) return (null, "Generate this fortnight's plan first.");
        return (new Ctx(user, data), null);
    }

    // The store works on the review until it's submitted - and again once it's
    // returned, or when the plan changed under a submission.
    private static bool StoreCanEdit(Db.ReviewData d) =>
        d.Status is Db.ReviewStatus.InProgress or Db.ReviewStatus.Returned || (d.Status == Db.ReviewStatus.Submitted && d.Stale);

    private static bool ReviewerCanDecide(Db.ReviewData d) => d.Status == Db.ReviewStatus.Submitted && !d.Stale;

    private static string DayLabel(Db.ReviewData d, DateOnly? date)
    {
        if (date is null) return "Fortnight";
        var day = d.Days.FirstOrDefault(x => x.Date == date);
        return day is null ? ReviewQuery.Iso(date.Value) : $"{day.Dow} {day.DateLabel}";
    }

    private static string Clean(string? s, int max)
    {
        var t = Js.Trim(s ?? "");
        return t.Length > max ? t[..max] : t;
    }

    private static string StoreLabel(Db.ReviewData d) => $"{d.StoreName} ({d.StoreCode})";

    private static string Link(Db.ReviewData d) => Db.ReviewLinks.ReviewLink(d.StoreId, d.PeriodStart);

    private RosterDbContext Admin(string why) => contexts.CreateSystem($"review: {why}");

    // The review row: inserted when missing, these fields set, updated_at stamped.
    private async Task<string?> WriteReview(Ctx ctx, Action<RosterReview> fields, CancellationToken ct)
    {
        try
        {
            await using var admin = Admin("the review row");
            var row = await admin.RosterReviews.FirstOrDefaultAsync(r => r.StoreId == ctx.Data.StoreId && r.PeriodStart == ctx.Data.PeriodStart, ct);
            if (row is null)
            {
                row = new RosterReview { StoreId = ctx.Data.StoreId, PeriodStart = ctx.Data.PeriodStart };
                admin.RosterReviews.Add(row);
            }
            fields(row);
            row.UpdatedAt = clock.GetUtcNow();
            await admin.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException e)
        {
            return e.InnerException?.Message ?? e.Message;
        }
    }

    private async Task LogEvent(Ctx ctx, string kind, string body, CancellationToken ct)
    {
        await using var admin = Admin("the activity log");
        admin.RosterReviewEvents.Add(new RosterReviewEvent
        {
            Id = Guid.NewGuid(),
            StoreId = ctx.Data.StoreId,
            PeriodStart = ctx.Data.PeriodStart,
            ActorId = ctx.User.Id,
            ActorName = ctx.User.Username,
            ActorTier = ctx.User.Tier.Key(),
            Kind = kind,
            Body = body.Length > 3000 ? body[..3000] : body,
            CreatedAt = clock.GetUtcNow(),
        });
        await admin.SaveChangesAsync(ct);
    }

    // The fortnight's day rows: each change applied to the rows that exist (an update).
    private async Task<string?> UpdateDays(Db.ReviewData d, Func<RosterReviewDay, bool> which, Action<RosterReviewDay> change, CancellationToken ct)
    {
        try
        {
            await using var admin = Admin("the day decisions");
            var rows = await admin.RosterReviewDays.Where(r => r.StoreId == d.StoreId && r.PeriodStart == d.PeriodStart).ToListAsync(ct);
            foreach (var r in rows.Where(which)) change(r);
            await admin.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException e)
        {
            return e.InnerException?.Message ?? e.Message;
        }
    }

    // One day's row: inserted when missing (an upsert on store, fortnight and date).
    private async Task<string?> UpsertDays(Db.ReviewData d, IEnumerable<DateOnly> dates, Action<RosterReviewDay> fields, CancellationToken ct)
    {
        try
        {
            await using var admin = Admin("the day decisions");
            var wanted = dates.ToList();
            var rows = await admin.RosterReviewDays.Where(r => r.StoreId == d.StoreId && r.PeriodStart == d.PeriodStart).ToListAsync(ct);
            foreach (var date in wanted)
            {
                var row = rows.FirstOrDefault(r => r.Date == date);
                if (row is null)
                {
                    row = new RosterReviewDay { StoreId = d.StoreId, PeriodStart = d.PeriodStart, Date = date };
                    admin.RosterReviewDays.Add(row);
                }
                fields(row);
            }
            await admin.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException e)
        {
            return e.InnerException?.Message ?? e.Message;
        }
    }

    // ─── The store's side ────────────────────────────────────────────────────

    public async Task<ReviewResult> CheckFlagAsync(Guid storeId, DateOnly periodStart, string key, string? note = null, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, true, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (!StoreCanEdit(ctx.Data)) return ReviewResult.Fail(StoreLocked);
        var check = ctx.Data.AllChecks().FirstOrDefault(c => c.Key == key);
        if (check is null) return ReviewResult.Fail("That flag is no longer on the plan. Refresh the page.");
        // A hard stop is fixed, not explained (owner decision R3).
        if (check.HardStop) return ReviewResult.Fail(HardStop);
        var text = Clean(note, 1000);
        if (check.Severity == Severity.Error && text.Length == 0) return ReviewResult.Fail("An error needs a note saying why it's OK.");

        try
        {
            await using var admin = Admin("a checked flag");
            var row = await admin.RosterReviewChecks.FirstOrDefaultAsync(
                c => c.StoreId == storeId && c.PeriodStart == periodStart && c.CheckKey == key, cancellationToken);
            if (row is null)
            {
                row = new RosterReviewCheck { StoreId = storeId, PeriodStart = periodStart, CheckKey = key };
                admin.RosterReviewChecks.Add(row);
            }
            row.Note = text.Length > 0 ? text : null;
            row.CheckedBy = ctx.User.Id;
            row.CheckedAt = clock.GetUtcNow();
            await admin.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e)
        {
            return ReviewResult.Fail(e.InnerException?.Message ?? e.Message);
        }
        await LogEvent(ctx, "checked", $"Checked {DayLabel(ctx.Data, check.Date)}: {check.Message}{(text.Length > 0 ? $". Note: {text}" : "")}", cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> UncheckFlagAsync(Guid storeId, DateOnly periodStart, string key, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, true, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (!StoreCanEdit(ctx.Data)) return ReviewResult.Fail(StoreLocked);
        var was = ctx.Data.AllChecks().FirstOrDefault(c => c.Key == key);
        try
        {
            await using var admin = Admin("an unchecked flag");
            var rows = await admin.RosterReviewChecks
                .Where(c => c.StoreId == storeId && c.PeriodStart == periodStart && c.CheckKey == key).ToListAsync(cancellationToken);
            admin.RosterReviewChecks.RemoveRange(rows);
            await admin.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e)
        {
            return ReviewResult.Fail(e.InnerException?.Message ?? e.Message);
        }
        return new ReviewResult(true, Note: was?.Note);
    }

    public async Task<ReviewResult> ReplyToQueryAsync(Guid storeId, DateOnly periodStart, DateOnly date, string text, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, true, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (ctx.Data.Status != Db.ReviewStatus.Returned) return ReviewResult.Fail("There's no query to answer.");
        var day = ctx.Data.Days.FirstOrDefault(d => d.Date == date);
        if (day is null || day.Verdict != ReviewVerdicts.Query) return ReviewResult.Fail("That day has no query.");
        var reply = Clean(text, 1000);
        if (reply.Length == 0) return ReviewResult.Fail("Write a reply first.");
        var now = clock.GetUtcNow();
        var err = await UpdateDays(ctx.Data, r => r.Date == date, r =>
        {
            r.Reply = reply;
            r.RepliedAt = now;
        }, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx, "replied", $"Replied on {DayLabel(ctx.Data, date)}: {reply}", cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> SubmitReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, true, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        var d = ctx.Data;
        if (!StoreCanEdit(d)) return ReviewResult.Fail(d.Status == Db.ReviewStatus.Approved ? "This fortnight is already approved." : StoreLocked);
        var hard = d.AllChecks().Count(c => c.HardStop);
        if (hard > 0) return ReviewResult.Fail($"Fix the hard stops in the plan first - {hard} to go. A note can't clear them.");
        var open = d.AllChecks().Count(c => !c.Done);
        if (open > 0) return ReviewResult.Fail($"Check every flag first - {open} still to go.");
        var queried = d.Days.Where(x => x.Verdict == ReviewVerdicts.Query).ToList();
        var resubmit = d.Status == Db.ReviewStatus.Returned;
        if (resubmit && queried.Any(x => string.IsNullOrEmpty(x.Reply))) return ReviewResult.Fail("Answer every query first.");

        // A fresh submission is reviewed afresh. A resubmission keeps the days the
        // EMSO already passed; each answered query moves into the day's history
        // and the day goes back to them.
        if (resubmit)
        {
            foreach (var x in queried)
            {
                var err = await UpdateDays(d, r => r.Date == x.Date, r =>
                {
                    (r.Verdict, r.Query, r.Reply, r.RepliedAt) = (null, null, null, null);
                    (r.PrevQuery, r.PrevReply) = (x.Query, x.Reply);
                }, cancellationToken);
                if (err is not null) return ReviewResult.Fail(err);
            }
        }
        else
        {
            var err = await UpdateDays(d, _ => true, r => (r.Verdict, r.Query, r.Reply, r.RepliedAt) = (null, null, null, null), cancellationToken);
            if (err is not null) return ReviewResult.Fail(err);
        }
        var now = clock.GetUtcNow();
        var written = await WriteReview(ctx, r =>
        {
            r.Status = ReviewStatuses.Submitted;
            r.SnapshotKey = d.SnapshotKey;
            r.SubmittedAt = now;
            r.SubmittedBy = ctx.User.Id;
            r.ReturnMessage = null;
        }, cancellationToken);
        if (written is not null) return ReviewResult.Fail(written);

        var to = d.ReviewerFallback ? "the super users (no EMSO is set up for this area)" : string.Join(", ", d.ReviewerNames);
        await LogEvent(ctx, resubmit ? "resubmitted" : "submitted", $"{(resubmit ? "Resubmitted" : "Submitted")} to {to} for review.", cancellationToken);
        await notices.NotifyReviewAsync(
            await notices.ReviewerIdsAsync(d.AreaId, cancellationToken),
            ctx.User.Id,
            resubmit ? Db.ReviewNoticeKinds.Resubmitted : Db.ReviewNoticeKinds.Submitted,
            resubmit
                ? $"{StoreLabel(d)} answered your query and resubmitted its plan for {d.PeriodTitle}."
                : $"{StoreLabel(d)} submitted its plan for {d.PeriodTitle}. It's ready for your review.",
            Link(d),
            cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> WithdrawReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, true, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (ctx.Data.Status != Db.ReviewStatus.Submitted) return ReviewResult.Fail("This fortnight isn't with the EMSO.");
        var err = await WriteReview(ctx, r => r.Status = ReviewStatuses.InProgress, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx, "withdrawn", "Withdrew the submission to make changes.", cancellationToken);
        await notices.NotifyReviewAsync(
            await notices.ReviewerIdsAsync(ctx.Data.AreaId, cancellationToken),
            ctx.User.Id,
            Db.ReviewNoticeKinds.Withdrawn,
            $"{StoreLabel(ctx.Data)} withdrew its plan for {ctx.Data.PeriodTitle} to make changes. Nothing to review until it's resubmitted.",
            Link(ctx.Data),
            cancellationToken);
        return ReviewResult.Success;
    }

    // ─── The EMSO's side ─────────────────────────────────────────────────────

    public async Task<ReviewResult> SetDayVerdictAsync(Guid storeId, DateOnly periodStart, DateOnly date, DayVerdict verdict, string? query = null, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, false, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (!ReviewerCanDecide(ctx.Data)) return ReviewResult.Fail(NotForReview);
        if (ctx.Data.Days.All(d => d.Date != date)) return ReviewResult.Fail("That day isn't in this fortnight.");
        var text = Clean(query, 1000);
        if (verdict == DayVerdict.Query && text.Length == 0) return ReviewResult.Fail("Say what the store should look at.");
        var now = clock.GetUtcNow();
        var err = await UpsertDays(ctx.Data, [date], r =>
        {
            r.Verdict = verdict.Key();
            r.Query = verdict == DayVerdict.Query ? text : null;
            (r.Reply, r.RepliedAt) = (null, null);
            (r.DecidedBy, r.DecidedAt) = (ctx.User.Id, now);
        }, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx,
            verdict == DayVerdict.Ok ? "day_ok" : "day_query",
            verdict == DayVerdict.Ok ? $"Marked {DayLabel(ctx.Data, date)} fine." : $"Queried {DayLabel(ctx.Data, date)}: {text}",
            cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> MarkUnflaggedFineAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, false, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (!ReviewerCanDecide(ctx.Data)) return ReviewResult.Fail(NotForReview);
        var days = ctx.Data.Days.Where(d => d.Checks.Count == 0 && d.Verdict is null).ToList();
        if (days.Count == 0) return ReviewResult.Success;
        var now = clock.GetUtcNow();
        var err = await UpsertDays(ctx.Data, days.Select(d => d.Date), r =>
        {
            (r.Verdict, r.Query) = (ReviewVerdicts.Ok, null);
            (r.DecidedBy, r.DecidedAt) = (ctx.User.Id, now);
        }, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx, "day_ok", $"Marked {days.Count} unflagged day{(days.Count == 1 ? "" : "s")} fine.", cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> ReturnReviewAsync(Guid storeId, DateOnly periodStart, string message, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, false, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (!ReviewerCanDecide(ctx.Data)) return ReviewResult.Fail(NotForReview);
        if (ctx.Data.Days.All(d => d.Verdict != ReviewVerdicts.Query)) return ReviewResult.Fail("Query at least one day first.");
        var text = Clean(message, 2000);
        if (text.Length == 0) return ReviewResult.Fail("Write a message to the store.");
        var now = clock.GetUtcNow();
        var err = await WriteReview(ctx, r =>
        {
            r.Status = ReviewStatuses.Returned;
            (r.DecidedAt, r.DecidedBy) = (now, ctx.User.Id);
            r.ReturnMessage = text;
        }, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx, "returned", $"Returned to the store: {text}", cancellationToken);
        await notices.NotifyReviewAsync(
            await notices.StoreUserIdsAsync(storeId, cancellationToken),
            ctx.User.Id,
            Db.ReviewNoticeKinds.Returned,
            $"Your plan for {ctx.Data.PeriodTitle} was returned by {ctx.User.Username}: {text}",
            Link(ctx.Data),
            cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> ApproveReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, false, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        var d = ctx.Data;
        if (!ReviewerCanDecide(d)) return ReviewResult.Fail(NotForReview);
        var pending = d.Days.Count(x => x.Verdict != ReviewVerdicts.Ok);
        if (pending > 0) return ReviewResult.Fail($"Mark every day fine first - {pending} to go.");

        // Approving the review approves the plan, every day of it - which is what
        // unlocks the roster (built from this plan on the Roster page).
        var approved = await planApproval.ApprovePlanDaysAsync(storeId, Period.PeriodDatesFrom(periodStart), ctx.User.Id, cancellationToken);
        if (!approved.Ok) return new ReviewResult(false, approved.Error);
        var now = clock.GetUtcNow();
        var err = await WriteReview(ctx, r =>
        {
            r.Status = ReviewStatuses.Approved;
            (r.DecidedAt, r.DecidedBy) = (now, ctx.User.Id);
        }, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx, "approved", "Approved the plan. The roster can now be generated from it.", cancellationToken);
        await notices.NotifyReviewAsync(
            await notices.StoreUserIdsAsync(storeId, cancellationToken),
            ctx.User.Id,
            Db.ReviewNoticeKinds.Approved,
            $"Your plan for {d.PeriodTitle} was approved by {ctx.User.Username}. You can now generate the roster on the Roster page.",
            Link(d),
            cancellationToken);
        return ReviewResult.Success;
    }

    // ─── Unlocking an approved plan (owner decision R5) ─────────────────────
    // An approved plan is locked: it can't be edited or regenerated. It changes
    // only after being deliberately unlocked by the EMSO or above - the area's
    // EMSO, a DSO or a super user - at the store's request, and the changed plan
    // goes back through the Review. A published roster stays on the floor
    // meanwhile; the Roster page shows it faded until it is rebuilt from the plan
    // once it is approved again.

    private static readonly Tier[] UnlockTiers = [Tier.Emso, Tier.Director, Tier.SuperUser];
    private const string NotLocked = "This plan isn't approved, so it isn't locked.";

    // The store asks, saying why; its reviewers hear about it on the bell.
    public async Task<ReviewResult> RequestUnlockAsync(Guid storeId, DateOnly periodStart, string reason, CancellationToken cancellationToken = default)
    {
        var (ctx, error) = await Begin(storeId, periodStart, true, true, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (ctx.Data.Status != Db.ReviewStatus.Approved) return ReviewResult.Fail(NotLocked);
        var why = Clean(reason, 1000);
        if (why.Length == 0) return ReviewResult.Fail("Say why the plan needs to change.");
        await LogEvent(ctx, "unlock_requested", why, cancellationToken);
        await notices.NotifyReviewAsync(
            await notices.ReviewerIdsAsync(ctx.Data.AreaId, cancellationToken),
            ctx.User.Id,
            Db.ReviewNoticeKinds.UnlockRequested,
            $"{StoreLabel(ctx.Data)} asks to unlock its approved plan for {ctx.Data.PeriodTitle}: {why}",
            Link(ctx.Data),
            cancellationToken);
        return ReviewResult.Success;
    }

    // The EMSO or above unlocks it: the plan stops being approved, every day goes
    // back to the store, and it is reviewed again once changed.
    public async Task<ReviewResult> UnlockReviewAsync(Guid storeId, DateOnly periodStart, string reason, CancellationToken cancellationToken = default)
    {
        if (!Period.IsPeriodStart(periodStart)) return ReviewResult.Fail("That isn't the start of a fortnight.");
        if (scope.User is not { } user) return ReviewResult.Fail(ScopeMessages.DeniedAuth);
        if (!UnlockTiers.Contains(user.Tier))
            return ReviewResult.Fail("Only the store's EMSO, a DSO or a super user can unlock an approved plan.");
        // An EMSO only in their own area. A DSO's login is otherwise read-only,
        // network-wide: unlocking is the one change it may make.
        if (user.Tier != Tier.Director)
        {
            var denied = await scope.DenyStoreWriteAsync(storeId, cancellationToken);
            if (denied is not null) return ReviewResult.Fail(denied);
        }
        var data = await reader.LoadReviewAsync(storeId, periodStart, cancellationToken);
        if (data is null) return ReviewResult.Fail("That store isn't available to you.");
        if (data.Status != Db.ReviewStatus.Approved) return ReviewResult.Fail(NotLocked);
        var why = Clean(reason, 1000);
        if (why.Length == 0) return ReviewResult.Fail("Say why the plan is being unlocked.");
        var ctx = new Ctx(user, data);
        // The plan is no longer approved, so the roster locks again until it is (a
        // roster already published stays on the floor, shown faded).
        var withdrawn = await planApproval.WithdrawPlanDaysAsync(storeId, Period.PeriodDatesFrom(periodStart), user.Id, cancellationToken);
        if (!withdrawn.Ok) return new ReviewResult(false, withdrawn.Error);
        await UpdateDays(data, _ => true, r => (r.Verdict, r.Query, r.Reply, r.RepliedAt) = (null, null, null, null), cancellationToken);
        var err = await WriteReview(ctx, r =>
        {
            r.Status = ReviewStatuses.InProgress;
            (r.DecidedAt, r.DecidedBy) = (null, null);
        }, cancellationToken);
        if (err is not null) return ReviewResult.Fail(err);
        await LogEvent(ctx, "unlocked", $"Unlocked the plan: {why}", cancellationToken);
        await notices.NotifyReviewAsync(
            await notices.StoreUserIdsAsync(storeId, cancellationToken),
            user.Id,
            Db.ReviewNoticeKinds.PlanUnlocked,
            $"{user.Username} unlocked your plan for {data.PeriodTitle}: {why}. Make your changes, then submit it again.",
            Link(data),
            cancellationToken);
        return ReviewResult.Success;
    }

    public async Task<ReviewResult> SendReminderAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        // Most useful before the store has even generated its plan.
        var (ctx, error) = await Begin(storeId, periodStart, false, false, cancellationToken);
        if (ctx is null) return ReviewResult.Fail(error!);
        if (ctx.Data.Status == Db.ReviewStatus.Submitted && !ctx.Data.Stale) return ReviewResult.Fail("It's already with you.");
        if (ctx.Data.Status == Db.ReviewStatus.Approved) return ReviewResult.Fail("It's already approved.");
        var users = await notices.StoreUserIdsAsync(storeId, cancellationToken);
        if (users.Count == 0) return ReviewResult.Fail("This store has no logins to remind.");
        await LogEvent(ctx, "reminder", "Sent the store a reminder.", cancellationToken);
        await notices.NotifyReviewAsync(
            users,
            ctx.User.Id,
            Db.ReviewNoticeKinds.Reminder,
            $"Reminder from {ctx.User.Username}: please check and submit your plan for {ctx.Data.PeriodTitle} (due {ctx.Data.DueLabel}).",
            Link(ctx.Data),
            cancellationToken);
        return ReviewResult.Success;
    }

    // ─── The bell ────────────────────────────────────────────────────────────

    // Opening a review clears your unread notices for it (the notifications rule
    // limits the update to your own rows), so the bell only counts reviews you
    // haven't looked at.
    public async Task MarkReviewNoticesReadAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        if (scope.User is null) return;
        var link = Db.ReviewLinks.ReviewLink(storeId, periodStart);
        try
        {
            await using var db = contexts.Create();
            var now = clock.GetUtcNow();
            var rows = await db.Notifications.Where(n => n.Link == link && n.ReadAt == null).ToListAsync(cancellationToken);
            foreach (var n in rows) n.ReadAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is DbUpdateException or ScopeViolationException)
        {
            // Left unread; the next visit tries again.
        }
    }

    // ─── The data layer's review as the page's view models ───────────────────

    internal static ReviewData ToView(Db.ReviewData d) => new()
    {
        StoreId = d.StoreId,
        StoreCode = d.StoreCode,
        StoreName = d.StoreName,
        AreaId = d.AreaId,
        PeriodStart = d.PeriodStart,
        PeriodEnd = d.PeriodEnd,
        PeriodTitle = d.PeriodTitle,
        DueLabel = d.DueLabel,
        HasSnapshot = d.HasSnapshot,
        GeneratedAt = d.GeneratedAt,
        SnapshotKey = d.SnapshotKey,
        Status = ReviewStatusKeys.FromKey(d.Status) ?? ReviewStatus.InProgress,
        Stale = d.Stale,
        SubmittedAt = d.SubmittedAt,
        SubmittedBy = d.SubmittedBy,
        DecidedAt = d.DecidedAt,
        DecidedBy = d.DecidedBy,
        ReturnMessage = d.ReturnMessage,
        UnlockRequest = d.UnlockRequest is { } u ? new UnlockRequest(u.By, u.At, u.Reason) : null,
        Days = [.. d.Days.Select(x => new ReviewDayView
        {
            Date = x.Date,
            Dow = x.Dow,
            DateLabel = x.DateLabel,
            LongLabel = x.LongLabel,
            Closed = x.Closed,
            HasData = x.HasData,
            Forecast = x.Forecast,
            NonOp = x.NonOp,
            Benchmark = x.Benchmark,
            Planned = x.Planned,
            PlannedShifts = (int)x.PlannedShifts,
            Checks = [.. x.Checks.Select(c => new ReviewCheckView
            {
                Key = c.Key,
                Date = c.Date,
                Severity = c.Severity == Severity.Error ? CheckSeverity.Error : CheckSeverity.Warning,
                HardStop = c.HardStop,
                Message = c.Message,
                Detail = c.Detail,
                Done = c.Done,
                Note = c.Note,
            })],
            Verdict = DayVerdictKeys.FromKey(x.Verdict),
            Query = x.Query,
            Reply = x.Reply,
            PrevQuery = x.PrevQuery,
            PrevReply = x.PrevReply,
        })],
        Events = [.. d.Events.Select(e => new ReviewEventView(e.At, e.ActorName, e.ActorTier, e.Kind, e.Body))],
        ReviewerNames = d.ReviewerNames,
        ReviewerFallback = d.ReviewerFallback,
    };

    private static QueueStatus QueueStatusOf(string status) => status switch
    {
        Db.ReviewStatus.Submitted => QueueStatus.Submitted,
        Db.ReviewStatus.Returned => QueueStatus.Returned,
        Db.ReviewStatus.Approved => QueueStatus.Approved,
        Db.ReviewStatus.InProgress => QueueStatus.InProgress,
        _ => QueueStatus.NotStarted,
    };
}
