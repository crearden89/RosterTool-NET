// Port of src/app/(app)/plan/review/checkList.test.ts.

using Roster.Engine.Planning;
using Roster.Web.Features.Review;

namespace Roster.Web.Tests.Review;

public class CheckListTests
{
    // A fortnight's worth of real flag wording, run through the review's own
    // check builder, so the list is tested on the checks the page really gets.
    private const string Err = "Cleaning 09:00–10:00: 0.25h of its 1.00h is worked in its hour";
    private const string Hard = "Only 1 person planned 06:00-07:00";
    private const string WarnA = "Longer shifts for the crew's breaks: 2.00 h added to existing shifts";
    private const string WarnB = "0.25h of shift time is held by the 3-hour minimum (15:45–16:00)";

    private static ReviewDayView Day(string date, string[] flags, string[]? done = null, DayVerdict? verdict = null, string? reply = null)
    {
        var d = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);
        done ??= [];
        return new ReviewDayView
        {
            Date = d,
            Checks = [.. ReviewChecks.SortChecks(ReviewChecks.FlagsToChecks(d, flags)).Select(c => new ReviewCheckView
            {
                Key = c.Key,
                Date = c.Date,
                Severity = c.Severity == Severity.Error ? CheckSeverity.Error : CheckSeverity.Warning,
                HardStop = c.HardStop,
                Message = c.Message,
                Detail = c.Detail,
                Done = done.Contains(c.Message),
                Note = done.Contains(c.Message) ? "ok" : null,
            })],
            Verdict = verdict,
            Reply = reply,
        };
    }

    private static List<ReviewDayView> Fortnight() =>
    [
        Day("2026-11-23", []),
        Day("2026-11-24", [WarnA], [WarnA]),
        Day("2026-11-25", [WarnB, Err]),
        Day("2026-11-26", []),
        Day("2026-11-27", [WarnA, WarnB, Hard], [WarnB]),
    ];

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ── the Review list's filters ──

    [Fact]
    public void Counts_every_check_under_each_filter() =>
        Assert.Equal(new FilterCounts(6, 2, 4, 4), CheckList.FilterCounts(Fortnight()));

    [Fact]
    public void A_hard_stop_is_an_error_and_stays_not_checked()
    {
        var hard = Fortnight()[4].Checks.First(c => c.HardStop);
        Assert.True(CheckList.MatchesFilter(hard, CheckFilter.Errors));
        Assert.False(CheckList.MatchesFilter(hard, CheckFilter.Warnings));
        Assert.True(CheckList.MatchesFilter(hard, CheckFilter.Unchecked));
    }

    [Fact]
    public void Groups_by_day_oldest_first_errors_on_top_and_leaves_out_days_with_nothing_to_show()
    {
        var all = CheckList.CheckGroups(Fortnight(), CheckFilter.All);
        Assert.Equal(["2026-11-24", "2026-11-25", "2026-11-27"], all.Select(g => Iso(g.Day.Date)));
        Assert.Equal([Err, WarnB], all[1].Checks.Select(c => c.Message));
        Assert.Equal(Hard, all[2].Checks[0].Message);

        var errors = CheckList.CheckGroups(Fortnight(), CheckFilter.Errors);
        Assert.Equal(["2026-11-25", "2026-11-27"], errors.Select(g => Iso(g.Day.Date)));
        Assert.All(errors.SelectMany(g => g.Checks), c => Assert.Equal(CheckSeverity.Error, c.Severity));

        var unchecked_ = CheckList.CheckGroups(Fortnight(), CheckFilter.Unchecked);
        Assert.Equal(["2026-11-25", "2026-11-27"], unchecked_.Select(g => Iso(g.Day.Date)));
        Assert.Equal([Err, WarnB, Hard, WarnA], unchecked_.SelectMany(g => g.Checks).Select(c => c.Message));
    }

    [Fact]
    public void The_counts_match_what_each_filter_lists()
    {
        var days = Fortnight();
        var counts = CheckList.FilterCounts(days);
        foreach (var f in new[] { CheckFilter.All, CheckFilter.Errors, CheckFilter.Warnings, CheckFilter.Unchecked })
            Assert.Equal(counts[f], CheckList.CheckGroups(days, f).Sum(g => g.Checks.Count));
    }

    [Fact]
    public void Shows_nothing_not_an_empty_day_when_a_filter_matches_no_check()
    {
        var days = new[] { Day("2026-11-24", [WarnA], [WarnA]) };
        Assert.Empty(CheckList.CheckGroups(days, CheckFilter.Unchecked));
        Assert.Empty(CheckList.CheckGroups(days, CheckFilter.Errors));
    }

    // ── the Review's progress counts ──

    [Fact]
    public void Counts_what_is_left_to_check()
    {
        var p = CheckList.ReviewProgress(Fortnight());
        Assert.Equal((6, 2, 4, 2, 4, 1, 1, 2), (p.Total, p.Done, p.Left, p.Errors, p.Warnings, p.HardStops, p.ErrorsToNote, p.WarningsToCheck));
    }

    [Fact]
    public void Counts_the_reviewers_days()
    {
        var days = new[]
        {
            Day("2026-11-23", [], [], DayVerdict.Ok),
            Day("2026-11-24", [WarnA], [WarnA], DayVerdict.Query, "Fixed the break"),
            Day("2026-11-25", [Err], [Err], DayVerdict.Query),
            Day("2026-11-26", []),
            Day("2026-11-27", [WarnB], [WarnB]),
        };
        var p = CheckList.ReviewProgress(days);
        Assert.Equal((1, 2, 1, 1), (p.OkDays, p.Queried, p.Answered, p.UnflaggedTodo));
    }

    // ── what still blocks submitting ──

    [Fact]
    public void Names_each_thing_in_words() =>
        Assert.Equal(
            ["1 hard stop to fix in the plan", "1 error needs a note", "2 warnings to check"],
            CheckList.SubmitBlockers(CheckList.ReviewProgress(Fortnight()), true, ReviewStatus.InProgress));

    [Fact]
    public void Asks_for_the_plan_first_when_there_is_none() =>
        Assert.Equal(["Generate the plan first"], CheckList.SubmitBlockers(CheckList.ReviewProgress([]), false, ReviewStatus.InProgress));

    [Fact]
    public void Asks_for_the_answers_on_a_returned_plan()
    {
        var days = new[] { Day("2026-11-24", [WarnA], [WarnA], DayVerdict.Query), Day("2026-11-25", [Err], [Err], DayVerdict.Query, "Done") };
        Assert.Equal(["1 query to answer"], CheckList.SubmitBlockers(CheckList.ReviewProgress(days), true, ReviewStatus.Returned));
        // A query only blocks a returned plan, as the submit action has it.
        Assert.Empty(CheckList.SubmitBlockers(CheckList.ReviewProgress(days), true, ReviewStatus.InProgress));
    }

    [Fact]
    public void Is_empty_exactly_when_the_page_lets_the_store_submit()
    {
        var cases = new (ReviewDayView[] Days, ReviewStatus Status, bool HasSnapshot)[]
        {
            ([.. Fortnight()], ReviewStatus.InProgress, true),
            ([Day("2026-11-24", [WarnA, Err], [WarnA, Err])], ReviewStatus.InProgress, true),
            ([Day("2026-11-24", [Hard], [])], ReviewStatus.InProgress, true),
            ([Day("2026-11-24", [WarnA], [WarnA], DayVerdict.Query)], ReviewStatus.Returned, true),
            ([Day("2026-11-24", [WarnA], [WarnA], DayVerdict.Query, "Answered")], ReviewStatus.Returned, true),
            ([Day("2026-11-24", [], [])], ReviewStatus.Submitted, true),
            ([Day("2026-11-24", [], [])], ReviewStatus.InProgress, false),
        };
        foreach (var (days, status, hasSnapshot) in cases)
        {
            var p = CheckList.ReviewProgress(days);
            // The page's own rule (ReviewView): every flag checked, and on a
            // returned plan every query answered.
            var ready = hasSnapshot && p.Done == p.Total && (status != ReviewStatus.Returned || p.Answered == p.Queried);
            Assert.Equal(ready, CheckList.SubmitBlockers(p, hasSnapshot, status).Count == 0);
        }
    }

    // ── the day sheet's position ──

    [Fact]
    public void Names_the_week_and_the_day()
    {
        Assert.Equal("Week 1 · day 1 of 14", CheckList.DayPosition(0));
        Assert.Equal("Week 1 · day 7 of 14", CheckList.DayPosition(6));
        Assert.Equal("Week 2 · day 8 of 14", CheckList.DayPosition(7));
        Assert.Equal("Week 2 · day 14 of 14", CheckList.DayPosition(13));
    }
}
