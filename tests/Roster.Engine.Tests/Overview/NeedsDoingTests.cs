// Port of src/lib/overview/needsDoing.test.ts.

using Roster.Engine.Overview;
using static Roster.Engine.Overview.NeedsDoing;

namespace Roster.Engine.Tests.Overview;

public class NeedsDoingTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    private static readonly NeedsLinks Links = new("/setup/requests?store=s1", "/plan/review?store=s1&period=2026-11-23", "/setup/staff?store=s1");
    private static NeedsCheck Err(bool done = false, bool hardStop = false) => new("error", done, hardStop);
    private static NeedsCheck Warn(bool done = false) => new("warning", done);

    private static readonly NeedsDoingInput Base = new(
        true,
        ReviewStatus.InProgress,
        [
            new(D("2026-11-23"), "Mon 23 Nov", [Warn(), Warn(true)]),
            new(D("2026-11-29"), "Sun 29 Nov", [Err(), Warn()]),
            new(D("2026-12-06"), "Sun 6 Dec", [Err(true), Err()]),
        ],
        [new("2026-11-01T22:14:00Z"), new("2026-11-03T05:40:00Z")],
        [new("Isla Scott", 45, 21)],
        Links);

    private static NeedsKind[] Kinds(NeedsDoingInput input) => Items(input).Select(i => i.Kind).ToArray();

    [Fact]
    public void Lists_each_kind_of_to_do_once_linked_to_where_it_is_done()
    {
        var items = Items(Base);
        Assert.Equal(
            [
                (NeedsKind.Requests, "2 day-off requests waiting", Links.Requests),
                (NeedsKind.Errors, "2 plan days with an error", Links.Review),
                (NeedsKind.Warnings, "2 warnings not checked yet", Links.Review),
                (NeedsKind.Contract, "Isla Scott's fixed roster pays above contract", Links.Team),
            ],
            items.Select(i => (i.Kind, i.Title, i.Href)));
        Assert.Equal(("Sun 29 Nov, Sun 6 Dec", ChipTone.Red, "Review"), (items[1].Detail, items[1].Tone, items[1].LinkLabel));
        Assert.Equal("45.0 h a fortnight against a 21 h contract", items[3].Detail);
    }

    [Fact]
    public void Drops_an_error_once_the_store_has_explained_it_with_a_note()
    {
        var days = Base.Days.Select(d => d with { Checks = d.Checks.Select(c => c with { Done = true }).ToList() }).ToList();
        Assert.Equal([NeedsKind.Requests, NeedsKind.Contract], Kinds(Base with { Days = days }));
    }

    [Fact]
    public void Keeps_a_hard_stop()
    {
        var days = new List<ReviewDayInput> { new(D("2026-11-24"), "Tue 24 Nov", [Err(true, true)]) };
        Assert.Equal("1 plan day with an error", Items(Base with { Days = days }).First(i => i.Kind == NeedsKind.Errors).Title);
    }

    [Fact]
    public void Lists_the_reviews_checks_only_while_the_review_is_with_the_store()
    {
        Assert.Contains(NeedsKind.Errors, Kinds(Base with { ReviewStatus = ReviewStatus.Returned }));
        foreach (var status in new[] { ReviewStatus.Submitted, ReviewStatus.Approved })
            Assert.Equal([NeedsKind.Requests, NeedsKind.Contract], Kinds(Base with { ReviewStatus = status }));
        Assert.Equal([NeedsKind.Requests, NeedsKind.Contract], Kinds(Base with { HasPlan = false }));
    }

    [Fact]
    public void Names_the_first_few_error_days_and_counts_the_rest()
    {
        var days = new[] { "Mon 23 Nov", "Tue 24 Nov", "Wed 25 Nov", "Thu 26 Nov", "Fri 27 Nov" }
            .Select((label, i) => new ReviewDayInput(D($"2026-11-2{3 + i}"), label, [Err()]))
            .ToList();
        Assert.Equal(
            "Mon 23 Nov, Tue 24 Nov, Wed 25 Nov and 2 more",
            Items(Base with { Days = days }).First(i => i.Kind == NeedsKind.Errors).Detail);
    }

    [Fact]
    public void Says_one_request_one_warning()
    {
        var items = Items(Base with
        {
            Requests = [new("2026-11-01T22:14:00Z")],
            Days = [new(D("2026-11-23"), "Mon 23 Nov", [Warn()])],
            OverContract = [],
        });
        Assert.Equal(["1 day-off request waiting", "1 warning not checked yet"], items.Select(i => i.Title));
    }

    [Fact]
    public void Is_empty_when_there_is_nothing_to_do()
    {
        Assert.Empty(Items(Base with { Requests = [], OverContract = [], ReviewStatus = ReviewStatus.Approved }));
    }

    // ── fixed rosters above contract (the Team Members rule) ──

    private static readonly ContractMember[] Employees =
    [
        new("a", "Isla Scott", "SA", 21),
        new("b", "Noah Brown", "SA", 76),
        new("j", "Mia Chen", "JSA", 0),
    ];

    // Three 9-5 shifts a week: 8 h less the SA's 30 unpaid minutes = 7.5 h each.
    private static List<FixedShiftRow> WeekOf(string id) =>
        Enumerable.Range(0, 3).Select(_ => new FixedShiftRow(id, "09:00:00", "17:00:00")).ToList();

    [Fact]
    public void Pays_each_shift_less_its_unpaid_break_scaled_to_the_fortnight()
    {
        Close.To(45, FixedPaidHours(WeekOf("a"), [], _ => "SA").Get("a"), 9);
    }

    [Fact]
    public void Averages_a_multi_week_cycle_over_its_weeks()
    {
        Close.To(22.5, FixedPaidHours(WeekOf("a"), [new("a", 2)], _ => "SA").Get("a"), 9);
    }

    [Fact]
    public void Flags_only_a_contracted_person_paid_more_than_their_contract()
    {
        var fixedShifts = WeekOf("a").Concat(WeekOf("b")).Concat(WeekOf("j")).ToList();
        Assert.Equal([new OverContract("Isla Scott", 45, 21)], FixedRostersAboveContract(Employees, fixedShifts, []));
    }

    [Fact]
    public void Allows_the_same_hundredth_of_an_hour_the_team_members_page_does()
    {
        Assert.Empty(FixedRostersAboveContract([new("a", "Isla Scott", "SA", 45)], WeekOf("a"), []));
    }
}
