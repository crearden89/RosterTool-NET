// New: a stand-in for the Review page's service until the data layer's review loader,
// notifications and plan approval land. Three invented stores in one area, each with a
// held plan (one without), whose flags, figures and review state live in memory for the
// life of the app, so the page can be built, tried in a browser and tested. The steps keep
// actions.ts's rules and words; the formatting here is the stand-in's own.

using System.Globalization;
using Roster.Engine.Core;
using Roster.Web.Features.Notifications;

namespace Roster.Web.Features.Review;

/// <summary>Who the stand-in is signed in as: <c>Review:FakeTier</c> (store, emso, super_user, director, pa).</summary>
public sealed record FakeReviewUser(Guid Id, string Username, string Tier, Guid? StoreId, Guid? AreaId);

/// <summary>The stand-in's stores and reviews, shared by every connection.</summary>
public sealed class FakeReviewStore
{
    public static readonly Guid AreaId = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    public static readonly Guid Northside = Guid.Parse("50000000-0000-4000-8000-000000000101");
    public static readonly Guid Riverside = Guid.Parse("50000000-0000-4000-8000-000000000102");
    public static readonly Guid Hillcrest = Guid.Parse("50000000-0000-4000-8000-000000000103");

    internal static readonly IReadOnlyList<(Guid Id, string Code, string Name)> Stores =
    [
        (Northside, "G101", "Northside"),
        (Riverside, "G102", "Riverside"),
        (Hillcrest, "G103", "Hillcrest"),
    ];

    internal readonly object Gate = new();
    internal readonly Dictionary<(Guid, DateOnly), FakeReview> Reviews = [];

    public FakeReviewStore(IConfiguration configuration)
    {
        User = UserFor(configuration["Review:FakeTier"] ?? "super_user");
    }

    /// <summary>The signed-in stand-in user.</summary>
    public FakeReviewUser User { get; set; }

    /// <summary>A stand-in user of a tier: the store login of G101, the area's EMSO, a super user, a DSO or a PA.</summary>
    public static FakeReviewUser UserFor(string tier) => tier switch
    {
        "store" => new(Guid.Parse("e0000000-0000-4000-8000-000000000001"), "sm.g101", "store", Northside, null),
        "emso" => new(Guid.Parse("e0000000-0000-4000-8000-000000000002"), "j.morgan", "emso", null, AreaId),
        "director" => new(Guid.Parse("e0000000-0000-4000-8000-000000000003"), "dso.north", "director", null, null),
        "pa" => new(Guid.Parse("e0000000-0000-4000-8000-000000000004"), "pa.north", "pa", null, null),
        _ => new(Guid.Parse("e0000000-0000-4000-8000-000000000005"), "admin", "super_user", null, null),
    };

    /// <summary>The plan changed under the review (a regenerate or an edit): its version goes up.</summary>
    public void BumpPlan(Guid storeId, DateOnly periodStart)
    {
        lock (Gate) Get(storeId, periodStart).PlanVersion++;
    }

    internal FakeReview Get(Guid storeId, DateOnly periodStart)
    {
        if (!Reviews.TryGetValue((storeId, periodStart), out var r))
            Reviews[(storeId, periodStart)] = r = new FakeReview { PlanVersion = storeId == Riverside ? 0 : 1 };
        return r;
    }
}

internal sealed class FakeReview
{
    public int PlanVersion { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.InProgress;
    public string? SnapshotKey { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? ReturnMessage { get; set; }
    public Dictionary<string, string?> Checked { get; } = [];
    public Dictionary<DateOnly, FakeDay> Days { get; } = [];
    public List<FakeEvent> Events { get; } = [];
}

internal sealed record FakeEvent(DateTimeOffset At, string Actor, string Tier, string Kind, string Body);

internal sealed class FakeDay
{
    public DayVerdict? Verdict { get; set; }
    public string? Query { get; set; }
    public string? Reply { get; set; }
    public string? PrevQuery { get; set; }
    public string? PrevReply { get; set; }
}

/// <summary><see cref="IReviewService"/> over <see cref="FakeReviewStore"/>.</summary>
public sealed class FakeReviewService(FakeReviewStore store, FakeNotificationStore notices, TimeProvider clock) : IReviewService
{
    private static readonly CultureInfo AuCulture = CultureInfo.GetCultureInfo("en-AU");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private const string StoreLocked = "This plan is with the EMSO. Withdraw it first to make changes.";
    private const string HardStopMessage =
        "This is a hard stop: someone alone, a junior without three adults, or no till while trading. Fix it in the plan - an edit or a re-plan. A note can't clear it.";
    private const string NotForReview = "There's nothing to decide: the store hasn't submitted this plan (or changed it since).";
    private const string NotLocked = "This plan isn't approved, so it isn't locked.";

    // ── The page ──

    public Task<ReviewPageModel> LoadPageAsync(Guid? requestedStore, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            var user = store.User;
            var isReviewer = user.Tier is "emso" or "super_user";
            var queue = isReviewer ? Queue(periodStart) : [];
            var waiting = requestedStore is null && user.StoreId is null
                ? queue.FirstOrDefault(q => q.Status == QueueStatus.Submitted) ?? queue.FirstOrDefault()
                : null;
            var storeId = user.StoreId
                ?? waiting?.StoreId
                ?? (FakeReviewStore.Stores.Any(s => s.Id == requestedStore) ? requestedStore : FakeReviewStore.Stores[0].Id);
            var data = storeId is { } sid ? Load(sid, periodStart) : null;
            var canWrite = user.Tier is "store" or "emso" or "super_user" && (user.StoreId is null || user.StoreId == storeId);
            var link = ReviewLink(storeId ?? Guid.Empty, periodStart);
            return Task.FromResult(new ReviewPageModel
            {
                PeriodStart = periodStart,
                PeriodTitle = PeriodTitle(periodStart),
                StoreId = storeId,
                Stores = [.. FakeReviewStore.Stores.Select(s => new ReviewStoreOption(s.Id, $"{s.Code} · {s.Name}"))],
                ShowStorePicker = user.StoreId is null && FakeReviewStore.Stores.Count > 1,
                Data = data,
                Queue = queue,
                CanStore = canWrite && user.Tier is "store" or "super_user",
                CanReview = canWrite && isReviewer,
                CanUnlock = (canWrite && isReviewer) || user.Tier == "director",
                UnreadNotices = notices.Snapshot().Count(n => n.ReadAt is null && n.Link == link),
            });
        }
    }

    // ── The store's side ──

    public Task<ReviewResult> CheckFlagAsync(Guid storeId, DateOnly periodStart, string key, string? note = null, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Store, true, (ctx, r) =>
        {
            if (!StoreCanEdit(ctx)) return ReviewResult.Fail(StoreLocked);
            var check = ctx.AllChecks().FirstOrDefault(c => c.Key == key);
            if (check is null) return ReviewResult.Fail("That flag is no longer on the plan. Refresh the page.");
            if (check.HardStop) return ReviewResult.Fail(HardStopMessage);
            var text = Clean(note, 1000);
            if (check.Severity == CheckSeverity.Error && text.Length == 0)
                return ReviewResult.Fail("An error needs a note saying why it's OK.");
            r.Checked[key] = text.Length == 0 ? null : text;
            Log(r, "checked", $"Checked {DayLabel(ctx, check.Date)}: {check.Message}{(text.Length > 0 ? $". Note: {text}" : "")}");
            return ReviewResult.Success;
        });

    public Task<ReviewResult> UncheckFlagAsync(Guid storeId, DateOnly periodStart, string key, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Store, true, (ctx, r) =>
        {
            if (!StoreCanEdit(ctx)) return ReviewResult.Fail(StoreLocked);
            var was = ctx.AllChecks().FirstOrDefault(c => c.Key == key);
            r.Checked.Remove(key);
            return new ReviewResult(true, Note: was?.Note);
        });

    public Task<ReviewResult> ReplyToQueryAsync(Guid storeId, DateOnly periodStart, DateOnly date, string text, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Store, true, (ctx, r) =>
        {
            if (ctx.Status != ReviewStatus.Returned) return ReviewResult.Fail("There's no query to answer.");
            var day = ctx.Days.FirstOrDefault(d => d.Date == date);
            if (day is null || day.Verdict != DayVerdict.Query) return ReviewResult.Fail("That day has no query.");
            var reply = Clean(text, 1000);
            if (reply.Length == 0) return ReviewResult.Fail("Write a reply first.");
            DayRow(r, date).Reply = reply;
            Log(r, "replied", $"Replied on {DayLabel(ctx, date)}: {reply}");
            return ReviewResult.Success;
        });

    public Task<ReviewResult> SubmitReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Store, true, (d, r) =>
        {
            if (!StoreCanEdit(d)) return ReviewResult.Fail(d.Status == ReviewStatus.Approved ? "This fortnight is already approved." : StoreLocked);
            var hard = d.AllChecks().Count(c => c.HardStop);
            if (hard > 0) return ReviewResult.Fail($"Fix the hard stops in the plan first - {hard} to go. A note can't clear them.");
            var open = d.AllChecks().Count(c => !c.Done);
            if (open > 0) return ReviewResult.Fail($"Check every flag first - {open} still to go.");
            var queried = d.Days.Where(x => x.Verdict == DayVerdict.Query).ToList();
            var resubmit = d.Status == ReviewStatus.Returned;
            if (resubmit && queried.Any(x => string.IsNullOrEmpty(x.Reply))) return ReviewResult.Fail("Answer every query first.");
            if (resubmit)
            {
                foreach (var x in queried)
                {
                    var row = DayRow(r, x.Date);
                    (row.PrevQuery, row.PrevReply) = (x.Query, x.Reply);
                    (row.Verdict, row.Query, row.Reply) = (null, null, null);
                }
            }
            else
            {
                foreach (var row in r.Days.Values) (row.Verdict, row.Query, row.Reply) = (null, null, null);
            }
            r.Status = ReviewStatus.Submitted;
            r.SnapshotKey = d.SnapshotKey;
            r.SubmittedAt = clock.GetUtcNow();
            r.ReturnMessage = null;
            var to = d.ReviewerFallback ? "the super users (no EMSO is set up for this area)" : string.Join(", ", d.ReviewerNames);
            Log(r, resubmit ? "resubmitted" : "submitted", $"{(resubmit ? "Resubmitted" : "Submitted")} to {to} for review.");
            return ReviewResult.Success;
        });

    public Task<ReviewResult> WithdrawReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Store, true, (d, r) =>
        {
            if (d.Status != ReviewStatus.Submitted) return ReviewResult.Fail("This fortnight isn't with the EMSO.");
            r.Status = ReviewStatus.InProgress;
            Log(r, "withdrawn", "Withdrew the submission to make changes.");
            return ReviewResult.Success;
        });

    // ── The EMSO's side ──

    public Task<ReviewResult> SetDayVerdictAsync(Guid storeId, DateOnly periodStart, DateOnly date, DayVerdict verdict, string? query = null, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Reviewer, true, (d, r) =>
        {
            if (!ReviewerCanDecide(d)) return ReviewResult.Fail(NotForReview);
            if (d.Days.All(x => x.Date != date)) return ReviewResult.Fail("That day isn't in this fortnight.");
            var text = Clean(query, 1000);
            if (verdict == DayVerdict.Query && text.Length == 0) return ReviewResult.Fail("Say what the store should look at.");
            var row = DayRow(r, date);
            (row.Verdict, row.Query, row.Reply) = (verdict, verdict == DayVerdict.Query ? text : null, null);
            Log(r, verdict == DayVerdict.Ok ? "day_ok" : "day_query",
                verdict == DayVerdict.Ok ? $"Marked {DayLabel(d, date)} fine." : $"Queried {DayLabel(d, date)}: {text}");
            return ReviewResult.Success;
        });

    public Task<ReviewResult> MarkUnflaggedFineAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Reviewer, true, (d, r) =>
        {
            if (!ReviewerCanDecide(d)) return ReviewResult.Fail(NotForReview);
            var days = d.Days.Where(x => x.Checks.Count == 0 && x.Verdict is null).ToList();
            if (days.Count == 0) return ReviewResult.Success;
            foreach (var x in days)
            {
                var row = DayRow(r, x.Date);
                (row.Verdict, row.Query) = (DayVerdict.Ok, null);
            }
            Log(r, "day_ok", $"Marked {days.Count} unflagged day{(days.Count == 1 ? "" : "s")} fine.");
            return ReviewResult.Success;
        });

    public Task<ReviewResult> ReturnReviewAsync(Guid storeId, DateOnly periodStart, string message, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Reviewer, true, (d, r) =>
        {
            if (!ReviewerCanDecide(d)) return ReviewResult.Fail(NotForReview);
            if (d.Days.All(x => x.Verdict != DayVerdict.Query)) return ReviewResult.Fail("Query at least one day first.");
            var text = Clean(message, 2000);
            if (text.Length == 0) return ReviewResult.Fail("Write a message to the store.");
            r.Status = ReviewStatus.Returned;
            r.DecidedAt = clock.GetUtcNow();
            r.ReturnMessage = text;
            Log(r, "returned", $"Returned to the store: {text}");
            return ReviewResult.Success;
        });

    public Task<ReviewResult> ApproveReviewAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Reviewer, true, (d, r) =>
        {
            if (!ReviewerCanDecide(d)) return ReviewResult.Fail(NotForReview);
            var pending = d.Days.Count(x => x.Verdict != DayVerdict.Ok);
            if (pending > 0) return ReviewResult.Fail($"Mark every day fine first - {pending} to go.");
            r.Status = ReviewStatus.Approved;
            r.DecidedAt = clock.GetUtcNow();
            Log(r, "approved", "Approved the plan. The roster can now be generated from it.");
            return ReviewResult.Success;
        });

    // ── Unlocking an approved plan (R5) ──

    public Task<ReviewResult> RequestUnlockAsync(Guid storeId, DateOnly periodStart, string reason, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Store, true, (d, r) =>
        {
            if (d.Status != ReviewStatus.Approved) return ReviewResult.Fail(NotLocked);
            var why = Clean(reason, 1000);
            if (why.Length == 0) return ReviewResult.Fail("Say why the plan needs to change.");
            Log(r, "unlock_requested", why);
            return ReviewResult.Success;
        });

    public Task<ReviewResult> UnlockReviewAsync(Guid storeId, DateOnly periodStart, string reason, CancellationToken cancellationToken = default)
    {
        lock (store.Gate)
        {
            if (!Period.IsPeriodStart(periodStart)) return Done(ReviewResult.Fail("That isn't the start of a fortnight."));
            var user = store.User;
            if (user.Tier is not ("emso" or "director" or "super_user"))
                return Done(ReviewResult.Fail("Only the store's EMSO, a DSO or a super user can unlock an approved plan."));
            var data = Load(storeId, periodStart);
            if (data is null) return Done(ReviewResult.Fail("That store isn't available to you."));
            if (data.Status != ReviewStatus.Approved) return Done(ReviewResult.Fail(NotLocked));
            var why = Clean(reason, 1000);
            if (why.Length == 0) return Done(ReviewResult.Fail("Say why the plan is being unlocked."));
            var r = store.Get(storeId, periodStart);
            foreach (var row in r.Days.Values) (row.Verdict, row.Query, row.Reply) = (null, null, null);
            r.Status = ReviewStatus.InProgress;
            r.DecidedAt = null;
            Log(r, "unlocked", $"Unlocked the plan: {why}");
            return Done(ReviewResult.Success);
        }
    }

    public Task<ReviewResult> SendReminderAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        Step(storeId, periodStart, Side.Reviewer, false, (d, r) =>
        {
            if (d.Status == ReviewStatus.Submitted && !d.Stale) return ReviewResult.Fail("It's already with you.");
            if (d.Status == ReviewStatus.Approved) return ReviewResult.Fail("It's already approved.");
            Log(r, "reminder", "Sent the store a reminder.");
            return ReviewResult.Success;
        });

    public Task MarkReviewNoticesReadAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        notices.MarkRead(ReviewLink(storeId, periodStart), clock.GetUtcNow());
        return Task.CompletedTask;
    }

    // ── The steps' common start (begin), and the review read ──

    private enum Side { Store, Reviewer }

    private Task<ReviewResult> Step(Guid storeId, DateOnly periodStart, Side side, bool needRoster, Func<ReviewData, FakeReview, ReviewResult> act)
    {
        lock (store.Gate)
        {
            if (!Period.IsPeriodStart(periodStart)) return Done(ReviewResult.Fail("That isn't the start of a fortnight."));
            var user = store.User;
            if (user.Tier is "pa" or "director") return Done(ReviewResult.Fail("Your access level is read-only - this change was not saved."));
            if (user.StoreId is { } own && own != storeId) return Done(ReviewResult.Fail("You don't have access to that store."));
            var tiers = side == Side.Store ? new[] { "store", "super_user" } : ["emso", "super_user"];
            if (!tiers.Contains(user.Tier))
                return Done(ReviewResult.Fail(side == Side.Store ? "Only the store can do this." : "Only the store's EMSO can do this."));
            var data = Load(storeId, periodStart);
            if (data is null) return Done(ReviewResult.Fail("That store isn't available to you."));
            if (needRoster && !data.HasSnapshot) return Done(ReviewResult.Fail("Generate this fortnight's plan first."));
            return Done(act(data, store.Get(storeId, periodStart)));
        }
    }

    private static Task<ReviewResult> Done(ReviewResult r) => Task.FromResult(r);

    private static bool StoreCanEdit(ReviewData d) =>
        d.Status is ReviewStatus.InProgress or ReviewStatus.Returned || (d.Status == ReviewStatus.Submitted && d.Stale);

    private static bool ReviewerCanDecide(ReviewData d) => d.Status == ReviewStatus.Submitted && !d.Stale;

    private static string Clean(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static string DayLabel(ReviewData d, DateOnly? date)
    {
        if (date is null) return "Fortnight";
        var day = d.Days.FirstOrDefault(x => x.Date == date);
        return day is null ? date.Value.ToIso() : $"{day.Dow} {day.DateLabel}";
    }

    private static FakeDay DayRow(FakeReview r, DateOnly date)
    {
        if (!r.Days.TryGetValue(date, out var row)) r.Days[date] = row = new FakeDay();
        return row;
    }

    private void Log(FakeReview r, string kind, string body)
    {
        var u = store.User;
        r.Events.Add(new FakeEvent(clock.GetUtcNow(), u.Username, u.Tier, kind, body.Length > 3000 ? body[..3000] : body));
    }

    private List<QueueStore> Queue(DateOnly periodStart) =>
    [
        .. FakeReviewStore.Stores.Select(s =>
        {
            var r = store.Reviews.GetValueOrDefault((s.Id, periodStart));
            var status = r is null ? QueueStatus.NotStarted : r.Status switch
            {
                ReviewStatus.Submitted => QueueStatus.Submitted,
                ReviewStatus.Returned => QueueStatus.Returned,
                ReviewStatus.Approved => QueueStatus.Approved,
                _ => QueueStatus.InProgress,
            };
            var ts = status == QueueStatus.Submitted ? r?.SubmittedAt : status is QueueStatus.Returned or QueueStatus.Approved ? r?.DecidedAt : null;
            return new QueueStore(s.Id, s.Code, s.Name, status, ts is { } t ? Stamp(t) : null,
                $"/plan/review?store={s.Id}&period={periodStart.ToIso()}");
        }),
    ];

    private ReviewData? Load(Guid storeId, DateOnly periodStart)
    {
        var s = FakeReviewStore.Stores.FirstOrDefault(x => x.Id == storeId);
        if (s.Code is null) return null;
        var r = store.Get(storeId, periodStart);
        var hasSnapshot = r.PlanVersion > 0;
        var snapshotKey = $"plan:{r.PlanVersion}";
        var current = r.SnapshotKey == snapshotKey;
        var status = r.Status == ReviewStatus.Approved && !current ? ReviewStatus.InProgress : r.Status;
        var dates = Period.PeriodDatesFrom(periodStart);
        var days = dates.Select((date, i) => Day(storeId, date, i, hasSnapshot, r)).ToList();
        var events = r.Events.AsEnumerable().Reverse().ToList();
        FakeEvent? LastBy(params string[] kinds) => events.FirstOrDefault(e => kinds.Contains(e.Kind));
        var lastLock = LastBy("unlock_requested", "unlocked", "approved");
        var reviewerNames = new[] { "j.morgan" };
        return new ReviewData
        {
            StoreId = storeId,
            StoreCode = s.Code,
            StoreName = s.Name,
            AreaId = FakeReviewStore.AreaId,
            PeriodStart = periodStart,
            PeriodEnd = Period.PeriodEndFrom(periodStart),
            PeriodTitle = PeriodTitle(periodStart),
            DueLabel = $"{Dates.AuDayLabel(Period.AddDaysIso(periodStart, -5))}, 6:00 pm",
            HasSnapshot = hasSnapshot,
            GeneratedAt = hasSnapshot ? Stamp(new DateTimeOffset(periodStart.AddDays(-16).ToDateTime(new TimeOnly(23, 42)), TimeSpan.Zero)) : null,
            SnapshotKey = snapshotKey,
            Status = status,
            Stale = status == ReviewStatus.Submitted && !current,
            SubmittedAt = r.SubmittedAt is { } sa ? Stamp(sa) : null,
            SubmittedBy = LastBy("submitted", "resubmitted")?.Actor,
            DecidedAt = r.DecidedAt is { } da ? Stamp(da) : null,
            DecidedBy = LastBy("returned", "approved")?.Actor,
            ReturnMessage = r.ReturnMessage,
            UnlockRequest = status == ReviewStatus.Approved && lastLock?.Kind == "unlock_requested"
                ? new UnlockRequest(lastLock.Actor, Stamp(lastLock.At), lastLock.Body)
                : null,
            Days = days,
            Events = [.. events.Select(e => new ReviewEventView(Stamp(e.At), e.Actor, e.Tier, e.Kind, e.Body))],
            ReviewerNames = reviewerNames,
            ReviewerFallback = false,
        };
    }

    // The invented plan: two weeks of trading, the same flags every fortnight.
    private static ReviewDayView Day(Guid storeId, DateOnly date, int i, bool hasSnapshot, FakeReview r)
    {
        var flags = new List<(string Message, CheckSeverity Severity, bool Hard)>();
        if (hasSnapshot)
        {
            if (i == 3) flags.Add(("No manager rostered 20:00–21:00", CheckSeverity.Error, false));
            if (i == 5) flags.Add(("2.00h added so the tills stay manned while the crew takes its breaks", CheckSeverity.Warning, false));
            if (i == 8) flags.Add(("Very short day - both manager shifts cover the whole span", CheckSeverity.Warning, false));
            if (i == 10)
            {
                flags.Add(("Cleaning 09:00–10:00: 0.25h of its 1.00h is worked in its hour", CheckSeverity.Error, false));
                flags.Add(("0.25h of shift time is held by the 3-hour minimum (15:45–16:00)", CheckSeverity.Warning, false));
            }
            if (i == 2 && storeId == FakeReviewStore.Hillcrest)
                flags.Insert(0, ("Fewer than 2 people rostered 20:00–21:00 - someone works alone", CheckSeverity.Error, true));
        }
        var checks = flags
            .Select((f, n) => (f.Message, f.Severity, f.Hard, N: n))
            .OrderBy(f => f.Severity == CheckSeverity.Error ? 0 : 1)
            .Select(f =>
            {
                var key = $"{date.ToIso()}:{f.N}";
                var done = r.Checked.TryGetValue(key, out var note);
                return new ReviewCheckView
                {
                    Key = key,
                    Date = date,
                    Severity = f.Severity,
                    HardStop = f.Hard,
                    Message = f.Message,
                    Done = done,
                    Note = done ? note : null,
                };
            })
            .ToList();
        var row = r.Days.GetValueOrDefault(date);
        var bench = hasSnapshot ? 52 + (i % 7) * 2.25 : 0;
        return new ReviewDayView
        {
            Date = date,
            Dow = Period.DayLabels[Period.DowMon0(date)],
            DateLabel = Dates.AuDayMonth(date),
            LongLabel = date.ToString("dddd d MMMM", AuCulture),
            HasData = hasSnapshot,
            Forecast = hasSnapshot ? 28_400 + (i % 7) * 2_150.5 : null,
            NonOp = hasSnapshot ? 2 + (i % 3) * 0.5 : 0,
            Benchmark = bench,
            Planned = hasSnapshot ? bench + (i == 5 ? 2 : i == 8 ? -1.25 : 0.25) : 0,
            PlannedShifts = hasSnapshot ? 9 + i % 4 : 0,
            Checks = checks,
            Verdict = row?.Verdict,
            Query = row?.Query,
            Reply = row?.Reply,
            PrevQuery = row?.PrevQuery,
            PrevReply = row?.PrevReply,
        };
    }

    private static string PeriodTitle(DateOnly start)
    {
        var end = Period.PeriodEndFrom(start);
        return $"{Dates.AuDayLabel(start)} – {Dates.AuDayLabel(end)} {end.Year}";
    }

    private static string Stamp(DateTimeOffset at)
    {
        var t = TimeZoneInfo.ConvertTime(at, Sydney);
        var h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return $"{Dates.AuDayLabel(DateOnly.FromDateTime(t.DateTime))}, {h}:{t.Minute:00} {(t.Hour < 12 ? "am" : "pm")}";
    }

    private static string ReviewLink(Guid storeId, DateOnly periodStart) => $"/plan/review?store={storeId}&period={periodStart.ToIso()}";
}
