// Port of src/lib/review/checks.test.ts.

using Roster.Engine.Planning;

namespace Roster.Engine.Tests.Planning;

public class ReviewChecksTests
{
    private static readonly DateOnly D = new(2026, 11, 19);

    private static void AllAre(Severity want, params string[] flags)
    {
        foreach (var f in flags) Assert.True(ReviewChecks.SeverityOf(f) == want, f);
    }

    [Fact]
    public void Calls_missing_work_or_cover_an_error_and_everything_else_a_warning()
    {
        AllAre(Severity.Error,
            "No manager available to cover 16:00–21:30 - external cover may be required",
            "Understaffed 06:00–10:00 - external cover may be required",
            "Load Running: 1.25h of today's plan is on nobody's shift - no one was free inside its window to make up the work breaks displaced",
            "Tills: 0.50h of register time has nobody on it - no one could take the till while the crew took its breaks",
            "Manager gap 13:00–14:00 - the day is longer than two max-length manager shifts can cover",
            "Only 1 person planned 20:00–21:00 - below the 2-person floor (check the benchmark demand)",
            "reset: 0.75h could not be placed - its allowed window falls outside the staffed day (adjust the window in Labour Rules or the trading hours)",
            "No tills configured: MCO Registers and SCO Lanes are both 0 on the Store Attributes screen, so checkout cannot be planned");
        AllAre(Severity.Warning,
            "2.00h added so the tills stay manned while the crew takes its breaks",
            "Very short day - both manager shifts cover the whole span",
            "Kim Arden is 3.50h under contract this fortnight",
            "Taylor Brook works 7 days in a row (16/11 - 22/11) - above the 6-day run warning",
            // An hours issue is shown, not a rule broken (owner decisions 1 and R2).
            "Admin hours issue: 1.50h over the benchmark (0.50h) - the Admin tasks due today need more than it holds");
    }

    [Fact]
    public void Keys_a_check_on_its_day_and_wording_so_the_same_flag_keeps_its_check()
    {
        var a = ReviewChecks.FlagsToChecks(D, ["Very short day - both manager shifts cover the whole span"])[0];
        var b = ReviewChecks.FlagsToChecks(D, ["Very short day - both manager shifts cover the whole span"])[0];
        Assert.Equal(b.Key, a.Key);
        Assert.NotEqual(ReviewChecks.CheckKey(new DateOnly(2026, 11, 20), a.Message), a.Key);
        Assert.NotEqual(ReviewChecks.CheckKey(D, "2.00h added so the tills stay manned while the crew takes its breaks"), a.Key);
        Assert.StartsWith("fortnight:", ReviewChecks.CheckKey(null, "x"));
        // FNV-1a over UTF-16 code units, as the TypeScript hashes them.
        Assert.Equal("fortnight:fd0c5087", ReviewChecks.CheckKey(null, "  x ")); // fnv1a("x") in Node
    }

    [Fact]
    public void Turns_the_rosters_Why_line_into_the_open_shifts_explanation_not_a_check()
    {
        var checks = ReviewChecks.FlagsToChecks(D,
        [
            "Understaffed 06:00–10:00 - external cover may be required",
            "Why: everyone available is already rostered or over their weekly hours",
            "Understaffed 06:00–10:00 - external cover may be required",
            "2.00h added so the tills stay manned while the crew takes its breaks",
        ]);
        Assert.Equal([Severity.Error, Severity.Warning], checks.Select(c => c.Severity));
        Assert.Equal("everyone available is already rostered or over their weekly hours", checks[0].Detail);
        Assert.Null(checks[1].Detail);
    }

    [Fact]
    public void Grades_the_day_checkers_findings()
    {
        AllAre(Severity.Error,
            "No manager rostered 20:00–20:30",
            "Fewer than 2 people rostered 06:00–06:15 - someone works alone",
            "Junior rostered with fewer than 3 adults 18:00–19:00",
            "Store assistant 09:00–17:00: Admin at 10:00–10:30 - only managers may do Admin",
            "Junior 16:00–20:00: on the tills at 17:00–17:15 - juniors may not work a till",
            "Store assistant 06:00–09:00: Load running at 06:00–06:30 is outside its window (06:30–10:00)",
            "Store assistant 09:00–11:00 is under the 3-hour minimum",
            "Store assistant 06:00–17:00 is longer than the 9.50-hour maximum",
            "Junior 15:00–20:00 starts before 16:00 on a school day",
            "Monday date checks: 90 of 120 min done by 08:30",
            "Planned hours are 2.50 h under the benchmark (40.00 h planned, 42.50 h benchmark)",
            "Planned hours are 3.00 h over the benchmark (45.00 h planned, 42.00 h benchmark) - 2.00 h of it is not explained by an hours issue, cover or the 3-hour minimum");
        AllAre(Severity.Warning,
            "Hours issue: 2.25 h over the benchmark (44.25 h planned, 42.00 h benchmark) - cover 1.25 h, 3-hour minimums 1.00 h",
            "1.25h of shift time is held for cover - nobody alone, a manager on, three adults with a junior (20:00–20:30)",
            "0.50h of shift time is held by the 3-hour minimum (13:00–13:30)",
            "Store assistant 07:00–14:00: 2 quarter-hours with no work (11:00–11:30)",
            // Work wins over the 18:00 preference (decision 16): a warning.
            "Store assistant 13:00–20:30: break ends 18:30 - it could have been taken by 18:00",
            "Till demand above the store's 5 registers: 5.75h of register time not planned (11:00–14:00)",
            "Extra junior 07:00-10:00 so the crew can take its breaks without losing the work - 1.00h of award break falls inside a window that closes at 10:00",
            // Time given to breaks, in the wording of 14/11/2026: notes, never errors.
            "Extra store assistant for the crew's breaks: 07:00–10:00, because 1.00 h of breaks fall in work that has to be finished by 10:00",
            "2 extra store assistants for the crew's breaks: both 06:00–09:00, because 3.25 h of breaks fall in work that has to be finished by 09:00",
            "Longer shifts for the crew's breaks: 0.75 h added to existing shifts, because breaks fall in work that has to be finished in its window",
            "Longer shifts for the crew's breaks at the tills: 0.50 h added so the tills stay manned while people take their breaks",
            "Longer shift so a break doesn't cost work: 0.25 h - Manager now 11:15–17:30 (starts 15 minutes earlier), so the work a break would push out is still done in its window",
            "Longer shift on the roster so a break doesn't cost work: 0.25 h - Sam now 07:00–12:15 (finishes 15 minutes later), so the work a break would push out is still done in its window",
            // A shift cut so its edge work fills time a shorter break left empty.
            "Shorter shift on the roster so a break leaves no time empty: 0.50 h - Sam now 13:30–17:00 (finishes 30 minutes earlier); Alex does that work (Reset) at 13:45–14:15, where the plan priced a break Alex is not owed",
            "Shorter shifts on the roster so breaks leave no time empty: 0.75 h - Sam now 14:00–17:30 (starts 30 minutes later, break 30 minutes later); Kim now 09:00–15:45 (finishes 15 minutes earlier); Alex does that work (Specials, Reset) at 13:45–14:15, where the plan priced a break Alex is not owed; Jo does that work (Reset) at 11:00–11:15, where the plan priced a break Jo is not owed");
    }

    [Fact]
    public void Grades_the_rules_phases_6_and_7_now_keep_as_errors()
    {
        AllAre(Severity.Error,
            "Cleaning 09:00–10:00: 0.50h of its 1.00h is worked in its hour",
            "Manager handover is 45 minutes (13:15–14:00) - the rule is at least an hour",
            "Managers do not overlap - no handover (the rule is at least an hour)",
            "Manager 14:00–19:00 is under the 6-hour minimum for a manager",
            "Monday date checks (with Register Assignment): 120 of 135 min done by 08:30");
        // JavaScript's $ matches only at the very end; .NET's would also match before a final newline.
        Assert.Equal(Severity.Warning, ReviewChecks.SeverityOf("Cleaning 09:00–10:00: 0.50h of its 1.00h is worked in its hour\n"));
    }

    [Fact]
    public void Lists_errors_before_warnings()
    {
        var checks = ReviewChecks.FlagsToChecks(D,
        [
            "Very short day - both manager shifts cover the whole span",
            "Understaffed 06:00–10:00 - external cover may be required",
        ]);
        Assert.Equal([Severity.Error, Severity.Warning], ReviewChecks.SortChecks(checks).Select(c => c.Severity));
    }

    [Fact]
    public void Hard_stops_are_the_legal_breaches_a_note_cannot_clear()
    {
        var checks = ReviewChecks.FlagsToChecks(null, ["Fewer than 2 people rostered 06:00–06:15 - someone works alone", "No manager rostered 20:00–20:30"]);
        Assert.Equal([true, false], checks.Select(c => c.HardStop));
    }
}
