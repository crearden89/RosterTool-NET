// Tests for the ports of src/lib/review/gate.ts, notify.ts and server.ts,
// src/lib/plan/approval.ts, roster/approval.ts (withdraw), roster/drift.ts and
// lib/notifications.ts, on D's in-memory SQLite.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Held;
using Roster.Data.Services.Notifications;
using Roster.Data.Services.Planning;
using Roster.Data.Services.Review;
using Roster.Data.Snapshots;
using Roster.Data.Tests.Support;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using EngineBlock = Roster.Engine.Planning.Stage1.PlannedBlock;

namespace Roster.Data.Tests.Services.Review;

public sealed class ReviewWorkflowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 22, 42, 0, TimeSpan.Zero); // Wed 11 Nov 9:42 am, Sydney
    private static readonly DateOnly Period = new(2026, 11, 23); // a grid fortnight (Mon 23 Nov - Sun 6 Dec)
    private readonly TestDatabase _tdb = new();
    private readonly TestContexts _contexts;
    private readonly FixedClock _clock = new(Now);
    private readonly Store _store;
    private readonly Area _area;
    private readonly PlanSnapshotStore _snapshots;
    private readonly HeldPlans _held;
    private readonly ReviewNoticesService _notices;
    private readonly PlanApprovalService _planApproval;
    private readonly ReviewGateService _gate;
    private readonly ReviewReaderService _reader;

    public ReviewWorkflowTests()
    {
        _contexts = new TestContexts(_tdb);
        _store = Seed.Stores(_tdb)[0];
        using (var db = _tdb.System())
        {
            _area = new Area { Id = Guid.NewGuid(), Name = "Area Example" };
            db.Areas.Add(_area);
            db.Stores.Attach(_store).Entity.AreaId = _area.Id;
            db.SaveChanges();
        }
        _snapshots = new PlanSnapshotStore(_contexts);
        var stage1 = new ThrowingStage1();
        _held = new HeldPlans(_snapshots, stage1, null!, null!, new PlanPeriods(_contexts, _clock), _clock);
        _notices = new ReviewNoticesService(_contexts, _clock, NullLogger<ReviewNoticesService>.Instance);
        _planApproval = new PlanApprovalService(_contexts, _held, stage1, _clock);
        _gate = new ReviewGateService(_contexts, _snapshots, _planApproval, _notices, _clock);
        _reader = new ReviewReaderService(_contexts, _held);
    }

    public void Dispose() => _tdb.Dispose();

    private sealed class ThrowingStage1 : IStage1Loader
    {
        public Task<Stage1Computation> ComputeStage1Async(Guid storeId, DateOnly date, CancellationToken ct = default) =>
            throw new InvalidOperationException("no live solve in these tests");

        public Task<Stage1Sources> LoadStage1SourcesAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
            throw new InvalidOperationException("no live solve in these tests");
    }

    private AppUser User(Tier tier, string name, Guid? areaId = null, Guid? storeId = null)
    {
        using var db = _tdb.System();
        var u = new AppUser { Id = Guid.NewGuid(), Username = name, Tier = tier, AreaId = areaId, StoreId = storeId };
        db.AppUsers.Add(u);
        db.SaveChanges();
        return u;
    }

    private static Stage1Computation Day(bool hasData, bool closed = false, params string[] flags) => new()
    {
        HasData = hasData,
        Closed = closed,
        TotalBenchmark = hasData ? 12 : 0,
        Flags = [.. flags],
        Blocks = hasData ? [new EngineBlock { Group = "load_running", Time = 420, Headcount = 2 }, new EngineBlock { Group = "admin", Time = 435, Headcount = 1 }] : [],
        Allocations = hasData ? [new GroupAllocation("non_op", 1.5, 1.5)] : [],
    };

    private static PlanShiftComputation Plan(params string[] flags) => new()
    {
        HasData = true,
        Shifts = [new PlanShift { SlotId = "mgr-open", PlanRole = "MGR", Start = 420, End = 900 }],
        Flags = [.. flags],
        Checks = [.. flags],
    };

    /// <summary>Hold a plan for the period: day 1 with data and two flags, day 2 closed, the rest without data.</summary>
    private async Task<int> HoldPlanAsync()
    {
        var days = Roster.Engine.Core.Period.PeriodDatesFrom(Period).Select((d, i) => new PlanSnapshotDay
        {
            Date = d,
            Ok = i == 1,
            Result = i == 0 ? Day(true, false, "No manager rostered 07:00-07:15") : i == 1 ? Day(false, closed: true) : Day(false),
            Plan = i == 0 ? Plan("Planned 0.50h over the benchmark") : null,
        }).ToList();
        return (await _held.StorePlanSnapshotAsync(_store.Id, Period, days)).Version;
    }

    [Fact]
    public async Task The_gate_opens_on_an_approval_of_the_held_plan_and_fades_a_roster_built_before_it()
    {
        var version = await HoldPlanAsync();
        Assert.False((await _gate.RosterGateAsync(_store.Id, Period)).Unlocked);
        Assert.Equal(RosterGates.RosterLocked, await _gate.RosterChangeRefusalAsync(_store.Id, Period));
        using (var db = _tdb.System())
        {
            db.RosterReviews.Add(new RosterReview { StoreId = _store.Id, PeriodStart = Period, Status = "approved", SnapshotKey = ReviewFormat.PlanKey(version), DecidedAt = Now });
            await db.SaveChangesAsync();
        }
        Assert.True(await _gate.PlanLockedAsync(_store.Id, Period));
        Assert.Null(await _gate.RosterChangeRefusalAsync(_store.Id, Period));
        // A roster held from before the approval is out of date.
        await _snapshots.StoreAsync(_store.Id, Period, SnapshotKinds.Roster, "{\"byDate\":[]}", Now.AddHours(-1), null);
        Assert.Equal(RosterGates.RosterOutOfDate, await _gate.RosterChangeRefusalAsync(_store.Id, Period));
        // A new plan version: the approval no longer stands.
        await HoldPlanAsync();
        Assert.False((await _gate.RosterGateAsync(_store.Id, Period)).PlanApproved);
    }

    [Fact]
    public async Task A_changed_plan_lapses_a_submitted_or_approved_review_and_tells_the_EMSO()
    {
        var emso = User(Tier.Emso, "emso.example", _area.Id);
        var storeUser = User(Tier.Store, "store.example", storeId: _store.Id);
        var actor = new ReviewActor(storeUser.Id, storeUser.Username, "store");
        await HoldPlanAsync();
        var first = Roster.Engine.Core.Period.PeriodDatesFrom(Period)[0];
        using (var db = _tdb.System())
        {
            db.RosterReviews.Add(new RosterReview { StoreId = _store.Id, PeriodStart = Period, Status = "approved", SnapshotKey = "plan:1", DecidedAt = Now, DecidedBy = emso.Id });
            db.RosterReviewDays.Add(new RosterReviewDay { StoreId = _store.Id, PeriodStart = Period, Date = first, Verdict = "query", Query = "Why?", Reply = "Because", PrevQuery = "Earlier" });
            db.PlannedBlocks.Add(new Entities.PlannedBlock { StoreId = _store.Id, Date = first, LabourGroup = "admin", BlockTime = new TimeOnly(7, 0), Headcount = 1 });
            db.DayPlans.Add(new DayPlan { StoreId = _store.Id, Date = first, PlanStatus = "approved", PlanGeneratedAt = Now });
            await db.SaveChangesAsync();
        }
        await _gate.LapseReviewOnPlanChangeAsync(_store.Id, Period, actor, "The plan was regenerated");
        using (var db = _tdb.System())
        {
            var review = await db.RosterReviews.SingleAsync();
            Assert.Equal("in_progress", review.Status);
            Assert.Null(review.SnapshotKey);
            Assert.Null(review.DecidedBy);
            var day = await db.RosterReviewDays.SingleAsync();
            Assert.Null(day.Verdict);
            Assert.Null(day.Query);
            Assert.Equal("Earlier", day.PrevQuery);
            // The approval is withdrawn with it.
            Assert.Equal(0, await db.PlannedBlocks.CountAsync());
            // Every date of the fortnight is reset, as the TypeScript's bulk upsert did.
            Assert.Equal(14, await db.DayPlans.CountAsync(d => d.PlanStatus == "none"));
            var ev = await db.RosterReviewEvents.SingleAsync();
            Assert.Equal("lapsed", ev.Kind);
            Assert.Equal("The plan was regenerated after it was approved, so it needs checking and submitting again.", ev.Body);
            var note = await db.Notifications.SingleAsync();
            Assert.Equal(emso.Id, note.UserId);
            Assert.Equal("review_withdrawn", note.Kind);
            Assert.Equal($"{_store.Name} ({_store.Code}) changed its plan for Mon 23 Nov – Sun 6 Dec 2026 after it was approved. It will come back to you for review.", note.Body);
            Assert.Equal(ReviewLinks.ReviewLink(_store.Id, Period), note.Link);
            Assert.Equal("withdraw_plan", (await db.RosterAudit.SingleAsync()).Action);
        }
        // In progress now: a second change is just the store doing its work.
        await _gate.LapseReviewOnPlanChangeAsync(_store.Id, Period, actor, "Edited");
        using (var db = _tdb.System()) Assert.Equal(1, await db.RosterReviewEvents.CountAsync());
    }

    [Fact]
    public async Task Notices_replace_the_unread_one_for_the_same_review_and_skip_the_actor()
    {
        var a = User(Tier.Emso, "a", _area.Id);
        var b = User(Tier.Emso, "b", _area.Id);
        var su = User(Tier.SuperUser, "su");
        Assert.Equal([a.Id, b.Id], await _notices.ReviewerIdsAsync(_area.Id));
        Assert.Equal([su.Id], await _notices.ReviewerIdsAsync(Guid.NewGuid()));
        Assert.Equal([su.Id], await _notices.ReviewerIdsAsync(null));
        var link = ReviewLinks.ReviewLink(_store.Id, Period);
        using (var db = _tdb.System())
        {
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = a.Id, Kind = "review_submitted", Body = "old", Link = link });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = a.Id, Kind = "review_submitted", Body = "read", Link = link, ReadAt = Now });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = a.Id, Kind = "emso_unapproved", Body = "other kind", Link = link });
            await db.SaveChangesAsync();
        }
        await _notices.NotifyReviewAsync([a.Id, b.Id, a.Id], b.Id, ReviewNoticeKinds.Resubmitted, new string('x', 600), link);
        using (var db = _tdb.System())
        {
            var mine = await db.Notifications.Where(n => n.UserId == a.Id).ToListAsync();
            Assert.Equal(["emso_unapproved", "review_resubmitted", "review_submitted"], mine.Select(n => n.Kind).Order());
            Assert.Equal(500, mine.Single(n => n.Kind == "review_resubmitted").Body.Length);
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == b.Id));
        }
    }

    [Fact]
    public async Task Load_review_reads_the_held_plan_and_the_review_rows()
    {
        User(Tier.Emso, "zed.emso", _area.Id);
        User(Tier.Emso, "amy.emso", _area.Id);
        var version = await HoldPlanAsync();
        var dates = Roster.Engine.Core.Period.PeriodDatesFrom(Period);
        var flags = new[] { "No manager rostered 07:00-07:15", "Planned 0.50h over the benchmark" };
        var checks = ReviewChecks.SortChecks(ReviewChecks.FlagsToChecks(dates[0], flags));
        using (var db = _tdb.System())
        {
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = _store.Id, Date = dates[0], SalesForecast = 41000 });
            db.RosterReviews.Add(new RosterReview { StoreId = _store.Id, PeriodStart = Period, Status = "approved", SnapshotKey = ReviewFormat.PlanKey(version), SubmittedAt = Now.AddHours(-2), DecidedAt = Now });
            db.RosterReviewChecks.Add(new RosterReviewCheck { StoreId = _store.Id, PeriodStart = Period, CheckKey = checks[0].Key, Note = "Covered by the ASM" });
            db.RosterReviewDays.Add(new RosterReviewDay { StoreId = _store.Id, PeriodStart = Period, Date = dates[0], Verdict = "ok" });
            void Ev(string kind, string actor, int minutes, string body = "") => db.RosterReviewEvents.Add(new RosterReviewEvent
            {
                Id = Guid.NewGuid(), StoreId = _store.Id, PeriodStart = Period, Kind = kind, ActorName = actor, ActorTier = "x", Body = body, CreatedAt = Now.AddMinutes(minutes),
            });
            Ev("submitted", "store.example", -120);
            Ev("approved", "amy.emso", -60);
            Ev("unlock_requested", "store.example", -30, "Wrong benchmark");
            await db.SaveChangesAsync();
        }

        var data = await _reader.LoadReviewAsync(_store.Id, Period);
        Assert.NotNull(data);
        Assert.Equal("Mon 23 Nov – Sun 6 Dec 2026", data!.PeriodTitle);
        Assert.Equal("Wed 18 Nov, 6:00 pm", data.DueLabel);
        Assert.Equal("approved", data.Status);
        Assert.False(data.Stale);
        Assert.Equal("Wed 11 Nov, 9:42 am", data.GeneratedAt);
        Assert.Equal(["amy.emso", "zed.emso"], data.ReviewerNames);
        Assert.False(data.ReviewerFallback);
        Assert.Equal("store.example", data.SubmittedBy);
        Assert.Equal("amy.emso", data.DecidedBy);
        Assert.Equal(new UnlockRequest("store.example", "Wed 11 Nov, 9:12 am", "Wrong benchmark"), data.UnlockRequest);
        Assert.Equal(["unlock_requested", "approved", "submitted"], data.Events.Select(e => e.Kind));

        var day = data.Days[0];
        Assert.Equal(("Mon", "23 Nov", "Monday 23 November"), (day.Dow, day.DateLabel, day.LongLabel));
        Assert.True(day.HasData);
        Assert.Equal(41000, day.Forecast);
        Assert.Equal(1.5, day.NonOp);
        Assert.Equal(12, day.Benchmark);
        Assert.Equal(1, day.PlannedShifts);
        Assert.Equal(checks.Select(c => c.Key), day.Checks.Select(c => c.Key));
        Assert.True(day.Checks[0].Done);
        Assert.Equal("Covered by the ASM", day.Checks[0].Note);
        Assert.Equal("ok", day.Verdict);
        Assert.True(data.Days[1].Closed);
        Assert.Empty(data.Days[1].Checks);
        Assert.Null(data.Days[2].Forecast);

        // A later plan version: an approval made on the old one reads as in progress.
        await HoldPlanAsync();
        Assert.Equal("in_progress", (await _reader.LoadReviewAsync(_store.Id, Period))!.Status);
        Assert.Null(await _reader.LoadReviewAsync(Guid.NewGuid(), Period));
    }

    [Fact]
    public async Task The_review_queue()
    {
        var other = Seed.Stores(_tdb, 1)[0];
        using (var db = _tdb.System())
        {
            db.Stores.Attach(other).Entity.AreaId = _area.Id;
            db.RosterReviews.Add(new RosterReview { StoreId = _store.Id, PeriodStart = Period, Status = "submitted", SubmittedAt = Now });
            await db.SaveChangesAsync();
        }
        var emsoQueue = await _reader.LoadReviewQueueAsync(new ReviewQueueUser("emso", _area.Id), Period);
        Assert.Equal(2, emsoQueue.Count);
        var mine = emsoQueue.Single(q => q.StoreId == _store.Id);
        Assert.Equal(("submitted", "Wed 11 Nov, 9:42 am"), (mine.Status, mine.When));
        Assert.Equal("not_started", emsoQueue.Single(q => q.StoreId == other.Id).Status);
        Assert.Equal([_store.Id], (await _reader.LoadReviewQueueAsync(new ReviewQueueUser("super_user", null), Period)).Select(q => q.StoreId));
        Assert.Empty(await _reader.LoadReviewQueueAsync(new ReviewQueueUser("store", null), Period));
    }

    [Fact]
    public async Task Plan_approval_persists_the_held_stage1_blocks_and_withdraws_them()
    {
        await HoldPlanAsync();
        var dates = Roster.Engine.Core.Period.PeriodDatesFrom(Period);
        var user = User(Tier.Emso, "emso.example", _area.Id).Id;
        var result = await _planApproval.ApprovePlanDaysAsync(_store.Id, dates, user);
        Assert.Equal(new PlanApprovalResult(true, 1), result);
        using (var db = _tdb.System())
        {
            var blocks = await db.PlannedBlocks.OrderBy(b => b.BlockTime).ToListAsync();
            Assert.Equal([("load_running", new TimeOnly(7, 0), 2), ("admin", new TimeOnly(7, 15), 1)], blocks.Select(b => (b.LabourGroup, b.BlockTime, b.Headcount)));
            var day = await db.DayPlans.SingleAsync();
            Assert.Equal(("approved", Now), (day.PlanStatus, day.PlanGeneratedAt));
            var audit = await db.RosterAudit.SingleAsync();
            Assert.Equal(("approve_plan", "{\"blocks\":2}", user), (audit.Action, audit.Detail, audit.UserId!.Value));
        }
        Assert.Equal(ApprovalResult.Success, await _planApproval.WithdrawPlanDaysAsync(_store.Id, dates, user));
        using (var db = _tdb.System())
        {
            Assert.Equal(0, await db.PlannedBlocks.CountAsync());
            Assert.Equal(14, await db.DayPlans.CountAsync(d => d.PlanStatus == "none" && d.PlanGeneratedAt == null));
            Assert.Equal("{\"days\":14}", (await db.RosterAudit.SingleAsync(a => a.Action == "withdraw_plan")).Detail);
        }
    }

    [Fact]
    public async Task Unpublish_and_drift()
    {
        var fn = new DatabaseFunctionsService(_contexts, _contexts.Scope(), _clock);
        var emp = Seed.Employee(_tdb, _store.Id, "1-Able, Ann");
        var date = Period.AddDays(2);
        await fn.ApproveRosterDayAsync(_store.Id, date, [new RosterDayShift(emp.Id, emp.Name, "SA", new TimeOnly(7, 0), new TimeOnly(15, 0), 30, false, [])]);
        var drift = new RosterDriftService(_contexts);
        // Approved, but no roster held: nothing to disagree with.
        Assert.Equal(new RosterDrift { Approved = true, ApprovedAt = Now }, await drift.DetectRosterDriftAsync(_store.Id, date));

        string Held(double start, double end) =>
            $"{{\"byDate\":[[\"{date:yyyy-MM-dd}\",{{\"shifts\":[{{\"employeeId\":\"{emp.Id:D}\",\"start\":{start},\"end\":{end}}},{{\"unfilled\":true,\"start\":0,\"end\":60}}]}}]]}}";
        await _snapshots.StoreAsync(_store.Id, Period, SnapshotKinds.Roster, Held(420, 900), Now.AddMinutes(5), null);
        Assert.False((await drift.DetectRosterDriftAsync(_store.Id, date)).Drifted);
        await _snapshots.StoreAsync(_store.Id, Period, SnapshotKinds.Roster, Held(480, 900), Now.AddMinutes(10), null);
        var moved = await drift.DetectRosterDriftAsync(_store.Id, date);
        Assert.True(moved.Drifted);
        Assert.Equal(Now.AddMinutes(10), moved.RegeneratedAt);
        // A snapshot solved before the approval is the approval's source.
        await _snapshots.StoreAsync(_store.Id, Period, SnapshotKinds.Roster, Held(480, 900), Now.AddMinutes(-10), null);
        Assert.False((await drift.DetectRosterDriftAsync(_store.Id, date)).Drifted);

        var approval = new RosterApprovalService(_contexts, fn, null!, _clock);
        Assert.Equal(ApprovalResult.Success, await approval.UnpublishRosterAsync(_store.Id, [date, date.AddDays(1)], User(Tier.Store, "store.example", storeId: _store.Id).Id));
        using var db = _tdb.System();
        Assert.Equal(0, await db.GeneratedShifts.CountAsync());
        Assert.Equal(2, await db.DayPlans.CountAsync(d => d.RosterStatus == "none" && d.RosterGeneratedAt == null));
        Assert.Contains("\"fortnightStart\":\"2026-11-25\"", (await db.RosterAudit.SingleAsync()).Detail);
        Assert.False((await drift.DetectRosterDriftAsync(_store.Id, date)).Approved);
    }

    [Fact]
    public async Task Notifications_are_the_signed_in_user_s_own_newest_first()
    {
        var me = User(Tier.Store, "me", storeId: _store.Id);
        var other = User(Tier.Store, "other", storeId: _store.Id);
        using (var db = _tdb.System())
        {
            for (var i = 0; i < 3; i++)
                db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = me.Id, Kind = "k", Body = $"n{i}", CreatedAt = Now.AddMinutes(i), ReadAt = i == 0 ? Now : null });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = other.Id, Kind = "k", Body = "not mine", CreatedAt = Now });
            await db.SaveChangesAsync();
        }
        _contexts.Current = AccessContext.ForUser(ScopedUser.From(me));
        var bell = new NotificationReaderService(_contexts);
        Assert.Equal(2, await bell.GetUnreadCountAsync());
        Assert.Equal(["n2", "n1", "n0"], (await bell.ListMyNotificationsAsync()).Select(n => n.Body));
    }
}
