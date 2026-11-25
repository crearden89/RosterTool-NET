// New: the Plan page's components rendered with bUnit - the status chip and the Review
// link (PlanStatus.tsx), the approve buttons, Generate in the filter bar, the day block's
// benchmark table and edit mode, and the page's empty state.

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Components.Ui;
using Roster.Web.Features.Plan;
using Roster.Web.Features.Review;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Plan;

public class PlanComponentsTests : UiTestContext
{
    private static readonly Guid Store = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    private readonly StubPlanService _plan = new();

    public PlanComponentsTests()
    {
        Services.AddSingleton<IPlanService>(_plan);
        Services.AddSingleton(TimeProvider.System);
    }

    [Theory]
    [InlineData(PlanReviewState.Approved, true, PlanOrRoster.Plan, "Approved · locked")]
    [InlineData(PlanReviewState.Approved, false, PlanOrRoster.Roster, "Plan approved")]
    [InlineData(PlanReviewState.Submitted, false, PlanOrRoster.Plan, "With the EMSO")]
    [InlineData(PlanReviewState.Returned, false, PlanOrRoster.Roster, "Plan returned - action needed")]
    [InlineData(PlanReviewState.NotSubmitted, false, PlanOrRoster.Plan, "Not submitted")]
    public void The_status_chip_says_where_the_review_stands(PlanReviewState state, bool locked, PlanOrRoster page, string text)
    {
        var cut = Render<PlanStatusChip>(p => p.Add(c => c.State, state).Add(c => c.Locked, locked).Add(c => c.Page, page));
        Assert.Equal(text, cut.Find("span").TextContent.Trim());
    }

    [Theory]
    [InlineData(PlanReviewState.NotSubmitted, true, false, "Review & submit")]
    [InlineData(PlanReviewState.Returned, true, false, "Answer the query")]
    [InlineData(PlanReviewState.Approved, true, true, "Ask to unlock")]
    [InlineData(PlanReviewState.Approved, false, true, "Open review")]
    [InlineData(PlanReviewState.Submitted, true, false, "Open review")]
    public void The_review_link_says_what_the_viewer_does_there_next(PlanReviewState state, bool isStore, bool locked, string label)
    {
        var cut = Render<ReviewLink>(p => p.Add(c => c.Href, "/plan/review?store=s&period=2026-11-23").Add(c => c.State, state).Add(c => c.IsStore, isStore).Add(c => c.Locked, locked));
        Assert.Equal(label, cut.Find("a").TextContent.Trim());
        Assert.Equal("plan/review?store=s&period=2026-11-23", cut.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void Approving_a_roster_with_a_cover_gap_asks_for_a_note_first()
    {
        string? sent = null;
        var saved = 0;
        _plan.ApproveRoster = (_, _, comment) =>
        {
            sent = comment;
            return Task.FromResult(PlanResult.Done);
        };
        var cut = Render<ApproveButtons>(p => p
            .Add(c => c.StoreId, Store).Add(c => c.Date, new DateOnly(2026, 11, 23)).Add(c => c.Kind, PlanOrRoster.Roster)
            .Add(c => c.RequiresComment, true).Add(c => c.CommentReason, "Tue 24 Nov has an open shift")
            .Add(c => c.OnSaved, () => saved++));
        cut.Find("button").Click();
        Assert.Contains("Error: Tue 24 Nov has an open shift. Add a note on how you tried to cover them, then approve.", cut.Markup);
        Assert.Null(sent);
        cut.Find("input").Input("  Called three casuals  ");
        cut.Find("button").Click();
        Assert.Equal("Called three casuals", sent);
        Assert.Contains("Roster approved &amp; saved.", cut.Markup);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void Generate_plans_the_fortnight_in_steps_and_says_how_it_ended()
    {
        var period = new DateOnly(2026, 11, 23);
        _plan.Start = (_, _) => Task.FromResult(new PlanStartResult(true, RunId: "r1", Steps: [[period, period.AddDays(1)]]));
        _plan.Step = (_, _, _, _) => Task.FromResult(new PlanStepResult(true, Part: "p1"));
        _plan.Finish = (_, _, _, _, _) => Task.FromResult(PlanResult.Done);
        var reloads = 0;
        var cut = Render<FilterBar>(p => p
            .Add(c => c.Stores, [new StoreOption(Store, "G101", "Northside")]).Add(c => c.SelectedStore, Store)
            .Add(c => c.SelectedDate, period).Add(c => c.PeriodStart, period).Add(c => c.BasePath, "/plan/generate")
            .Add(c => c.CanGenerate, true).Add(c => c.Title, "Plan").Add(c => c.OnGenerated, () => reloads++));
        var button = cut.Find("button[title='Solve the fortnight and hold the result']");
        Assert.Equal("Generate Plan", button.TextContent.Trim());
        button.Click();
        cut.WaitForAssertion(() => Assert.Contains("✓ Plan regenerated and held", cut.Markup));
        Assert.Equal(["start", "step", "finish"], _plan.Calls);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public void A_locked_plan_shows_Generate_greyed_out_with_the_reason()
    {
        var period = new DateOnly(2026, 11, 23);
        var cut = Render<FilterBar>(p => p
            .Add(c => c.Stores, []).Add(c => c.SelectedStore, Store).Add(c => c.PeriodStart, period).Add(c => c.BasePath, "/plan/generate")
            .Add(c => c.HasSnapshot, true).Add(c => c.CanGenerate, false).Add(c => c.Title, "Plan")
            .Add(c => c.LockedNote, "Approved plans are locked - unlock it on Review to regenerate."));
        var button = cut.Find("button[title='Approved plans are locked - unlock it on Review to regenerate.']");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("Regenerate", button.TextContent.Trim());
        Assert.Contains("Approved plans are locked - unlock it on Review to regenerate.", cut.Find("span.text-xs").TextContent);
    }

    private static (Stage1Computation Result, PlanShiftComputation Plan) Day()
    {
        var day = FakePlanDays.Invent(Store, new DateOnly(2026, 11, 24), false);
        return (day.Result, day.Plan!);
    }

    [Fact]
    public void The_day_block_draws_the_benchmark_table_and_opens_a_group_into_its_sub_tasks()
    {
        var (result, plan) = Day();
        var cut = Render<DayPlanBlock>(p => p
            .Add(c => c.Date, new DateOnly(2026, 11, 24)).Add(c => c.Result, result).Add(c => c.Plan, plan)
            .Add(c => c.StoreId, Store).Add(c => c.PeriodStart, new DateOnly(2026, 11, 23)).Add(c => c.CanEdit, true));
        Assert.Equal("Tuesday 24 November", cut.Find("h2").TextContent.Trim());
        var rows = cut.FindAll("tbody tr").Select(r => r.QuerySelector("td")!.TextContent.Trim()).ToList();
        Assert.Contains("Checkout", rows);
        Assert.Contains("Breaks", rows);
        Assert.Contains("Total work", rows);
        Assert.DoesNotContain(rows, r => r.StartsWith("Re-runs"));
        cut.Find("button[aria-label=\"Open Reset's sub-tasks\"]").Click();
        rows = cut.FindAll("tbody tr").Select(r => r.QuerySelector("td")!.TextContent.Trim()).ToList();
        Assert.Contains(rows, r => r.Contains("Re-runs"));
        Assert.Contains(rows, r => r.Contains("Decarding"));
    }

    [Fact]
    public void Edit_mode_offers_the_paint_chips_and_a_benchmark_row_picks_its_group()
    {
        var (result, plan) = Day();
        PlanShiftEdit[]? saved = null;
        _plan.Edit = (_, _, _, edits) =>
        {
            saved = [.. edits];
            return Task.FromResult(PlanResult.Done);
        };
        var cut = Render<DayPlanBlock>(p => p
            .Add(c => c.Date, new DateOnly(2026, 11, 24)).Add(c => c.Result, result).Add(c => c.Plan, plan)
            .Add(c => c.StoreId, Store).Add(c => c.PeriodStart, new DateOnly(2026, 11, 23)).Add(c => c.CanEdit, true));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit").Click();
        Assert.Contains("Pick a labour group, then click 15-min tiles to fill:", cut.Markup);
        Assert.Contains("Nothing selected yet", cut.Markup);
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Cleaning")).Click();
        Assert.Contains("← painting", cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Cleaning")).TextContent);
        Assert.Contains("Click tiles to paint", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();
        cut.WaitForAssertion(() => Assert.NotNull(saved));
        Assert.Equal(plan.Shifts.Count, saved!.Length);
        Assert.DoesNotContain("Save changes", cut.Markup);
    }
}
