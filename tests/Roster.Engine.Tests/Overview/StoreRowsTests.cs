// Port of src/lib/overview/storeRows.test.ts.

using Roster.Engine.Overview;
using static Roster.Engine.Overview.Metrics;
using static Roster.Engine.Overview.StoreRows;

namespace Roster.Engine.Tests.Overview;

public class StoreRowsTests
{
    private static bool?[] Ok14(params int[] bad) => Enumerable.Range(0, 14).Select(i => (bool?)!bad.Contains(i)).ToArray();

    private static readonly StoreStatusInput Base = new(
        new HeldPlanStatus(3, Ok14(6, 13)),
        new StoreReviewRow("approved", "plan:3"),
        14,
        14,
        true);

    private static (string, string, string, int?) Labels(StoreStatusInput s)
    {
        var r = StoreStatusOf(s);
        return (r.Plan.Label, r.Review.Label, r.Roster.Label, r.ErrorDays);
    }

    [Fact]
    public void Reads_a_finished_fortnight()
    {
        Assert.Equal(("Generated", "Approved", "Published", 2), Labels(Base));
    }

    [Fact]
    public void Counts_the_plan_days_not_ok()
    {
        Assert.Equal(2, StoreStatusOf(Base with { Plan = new HeldPlanStatus(3, [true, false, null, false]) }).ErrorDays);
    }

    [Fact]
    public void Has_nothing_to_show_before_a_plan_is_held()
    {
        Assert.Equal(("Not generated", "-", "-", (int?)null), Labels(Base with { Plan = null, Review = null, PublishedDays = 0, RosterBuilt = false }));
    }

    [Fact]
    public void Follows_the_review()
    {
        (string, string) At(string status, string key = "plan:3")
        {
            var (_, review, roster, _) = Labels(Base with { Review = new StoreReviewRow(status, key), PublishedDays = 0, RosterBuilt = false });
            return (review, roster);
        }
        Assert.Equal(("Not submitted", "Waiting"), At("in_progress"));
        Assert.Equal(("Submitted", "Waiting"), At("submitted"));
        Assert.Equal(("Returned", "Waiting"), At("returned"));
        Assert.Equal(("Approved", "To build"), At("approved"));
        Assert.Equal("Not published", Labels(Base with { PublishedDays = 0 }).Item3);
    }

    [Fact]
    public void Doesnt_count_an_approval_or_a_submission_made_on_an_earlier_plan()
    {
        Assert.Equal(ReviewState.InProgress, ReviewStateOf(new StoreReviewRow("approved", "plan:2"), Base.Plan));
        Assert.Equal(ReviewState.Stale, ReviewStateOf(new StoreReviewRow("submitted", "plan:2"), Base.Plan));
        Assert.Equal(ReviewState.NotStarted, ReviewStateOf(null, Base.Plan));
        Assert.Equal("Plan changed", StoreStatusOf(Base with { Review = new StoreReviewRow("submitted", "plan:2"), PublishedDays = 0 }).Review.Label);
    }

    [Fact]
    public void Is_published_only_when_every_day_of_the_fortnight_is()
    {
        Assert.Equal("Not published", Labels(Base with { PublishedDays = 13 }).Item3);
        Assert.Equal("Not published", Labels(Base with { FortnightDays = 0, PublishedDays = 0 }).Item3);
    }

    // ── efficiency month to date, store by store ──

    [Fact]
    public void Is_the_dashboards_oe_for_each_store_on_its_own()
    {
        StoreActualsRow[] actuals = [new("a", 47.8764, 53.8), new("a", 40, 44), new("b", 30, 40)];
        StoreNonOpRow[] nonOp = [new("a", 18), new("b", null)];
        var oe = OeByStore(actuals, nonOp, 4.5);
        // The per-date sums the Dashboard pools, divided once.
        Close.To(OperationalEfficiencyPct(WithPaidBreaks(47.8764, 4.5) + WithPaidBreaks(40, 4.5), 97.8, 0.3)!.Value, oe.Get("a"), 9);
        Close.To(OperationalEfficiencyPct(WithPaidBreaks(30, 4.5), 40, 0)!.Value, oe.Get("b"), 9);
    }

    [Fact]
    public void Has_no_figure_for_a_store_without_hours()
    {
        var oe = OeByStore([new("a", null, null)], [new("c", 30)], 4.5);
        Assert.Null(oe.Get("a"));
        Assert.Null(oe.Get("c"));
        Assert.False(oe.Has("z"));
    }
}
