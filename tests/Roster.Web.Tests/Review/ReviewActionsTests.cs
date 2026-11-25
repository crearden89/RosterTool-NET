// Port of src/app/(app)/plan/review/actions.test.ts.
// The SM and EMSO plan review (Plan -> Review -> Roster), end to end through
// the real review service, review loader and roster gate, against an
// in-memory database. Only the signed-in user, the held plan snapshot and the
// day-by-day plan approval writes are stood in for (ReviewTestDb).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Held;
using Roster.Data.Services.Review;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Review;
using static Roster.Web.Tests.Review.ReviewTestDb;
using HeldPlan = Roster.Data.Services.Held.PlanSnapshot;
using ReviewResult = Roster.Web.Features.Review.ReviewResult;
using ReviewData = Roster.Data.Services.Review.ReviewData;
using ReviewCheckView = Roster.Data.Services.Review.ReviewCheckView;

namespace Roster.Web.Tests.Review;

public sealed class ReviewActionsTests : IDisposable
{
    private static readonly DateOnly PS = new(2026, 11, 23);
    private static readonly List<DateOnly> Dates = Period.PeriodDatesFrom(PS);
    // An error a note can clear (no manager on, F2 - owner decision R3).
    private const string Lone = "No manager rostered 20:00–21:00";

    private readonly ReviewTestDb _t = new();

    public ReviewActionsTests() => _t.Plan = Snapshot();

    public void Dispose() => _t.Dispose();

    // Two 25 h shifts a day against a 50 h benchmark: on benchmark. Thursday plans
    // someone alone (an error); Saturday bought till cover and the second Tuesday
    // is a very short day (warnings). No roster yet - it waits for the review.
    private static HeldPlan Snapshot(int version = 1)
    {
        static PlanShift Shift() => new()
        {
            SlotId = "s",
            PlanRole = "SA",
            Start = 360,
            End = 1860,
            BreakMinutes = 0,
            UnpaidBreakMinutes = 0,
            Blocks = [.. Enumerable.Range(0, 100).Select(i => new DisplayBlock { Time = 360 + i * 15, Group = "checkout" })],
        };
        static List<string> Flags(int i) =>
            i == 3 ? [Lone]
            : i == 5 ? ["2.00h added so the tills stay manned while the crew takes its breaks"]
            : i == 8 ? ["Very short day - both manager shifts cover the whole span"]
            : [];
        return new HeldPlan
        {
            Version = version,
            GeneratedAt = DateTimeOffset.Parse("2026-11-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Days =
            [
                .. Dates.Select((date, i) => new PlanSnapshotDay
                {
                    Date = date,
                    // As planSnapshots sets it: a day carrying any plan flag is not OK.
                    Ok = Flags(i).Count == 0,
                    Result = new Stage1Computation
                    {
                        HasData = true,
                        Closed = false,
                        TotalBenchmark = 50,
                        TotalAllocated = 50,
                        Variance = 0,
                        WithinTolerance = true,
                        Allocations = [new GroupAllocation("non_op", 2, 2)],
                        StaffOpen = 300,
                        StaffClose = 1320,
                        TradingOpen = 480,
                        TradingClose = 1200,
                    },
                    Plan = new PlanShiftComputation { HasData = true, TradingOpen = 480, TradingClose = 1200, Flags = Flags(i), Shifts = [Shift(), Shift()] },
                }),
            ],
        };
    }

    private static readonly ReviewResult Ok = new(true);

    private static ReviewResult Fail(string error) => new(false, error);

    private void As(ScopedUser u) => _t.As(u);

    private RosterReview? ReviewRow()
    {
        using var db = _t.System();
        return db.RosterReviews.AsNoTracking().FirstOrDefault(r => r.StoreId == St1 && r.PeriodStart == PS);
    }

    private List<Notification> Notices(ScopedUser user)
    {
        using var db = _t.System();
        return [.. db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id).OrderBy(n => n.CreatedAt)];
    }

    private List<string> NoticeKinds(ScopedUser user) => [.. Notices(user).Select(n => n.Kind)];

    private async Task<ReviewData> Load() => (await _t.Reader.LoadReviewAsync(St1, PS))!;

    private async Task<List<ReviewCheckView>> Checks() => [.. (await Load()).AllChecks()];

    private IReviewService A => _t.Service;

    private async Task CheckEverything()
    {
        As(Sm);
        foreach (var c in await Checks())
        {
            var r = await A.CheckFlagAsync(St1, PS, c.Key, c.Severity == Severity.Error ? "Cover booked from Northgate" : null);
            Assert.True(r == Ok, $"{c.Message}: {r}");
        }
    }

    // ─── fortnight review ───

    [Fact]
    public async Task Wont_take_a_note_for_a_hard_stop_it_has_to_be_fixed_in_the_plan_R3()
    {
        const string alone = "Fewer than 2 people rostered 20:00–21:00 - someone works alone";
        var snap = Snapshot();
        _t.Plan = snap with
        {
            Days = [.. snap.Days.Select((d, i) => i == 3 ? d with { Plan = d.Plan! with { Flags = [alone] } } : d)],
        };
        var hard = (await Checks()).First(c => c.Message == alone);
        Assert.True(hard.HardStop);
        As(Sm);
        var r = await A.CheckFlagAsync(St1, PS, hard.Key, "Two from Riverside");
        Assert.False(r.Ok);
        Assert.Matches("(?i)hard stop", r.Error);
        foreach (var c in await Checks())
            if (!c.HardStop) await A.CheckFlagAsync(St1, PS, c.Key, "noted");
        Assert.Equal("Fix the hard stops in the plan first - 1 to go. A note can't clear them.", (await A.SubmitReviewAsync(St1, PS)).Error);
    }

    [Fact]
    public async Task Builds_the_checklist_from_the_held_plan_errors_need_notes()
    {
        var all = await Checks();
        Assert.Equal([Severity.Error, Severity.Warning, Severity.Warning], all.Select(c => c.Severity).OrderBy(s => s == Severity.Error ? 0 : 1));
        var err = all.First(c => c.Severity == Severity.Error);
        Assert.Equal(Lone, err.Message);
        As(Sm);
        Assert.Equal(Fail("An error needs a note saying why it's OK."), await A.CheckFlagAsync(St1, PS, err.Key, "  "));
        Assert.Equal(Fail("Check every flag first - 3 still to go."), await A.SubmitReviewAsync(St1, PS));
    }

    [Fact]
    public async Task Shows_the_checks_of_a_day_that_isnt_OK_those_are_the_days_with_something_to_explain()
    {
        var data = await Load();
        var lone = data.Days[3];
        Assert.True(lone.HasData);
        Assert.Contains(Lone, lone.Checks.Select(c => c.Message));
        Assert.Equal(Dates.Count, data.Days.Count(d => d.HasData));
    }

    [Fact]
    public async Task Runs_a_plan_from_the_first_check_to_approval_telling_each_side_on_the_bell()
    {
        await CheckEverything();
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        Assert.Equal("submitted", ReviewRow()?.Status);
        Assert.Equal(["review_submitted"], NoticeKinds(Emso));
        Assert.Equal($"/plan/review?store={St1}&period=2026-11-23", Notices(Emso)[0].Link);
        Assert.Empty(Notices(Admin)); // the area has an EMSO

        // The store can't change a submitted review.
        var key = (await Checks())[0].Key;
        Assert.False((await A.UncheckFlagAsync(St1, PS, key)).Ok);

        // The EMSO passes every day but Thursday, queries it and returns the plan.
        As(Emso);
        Assert.Equal(Ok, await A.MarkUnflaggedFineAsync(St1, PS));
        Assert.Equal(Ok, await A.SetDayVerdictAsync(St1, PS, Dates[5], DayVerdict.Ok));
        Assert.Equal(Ok, await A.SetDayVerdictAsync(St1, PS, Dates[8], DayVerdict.Ok));
        Assert.Equal(Ok, await A.SetDayVerdictAsync(St1, PS, Dates[3], DayVerdict.Query, "Who is covering?"));
        Assert.Equal(Fail("Mark every day fine first - 1 to go."), await A.ApproveReviewAsync(St1, PS));
        Assert.Equal(Ok, await A.ReturnReviewAsync(St1, PS, "Thu: who is covering?"));
        Assert.Equal("returned", ReviewRow()?.Status);
        Assert.Equal(["review_returned"], NoticeKinds(Sm));

        // The store answers and resubmits; only Thursday goes back to the EMSO.
        As(Sm);
        Assert.Equal(Fail("Answer every query first."), await A.SubmitReviewAsync(St1, PS));
        Assert.Equal(Ok, await A.ReplyToQueryAsync(St1, PS, Dates[3], "Two from Riverside, confirmed."));
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        var d = await Load();
        Assert.Equal("submitted", d.Status);
        Assert.Equal(13, d.Days.Count(x => x.Verdict == "ok"));
        Assert.Null(d.Days[3].Verdict);
        Assert.Equal("Who is covering?", d.Days[3].PrevQuery);
        Assert.Equal("Two from Riverside, confirmed.", d.Days[3].PrevReply);
        // The EMSO's bell carries one notice for the review, the newest.
        Assert.Equal(["review_resubmitted"], NoticeKinds(Emso));

        // Approval approves the plan, every day of it, and unlocks the roster.
        As(Emso);
        Assert.Equal(Ok, await A.SetDayVerdictAsync(St1, PS, Dates[3], DayVerdict.Ok));
        Assert.False((await _t.Gate.RosterGateAsync(St1, PS)).Unlocked);
        Assert.Equal(Ok, await A.ApproveReviewAsync(St1, PS));
        Assert.Equal([Dates], _t.ApprovedPlans);
        Assert.Equal("approved", (await Load()).Status);
        var gate = await _t.Gate.RosterGateAsync(St1, PS);
        Assert.True(gate.Unlocked);
        Assert.True(gate.PlanApproved);
        Assert.Equal(["review_approved"], NoticeKinds(Sm));
        Assert.Contains("generate the roster", Notices(Sm)[0].Body);

        // The approved plan is locked. The store asks for it to be unlocked,
        // saying why, and the EMSO hears of it on the bell.
        As(Sm);
        Assert.Equal(Fail("Say why the plan needs to change."), await A.RequestUnlockAsync(St1, PS, "  "));
        Assert.Equal(Ok, await A.RequestUnlockAsync(St1, PS, "Two team members resigned"));
        Assert.Equal(["unlock_requested"], NoticeKinds(Emso));
        var request = (await Load()).UnlockRequest;
        Assert.Equal("sm.g999", request?.By);
        Assert.Equal("Two team members resigned", request?.Reason);
        // ...but can't unlock it itself.
        Assert.Equal(Fail("Only the store's EMSO, a DSO or a super user can unlock an approved plan."), await A.UnlockReviewAsync(St1, PS, "Resignations"));

        // The EMSO unlocks it, saying why: the plan approval is withdrawn, the
        // roster locks again and every day goes back to the store.
        As(Emso);
        Assert.Equal(Fail("Say why the plan is being unlocked."), await A.UnlockReviewAsync(St1, PS, ""));
        Assert.Equal(Ok, await A.UnlockReviewAsync(St1, PS, "Agreed - re-plan around the resignations"));
        Assert.Equal([Dates], _t.WithdrawnPlans);
        Assert.False((await _t.Gate.RosterGateAsync(St1, PS)).Unlocked);
        var unlocked = await Load();
        Assert.Equal("in_progress", unlocked.Status);
        Assert.Null(unlocked.UnlockRequest);
        Assert.All(unlocked.Days, x => Assert.Null(x.Verdict));
        Assert.Equal(["plan_unlocked"], NoticeKinds(Sm));
        Assert.Contains("Agreed - re-plan around the resignations", Notices(Sm)[0].Body);
        Assert.Equal(Fail("This plan isn't approved, so it isn't locked."), await A.UnlockReviewAsync(St1, PS, "again"));

        // Every step is in the activity log, newest first, with who did it.
        var kinds = unlocked.Events.Select(e => e.Kind).ToList();
        foreach (var k in new[] { "checked", "submitted", "day_ok", "day_query", "returned", "replied", "resubmitted", "approved", "unlock_requested", "unlocked" })
            Assert.True(kinds.Contains(k), k);
        Assert.Equal(["unlocked", "unlock_requested", "approved"], kinds.Take(3));
        Assert.Equal("Unlocked the plan: Agreed - re-plan around the resignations", unlocked.Events.First(e => e.Kind == "unlocked").Body);
        Assert.Equal("j.morgan", unlocked.Events.First(e => e.Kind == "returned").ActorName);
        Assert.Equal("sm.g999", unlocked.Events.First(e => e.Kind == "submitted").ActorName);
    }

    [Fact]
    public async Task Lets_a_DSO_unlock_an_approved_plan_the_one_change_their_read_only_login_may_make()
    {
        await CheckEverything();
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        As(Emso);
        Assert.Equal(Ok, await A.MarkUnflaggedFineAsync(St1, PS));
        foreach (var i in new[] { 3, 5, 8 }) Assert.Equal(Ok, await A.SetDayVerdictAsync(St1, PS, Dates[i], DayVerdict.Ok));
        Assert.Equal(Ok, await A.ApproveReviewAsync(St1, PS));

        // Store writes are refused for this login...
        _t.Deny = "Your login is read-only.";
        As(Emso);
        Assert.Equal(Fail("Your login is read-only."), await A.UnlockReviewAsync(St1, PS, "Re-plan"));
        // ...but a DSO may still unlock.
        As(Dso);
        Assert.Equal(Ok, await A.UnlockReviewAsync(St1, PS, "Re-plan for the refit"));
        Assert.Equal("in_progress", (await Load()).Status);
        Assert.Equal(["plan_unlocked"], NoticeKinds(Sm));
    }

    [Fact]
    public async Task Keeps_each_side_to_its_own_steps()
    {
        await CheckEverything();
        As(Emso);
        Assert.Equal(Fail("Only the store can do this."), await A.SubmitReviewAsync(St1, PS));
        As(Sm);
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        Assert.Equal(Fail("Only the store's EMSO can do this."), await A.ApproveReviewAsync(St1, PS));
        // Outside the user's write scope (or a read-only tier), nothing happens.
        As(Emso);
        _t.Deny = "Your access level is read-only - this change was not saved.";
        Assert.Equal(Fail(_t.Deny), await A.SetDayVerdictAsync(St1, PS, Dates[0], DayVerdict.Ok));
    }

    [Fact]
    public async Task Wont_approve_a_plan_that_changed_after_it_was_submitted()
    {
        await CheckEverything();
        await A.SubmitReviewAsync(St1, PS);
        _t.Plan = Snapshot(2); // regenerated
        As(Emso);
        Assert.False((await A.MarkUnflaggedFineAsync(St1, PS)).Ok);
        Assert.True((await Load()).Stale);
        // The store can submit the new roster again.
        As(Sm);
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        Assert.False((await Load()).Stale);
    }

    [Fact]
    public async Task Sends_a_submission_to_the_super_users_when_the_area_has_no_EMSO()
    {
        using (var db = _t.System())
        {
            db.AppUsers.RemoveRange(db.AppUsers.Where(u => u.Tier == Tier.Emso));
            await db.SaveChangesAsync();
        }
        await CheckEverything();
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        Assert.Equal(["review_submitted"], NoticeKinds(Admin));
        Assert.True((await Load()).ReviewerFallback);
    }

    [Fact]
    public async Task Lets_the_EMSO_remind_a_store_that_hasnt_generated_yet()
    {
        _t.Plan = null;
        As(Emso);
        Assert.Equal(Ok, await A.SendReminderAsync(St1, PS));
        Assert.Equal(["review_reminder"], NoticeKinds(Sm));
        Assert.Contains("due", Notices(Sm)[0].Body);
    }

    // ─── Plan -> Review -> Roster gate ───

    private async Task ApproveAll()
    {
        await CheckEverything();
        Assert.Equal(Ok, await A.SubmitReviewAsync(St1, PS));
        As(Emso);
        Assert.Equal(Ok, await A.MarkUnflaggedFineAsync(St1, PS));
        foreach (var i in new[] { 3, 5, 8 }) Assert.Equal(Ok, await A.SetDayVerdictAsync(St1, PS, Dates[i], DayVerdict.Ok));
        Assert.Equal(Ok, await A.ApproveReviewAsync(St1, PS));
    }

    private static ReviewActor Actor(ScopedUser u) => new(u.Id, u.Username, u.Tier.Key());

    [Fact]
    public async Task Keeps_the_roster_locked_until_the_plan_is_approved()
    {
        var gate = await _t.Gate.RosterGateAsync(St1, PS);
        Assert.Equal((false, false, false), (gate.Unlocked, gate.PlanApproved, gate.Published));
        await ApproveAll();
        gate = await _t.Gate.RosterGateAsync(St1, PS);
        Assert.Equal((true, true), (gate.Unlocked, gate.PlanApproved));
    }

    [Fact]
    public async Task Sends_an_approved_plan_back_round_when_it_changes_and_locks_the_roster_again()
    {
        await ApproveAll();
        _t.Plan = Snapshot(2); // the store regenerates its plan
        await _t.Gate.LapseReviewOnPlanChangeAsync(St1, PS, Actor(Sm), "The plan was regenerated");
        Assert.Equal("in_progress", ReviewRow()?.Status);
        Assert.Equal([Dates], _t.WithdrawnPlans); // the plan approval comes off
        Assert.All((await Load()).Days, d => Assert.Null(d.Verdict));
        var gate = await _t.Gate.RosterGateAsync(St1, PS);
        Assert.Equal((false, false), (gate.Unlocked, gate.PlanApproved));
        Assert.Equal(["review_withdrawn"], NoticeKinds(Emso));
        Assert.Contains("changed its plan", Notices(Emso)[0].Body);
    }

    [Fact]
    public async Task Leaves_a_returned_plan_with_the_store_when_it_changes_thats_the_store_doing_its_work()
    {
        await CheckEverything();
        await A.SubmitReviewAsync(St1, PS);
        As(Emso);
        await A.SetDayVerdictAsync(St1, PS, Dates[3], DayVerdict.Query, "Who is on with them?");
        await A.ReturnReviewAsync(St1, PS, "Thu: who is on with them?");
        await _t.Gate.LapseReviewOnPlanChangeAsync(St1, PS, Actor(Sm), "The plan was edited");
        Assert.Equal("returned", ReviewRow()?.Status);
    }

    [Fact]
    public async Task Keeps_a_published_roster_readable_but_locked_and_faded_until_its_plan_is_approved_again_R5()
    {
        // Publishing no longer reopens the roster for good (review RC-17).
        using (var db = _t.System())
        {
            foreach (var date in Dates) db.DayPlans.Add(new DayPlan { StoreId = St1, Date = date, RosterStatus = "approved" });
            await db.SaveChangesAsync();
        }
        var gate = await _t.Gate.RosterGateAsync(St1, PS);
        Assert.Equal((false, false, true, true), (gate.Unlocked, gate.PlanApproved, gate.Published, gate.OutOfDate));
    }

    [Fact]
    public void Calls_a_roster_built_before_the_plans_latest_approval_out_of_date()
    {
        var approved = new RosterGateInput
        {
            ReviewStatus = "approved",
            ReviewKey = ReviewFormat.PlanKey(1),
            PlanVersion = 1,
            Dates = Dates,
            ApprovedDates = new HashSet<DateOnly>(),
            DecidedAt = DateTimeOffset.Parse("2026-11-12T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        };
        static (bool, bool) Of(RosterGate g) => (g.Unlocked, g.OutOfDate);
        Assert.Equal((true, false), Of(RosterGates.GateFrom(approved with { RosterGeneratedAt = DateTimeOffset.Parse("2026-11-12T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture) })));
        Assert.Equal((true, true), Of(RosterGates.GateFrom(approved with { RosterGeneratedAt = DateTimeOffset.Parse("2026-11-11T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture) })));
        Assert.Equal((true, false), Of(RosterGates.GateFrom(approved with { RosterGeneratedAt = null })));
    }
}
