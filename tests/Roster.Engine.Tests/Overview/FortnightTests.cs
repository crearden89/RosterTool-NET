// Port of src/lib/overview/fortnight.test.ts.

using Roster.Engine.Overview;
using static Roster.Engine.Overview.FortnightCard;

namespace Roster.Engine.Tests.Overview;

public class FortnightTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    private static readonly FortnightReview InProgress = new(ReviewStatus.InProgress, false, null, null, null);

    private static readonly FortnightState Base = new(
        "s1", D("2026-11-23"), D("2026-11-15"), true, null, InProgress, new FortnightRoster(false, null));

    private static FortnightState WithPlan(FortnightReview? review = null, FortnightRoster? roster = null, string? today = null) =>
        Base with
        {
            Plan = new FortnightPlan("Sun 15 Nov, 7:48 am"),
            Review = review ?? Base.Review,
            Roster = roster ?? Base.Roster,
            Today = today is null ? Base.Today : D(today),
        };

    private static StepStatus[] States(FortnightState f) => FortnightSteps(f).Select(s => s.State).ToArray();

    private const StepStatus Done = StepStatus.Done, Current = StepStatus.Current, Todo = StepStatus.Todo, Attention = StepStatus.Attention;

    private static NextActions Store(FortnightState f) => NextActionsFor(f, canWrite: true, isReviewer: false);
    private static NextActions Emso(FortnightState f) => NextActionsFor(f, canWrite: true, isReviewer: true);
    private static NextActions ReadOnly(FortnightState f) => NextActionsFor(f, canWrite: false, isReviewer: false);

    [Fact]
    public void Waits_for_the_forecast_before_there_is_anything_to_plan()
    {
        var noForecast = Base with { HasForecast = false };
        var steps = FortnightSteps(noForecast);
        Assert.Equal([Todo, Todo, Todo, Todo], steps.Select(s => s.State));
        Assert.Equal("Waiting for the forecast", steps[0].Detail);
        Assert.Equal("Open the plan", Store(noForecast).Primary.Label);
    }

    [Fact]
    public void Asks_for_the_plan_first()
    {
        Assert.Equal([Current, Todo, Todo, Todo], States(Base));
        Assert.Equal(new LinkAction("Generate the plan", "/plan/generate?store=s1&period=2026-11-23"), Store(Base).Primary);
        Assert.Equal("Open the plan", ReadOnly(Base).Primary.Label);
    }

    [Fact]
    public void Sends_a_generated_plan_to_the_stores_review()
    {
        var f = WithPlan();
        Assert.Equal([Done, Current, Todo, Todo], States(f));
        Assert.Equal("Sun 15 Nov, 7:48 am", FortnightSteps(f)[0].Detail);
        Assert.Equal(
            new NextActions(
                new LinkAction("Check and submit the plan", "/plan/review?store=s1&period=2026-11-23"),
                new LinkAction("Open the plan", "/plan/generate?store=s1&period=2026-11-23")),
            Store(f));
        Assert.Equal(new Headline(ChipTone.Soft, "Plan in review", false), FortnightHeadline(f));
    }

    [Fact]
    public void Waits_for_the_emso_once_submitted_and_gives_the_emso_the_approval()
    {
        var f = WithPlan(InProgress with { Status = ReviewStatus.Submitted, SubmittedAt = "Sun 15 Nov, 9:02 am" });
        Assert.Equal([Done, Done, Current, Todo], States(f));
        Assert.Equal("Submitted Sun 15 Nov, 9:02 am", FortnightSteps(f)[1].Detail);
        Assert.Equal("Open the review", Store(f).Primary.Label);
        Assert.Equal("Review and approve", Emso(f).Primary.Label);
        Assert.Equal("Waiting for approval", FortnightHeadline(f).Text);
    }

    [Fact]
    public void Flags_a_returned_review_for_the_store()
    {
        var f = WithPlan(InProgress with { Status = ReviewStatus.Returned, ReturnMessage = "Tuesday needs a second till" });
        var steps = FortnightSteps(f);
        Assert.Equal(Attention, steps[1].State);
        Assert.Equal("Returned: “Tuesday needs a second till”", steps[1].Detail);
        Assert.Equal(Todo, steps[2].State);
        Assert.Equal(ChipTone.Red, FortnightHeadline(f).Tone);
        Assert.Equal("Returned to the store", FortnightHeadline(f).Text);
        Assert.Equal("Check and submit the plan", Store(f).Primary.Label);
    }

    [Fact]
    public void Shortens_a_long_return_message()
    {
        var message = "  Tuesday   needs a second till,\n and Wednesday's close is short by an hour on the evening fill, please look again  ";
        var detail = FortnightSteps(WithPlan(InProgress with { Status = ReviewStatus.Returned, ReturnMessage = message }))[1].Detail;
        // The TypeScript's shorten(), checked in Node: whitespace collapsed, 69 characters and an ellipsis.
        Assert.Equal("Returned: “Tuesday needs a second till, and Wednesday's close is short by an hou…”", detail);
    }

    [Fact]
    public void Treats_a_submission_the_plan_has_moved_on_from_as_needing_another_look()
    {
        var f = WithPlan(InProgress with { Status = ReviewStatus.Submitted, Stale = true });
        Assert.Equal([Done, Attention, Todo, Todo], States(f));
        Assert.Equal("Check and submit the plan", Store(f).Primary.Label);
    }

    [Fact]
    public void Moves_to_the_roster_once_approved()
    {
        var approved = InProgress with { Status = ReviewStatus.Approved, SubmittedAt = "Sun 15 Nov", DecidedAt = "Sun 15 Nov, 10:15 am" };
        var ready = WithPlan(approved);
        Assert.Equal([Done, Done, Done, Current], States(ready));
        Assert.Equal("Ready to build", FortnightSteps(ready)[3].Detail);
        Assert.Equal(
            "Built Sun 15 Nov, 11:00 am, not published yet",
            FortnightSteps(WithPlan(approved, new FortnightRoster(false, "Sun 15 Nov, 11:00 am")))[3].Detail);
        Assert.Equal(new LinkAction("Build and publish the roster", "/plan/roster?store=s1&period=2026-11-23"), Store(ready).Primary);
        Assert.Equal(new Headline(ChipTone.Green, "Plan approved · locked", true), FortnightHeadline(ready));
    }

    [Fact]
    public void Is_done_when_the_roster_is_on_the_floor()
    {
        var f = WithPlan(
            InProgress with { Status = ReviewStatus.Approved, DecidedAt = "Sun 15 Nov, 6:48 am" },
            new FortnightRoster(true, "Sun 15 Nov, 7:00 am"));
        Assert.Equal([Done, Done, Done, Done], States(f));
        Assert.Equal("On the floor from Mon 23 Nov", FortnightSteps(f)[3].Detail);
        Assert.Equal("On the floor since Mon 23 Nov", FortnightSteps(f with { Today = D("2026-11-24") })[3].Detail);
        Assert.Equal(new Headline(ChipTone.Green, "Approved · roster published", true), FortnightHeadline(f));
        Assert.Equal("Open the roster", ReadOnly(f).Primary.Label);
    }

    [Fact]
    public void Says_when_a_published_rosters_plan_has_been_unlocked_since()
    {
        var f = WithPlan(roster: new FortnightRoster(true, null));
        Assert.Equal(ChipTone.Amber, FortnightHeadline(f).Tone);
        Assert.False(FortnightHeadline(f).Lock);
        Assert.Equal(Done, States(f)[3]);
    }

    [Fact]
    public void Is_overdue_when_the_fortnight_has_started_without_a_published_roster()
    {
        Assert.False(IsOverdue(WithPlan()));
        Assert.True(IsOverdue(WithPlan(today: "2026-11-23")));
        Assert.False(IsOverdue(WithPlan(roster: new FortnightRoster(true, null), today: "2026-11-30")));
    }

    // ── the fortnight's figures ──

    private static readonly FortnightFigureDay[] Days =
    [
        new(100_000, 44.5, 45, 10),
        new(120_000, 48.25, 48, 11),
        new(null, 0, 0, 0),
    ];

    [Fact]
    public void Adds_up_the_days_as_the_review_page_counts_them()
    {
        Assert.Equal(new FortnightFigures(220_000, 92.75, 93, 21), FortnightFiguresFor(Days, true));
    }

    [Fact]
    public void Knows_only_the_forecast_before_a_plan_is_held()
    {
        Assert.Equal(new FortnightFigures(220_000, null, null, null), FortnightFiguresFor(Days, false));
        Assert.Null(FortnightFiguresFor([], false).Forecast);
    }

    // ── day labels ──

    [Fact]
    public void Reads_the_date_itself()
    {
        Assert.Equal("Mon 23 Nov", DayLabel(D("2026-11-23")));
        Assert.Equal("Sun 6 Dec", DayLabel(D("2026-12-06")));
        Assert.Equal("23 Nov – 6 Dec", RangeLabel(D("2026-11-23"), D("2026-12-06")));
    }
}
