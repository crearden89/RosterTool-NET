// New: the Review page's query handling and the plan review state, as page.tsx and
// PlanReviewStatus.tsx have them (V8's reading of ?period=, the header's dates).

using Roster.Web.Features.Review;

namespace Roster.Web.Tests.Review;

public class ReviewQueryTests
{
    private static readonly DateOnly Today = new(2026, 11, 10); // planning: 23 Nov

    [Theory]
    [InlineData("2026-11-23", "2026-11-23")]
    [InlineData("2026-11-30", "2026-11-23")]
    [InlineData("2026-02-30", "2026-03-02")] // V8 reads 2 March, itself a fortnight start
    [InlineData("2026-13-01", "2026-11-23")] // not a date: the fortnight being planned
    [InlineData("2026-1-01", "2026-11-23")]
    [InlineData("2026-11-23\n", "2026-11-23")]
    [InlineData("", "2026-11-23")]
    [InlineData(null, "2026-11-23")]
    public void Opens_the_fortnight_a_link_names_or_the_one_being_planned(string? period, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ReviewQuery.PeriodStartFrom(period, Today));

    [Fact]
    public void Writes_the_header_span_with_the_short_months_en_AU_uses()
    {
        Assert.Equal("23 Nov – 6 Dec", ReviewQuery.ShortSpan(new DateOnly(2026, 11, 23)));
        Assert.Equal("1 June – 14 June", ReviewQuery.ShortSpan(new DateOnly(2026, 6, 1)));
        Assert.Equal("31 Aug – 13 Sept", ReviewQuery.ShortSpan(new DateOnly(2026, 8, 31)));
    }

    [Fact]
    public void Links_between_fortnights_keep_the_store()
    {
        var store = Guid.Parse("50000000-0000-4000-8000-000000000101");
        Assert.Equal($"/plan/review?store={store}&period=2026-12-07", ReviewQuery.Href(store, new DateOnly(2026, 12, 7)));
        Assert.Equal("/plan/review?period=2026-12-07", ReviewQuery.Href(null, new DateOnly(2026, 12, 7)));
    }

    [Fact]
    public void An_approval_holds_only_for_the_plan_it_was_made_on()
    {
        Assert.Equal(PlanReviewState.Approved, PlanReviewStatus.PlanReviewState("approved", "plan:3", 3));
        Assert.Equal(PlanReviewState.NotSubmitted, PlanReviewStatus.PlanReviewState("approved", "plan:3", 4));
        // planKey(null) is "plan:0", as the TypeScript has it.
        Assert.Equal(PlanReviewState.Approved, PlanReviewStatus.PlanReviewState("approved", "plan:0", null));
        Assert.Equal(PlanReviewState.Submitted, PlanReviewStatus.PlanReviewState("submitted", "plan:1", 2));
        Assert.Equal(PlanReviewState.Returned, PlanReviewStatus.PlanReviewState("returned", null, 1));
        Assert.Equal(PlanReviewState.NotSubmitted, PlanReviewStatus.PlanReviewState("in_progress", "plan:1", 1));
        Assert.Equal(PlanReviewState.NotSubmitted, PlanReviewStatus.PlanReviewState(null, null, null));
    }
}
