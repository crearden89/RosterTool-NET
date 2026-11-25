// New: the Review page rendered (bUnit) over the in-memory stand-in service: what each
// side sees, the checks list's note rule, the day sheet and the reviewer's decisions.

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Roster.Web.Features.Review;
using Roster.Web.Tests.Infrastructure;
using Roster.Web.Tests.Notifications;

namespace Roster.Web.Tests.Review;

public sealed class ReviewPageTests : UiTestContext
{
    // Today 10 Nov 2026: the fortnight being planned starts Monday 23 November.
    private static readonly DateOnly PS = new(2026, 11, 23);
    private readonly FakeReviewStore _store = new("store");

    public ReviewPageTests()
    {
        Services.AddSingleton<TimeProvider>(new TickingClock(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero)));
        Services.AddSingleton(_store);
        Services.AddSingleton<FakeNotificationStore>();
        Services.AddScoped<IReviewService, FakeReviewService>();
    }

    private IReviewService Service => Services.GetRequiredService<IReviewService>();

    private static string Text(IRenderedComponent<ReviewPage> cut) =>
        System.Text.RegularExpressions.Regex.Replace(cut.Find("div").TextContent, @"\s+", " ");

    [Fact]
    public void The_store_sees_its_fortnight_its_checks_and_what_still_blocks_submitting()
    {
        var cut = Render<ReviewPage>();
        cut.WaitForAssertion(() => Assert.Contains("Northside (G101) · Mon 23 Nov – Sun 6 Dec 2026", cut.Find("p").TextContent));
        var text = Text(cut);
        Assert.Contains("Check the plan's flagged days, then submit it to j.morgan.", text);
        Assert.Contains("23 Nov – 6 Dec", text);
        Assert.Empty(cut.FindAll("select")); // a store login has no store picker
        var submit = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Submit to EMSO");
        Assert.True(submit.HasAttribute("disabled"));
        Assert.Contains("2 errors need a note", text);
        Assert.Contains("3 warnings to check", text);
        Assert.Contains("5 checks left.", text);
        Assert.Contains("Thursday 26 November", text);
    }

    [Fact]
    public void An_error_takes_a_note_before_it_counts_as_checked()
    {
        var cut = Render<ReviewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("li [role=switch]")));
        // The first check in the list: Thursday's error.
        cut.FindAll("li [role=switch]")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal("An error needs a note saying why it's OK.", cut.Find("li [role=alert]").TextContent));
        cut.Find("li textarea").Input("Cover booked from Riverside");
        cut.FindAll("li button").Single(b => b.TextContent.Trim() == "Mark checked").Click();
        cut.WaitForAssertion(() =>
        {
            var text = Text(cut);
            Assert.Contains("Your note", text);
            Assert.Contains("Cover booked from Riverside", text);
            Assert.Contains("4 checks left.", text);
        });
    }

    [Fact]
    public void A_day_opens_as_a_sheet_with_its_figures_and_checks()
    {
        var cut = Render<ReviewPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button")));
        cut.FindAll("button").First(b => b.TextContent.Contains("Open day")).Click();
        var sheet = cut.Find("aside[role=dialog]");
        Assert.Contains("Week 1 · day 4 of 14", sheet.TextContent);
        Assert.Equal("Thursday 26 November", sheet.QuerySelector("#review-day-title")!.TextContent);
        Assert.Contains("No manager rostered 20:00–21:00", sheet.TextContent);
        Assert.Contains("Note for the reviewer (needed for an error)", sheet.TextContent);
    }

    [Fact]
    public async Task The_reviewer_sees_its_stores_marks_days_fine_and_cannot_approve_until_all_14_are()
    {
        foreach (var c in (await Service.LoadPageAsync(null, PS)).Data!.AllChecks())
            Assert.True((await Service.CheckFlagAsync(FakeReviewStore.Northside, PS, c.Key, "ok")).Ok);
        Assert.True((await Service.SubmitReviewAsync(FakeReviewStore.Northside, PS)).Ok);
        _store.User = FakeReviewStore.UserFor("emso");

        var cut = Render<ReviewPage>();
        cut.WaitForAssertion(() => Assert.Contains("Your stores", Text(cut)));
        var text = Text(cut);
        Assert.Contains("1 of 3 ready for you", text);
        Assert.Contains("Submitted by sm.g101", text);
        Assert.NotEmpty(cut.FindAll("select")); // an EMSO picks between stores
        var approve = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Approve plan");
        Assert.True(approve.HasAttribute("disabled"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("unflagged days fine")).Click();
        cut.WaitForAssertion(() => Assert.Contains("10 of 14 days fine", Text(cut)));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Approve plan").HasAttribute("disabled"));
    }
}
