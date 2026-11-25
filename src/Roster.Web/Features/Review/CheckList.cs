// Port of src/app/(app)/plan/review/checkList.ts.
// The Review page's list of checks: the fortnight's flags as one list, by day,
// with the filters (All, Errors, Warnings, Not checked), the progress counts
// and what still blocks submitting. Pure - presentation only. The checks, who
// may act on them and every rule about submitting stay where they are (the
// review service and the data layer): the blockers here say in words what the
// submit step itself refuses.

namespace Roster.Web.Features.Review;

/// <summary>Which checks the list shows (CheckFilter).</summary>
public enum CheckFilter
{
    All,
    Errors,
    Warnings,
    Unchecked,
}

/// <summary>How many of the fortnight's checks each filter shows.</summary>
public sealed record FilterCounts(int All, int Errors, int Warnings, int Unchecked)
{
    public int this[CheckFilter filter] => filter switch
    {
        CheckFilter.Errors => Errors,
        CheckFilter.Warnings => Warnings,
        CheckFilter.Unchecked => Unchecked,
        _ => All,
    };
}

/// <summary>One day of the list under a filter, with the checks that match.</summary>
public sealed record CheckGroup(ReviewDayView Day, IReadOnlyList<ReviewCheckView> Checks);

/// <summary>The review's progress counts (ReviewProgress).</summary>
/// <param name="Total">Every check in the fortnight.</param>
/// <param name="Done">Checked by the store.</param>
/// <param name="Left">Not checked yet (hard stops included: they can't be checked).</param>
/// <param name="ErrorsToNote">Errors (not hard stops) still waiting for their note.</param>
/// <param name="OkDays">Days the reviewer marked fine.</param>
/// <param name="Queried">Days the reviewer queried.</param>
/// <param name="Answered">Queried days the store has replied on.</param>
/// <param name="UnflaggedTodo">Days with nothing flagged and no decision yet.</param>
public sealed record ReviewProgress(
    int Total,
    int Done,
    int Left,
    int Errors,
    int Warnings,
    int HardStops,
    int ErrorsToNote,
    int WarningsToCheck,
    int OkDays,
    int Queried,
    int Answered,
    int UnflaggedTodo);

public static class CheckList
{
    /// <summary>CHECK_FILTERS: the filter chips, in order.</summary>
    public static IReadOnlyList<(CheckFilter Key, string Label)> CheckFilters { get; } =
    [
        (CheckFilter.All, "All"),
        (CheckFilter.Errors, "Errors"),
        (CheckFilter.Warnings, "Warnings"),
        (CheckFilter.Unchecked, "Not checked"),
    ];

    public static bool MatchesFilter(ReviewCheckView c, CheckFilter f) => f switch
    {
        CheckFilter.Errors => c.Severity == CheckSeverity.Error,
        CheckFilter.Warnings => c.Severity == CheckSeverity.Warning,
        CheckFilter.Unchecked => !c.Done,
        _ => true,
    };

    /// <summary>How many of the fortnight's checks each filter shows.</summary>
    public static FilterCounts FilterCounts(IEnumerable<ReviewDayView> days)
    {
        var all = days.SelectMany(d => d.Checks).ToList();
        return new FilterCounts(
            all.Count,
            all.Count(c => MatchesFilter(c, CheckFilter.Errors)),
            all.Count(c => MatchesFilter(c, CheckFilter.Warnings)),
            all.Count(c => MatchesFilter(c, CheckFilter.Unchecked)));
    }

    /// <summary>
    /// The list under one filter: the fortnight's days in order (oldest first),
    /// each with the checks that match, in the day's own order - errors first, as
    /// the review sorts them. A day with nothing to show is left out.
    /// </summary>
    public static List<CheckGroup> CheckGroups(IEnumerable<ReviewDayView> days, CheckFilter f) =>
        days
            .Select(day => new CheckGroup(day, day.Checks.Where(c => MatchesFilter(c, f)).ToList()))
            .Where(g => g.Checks.Count > 0)
            .ToList();

    public static ReviewProgress ReviewProgress(IEnumerable<ReviewDayView> days)
    {
        var dayList = days.ToList();
        var all = dayList.SelectMany(d => d.Checks).ToList();
        var errors = all.Where(c => c.Severity == CheckSeverity.Error).ToList();
        var queried = dayList.Where(d => d.Verdict == DayVerdict.Query).ToList();
        var done = all.Count(c => c.Done);
        return new ReviewProgress(
            Total: all.Count,
            Done: done,
            Left: all.Count - done,
            Errors: errors.Count,
            Warnings: all.Count - errors.Count,
            HardStops: all.Count(c => c.HardStop),
            ErrorsToNote: errors.Count(c => !c.HardStop && !c.Done),
            WarningsToCheck: all.Count(c => c.Severity == CheckSeverity.Warning && !c.Done),
            OkDays: dayList.Count(d => d.Verdict == DayVerdict.Ok),
            Queried: queried.Count,
            // A reply counts when it says something (an empty one is falsy in the TypeScript).
            Answered: queried.Count(d => !string.IsNullOrEmpty(d.Reply)),
            UnflaggedTodo: dayList.Count(d => d.Checks.Count == 0 && d.Verdict is null));
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>
    /// What still stands between the store and submitting, in the order it is
    /// worth doing: the same conditions the submit step refuses on (no plan, a
    /// hard stop, a flag not checked, a query not answered on a returned plan).
    /// Empty = the store can submit.
    /// </summary>
    public static List<string> SubmitBlockers(ReviewProgress p, bool hasSnapshot, ReviewStatus status)
    {
        if (!hasSnapshot) return ["Generate the plan first"];
        var output = new List<string>();
        if (p.HardStops > 0) output.Add($"{Plural(p.HardStops, "hard stop", "hard stops")} to fix in the plan");
        if (p.ErrorsToNote > 0) output.Add($"{Plural(p.ErrorsToNote, "error needs", "errors need")} a note");
        if (p.WarningsToCheck > 0) output.Add($"{Plural(p.WarningsToCheck, "warning", "warnings")} to check");
        if (status == ReviewStatus.Returned && p.Answered < p.Queried)
            output.Add($"{Plural(p.Queried - p.Answered, "query", "queries")} to answer");
        return output;
    }

    /// <summary>"Week 1 · day 7 of 14" for the day sheet's heading.</summary>
    public static string DayPosition(int index, int of = 14) => $"Week {(index < 7 ? 1 : 2)} · day {index + 1} of {of}";
}
