// Port of src/lib/roster/dayStatus.ts.
// ─── A roster day's status: errors, warnings and the cover note ──────────────
// The Roster page grades a day's flags the way the Review grades the plan's
// (ReviewChecks.SeverityOf): an ERROR is missing work or cover that
// someone has to act on or explain - an open shift, a till or a manager gap;
// everything else is a warning or a note. One reading drives the date pills
// (red only for an error), the flags panel (errors, then warnings) and
// publishing (a note on cover is asked for only when a day has an error).
// Pure.

using Roster.Engine.Core;
using Roster.Engine.Planning;

namespace Roster.Engine.RosterOps;

/// <summary>What the status reads of a roster day (RosterComputation's shifts, flags, hasData and closed).</summary>
public sealed record RosterDay(IReadOnlyList<Shift> Shifts, IReadOnlyList<string> Flags, bool HasData, bool Closed);

/// <summary>A day's flags split into errors (with the open shifts' "Why:" line) and warnings.</summary>
public sealed record RosterFlagSplit(List<string> Errors, List<string> Warnings);

public static class DayStatus
{
    // The engine's one-line explanation under a day's open shifts (/^Why:\s*/).
    private static bool IsWhy(string f) => f.StartsWith("Why:", StringComparison.Ordinal);

    /// <summary>The day's flags split into errors (with the open shifts' "Why:" line) and warnings.</summary>
    public static RosterFlagSplit SplitRosterFlags(IEnumerable<string> flags)
    {
        var all = flags.ToList();
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var f in all)
        {
            if (IsWhy(f)) continue;
            (ReviewChecks.SeverityOf(f) == Severity.Error ? errors : warnings).Add(f);
        }
        // The "Why:" line explains the open shifts, so it reads with the errors.
        var why = all.Where(IsWhy).ToList();
        if (why.Count > 0) (errors.Count > 0 ? errors : warnings).AddRange(why);
        return new RosterFlagSplit(errors, warnings);
    }

    /// <summary>An open shift: a slot nobody could legally take.</summary>
    public static int OpenShiftCount(RosterDay? day) => day?.Shifts.Count(s => s.Unfilled) ?? 0;

    /// <summary>True when the day has an error: an open shift, or a flag graded as an error.</summary>
    public static bool RosterDayHasError(RosterDay? day)
    {
        if (day is null || day.Closed || !day.HasData) return false;
        return OpenShiftCount(day) > 0 || day.Flags.Any(f => !IsWhy(f) && ReviewChecks.SeverityOf(f) == Severity.Error);
    }

    /// <summary>The date pill: green when the day is closed or rostered without an error.</summary>
    public static bool RosterDayOk(RosterDay? day)
    {
        if (day is null) return false;
        if (day.Closed) return true;
        return day.HasData && !RosterDayHasError(day);
    }

    /// <summary>The days of the fortnight whose errors need a note on cover before the roster is published.</summary>
    public static List<T> DaysNeedingCoverNote<T>(Func<T, RosterDay?> dayOf, IEnumerable<T> dates) =>
        dates.Where(d => RosterDayHasError(dayOf(d))).ToList();

    /// <inheritdoc cref="DaysNeedingCoverNote{T}(Func{T, RosterDay?}, IEnumerable{T})"/>
    public static List<T> DaysNeedingCoverNote<T>(JsMap<T, RosterDay> byDate, IEnumerable<T> dates) where T : notnull =>
        DaysNeedingCoverNote(d => byDate.Get(d), dates);

    /// <summary>Plain words for why the note is asked for, naming the days (dd/mm).</summary>
    public static string CoverNoteReason(IReadOnlyList<DateOnly> days) => CoverNoteReason(days.Select(d => d.ToIso()).ToList());

    /// <inheritdoc cref="CoverNoteReason(IReadOnlyList{DateOnly})"/>
    public static string CoverNoteReason(IReadOnlyList<string> days)
    {
        var shown = days.Select(d => $"{d.Substring(8, 2)}/{d.Substring(5, 2)}").ToList();
        var list = shown.Count > 4 ? $"{string.Join(", ", shown.Take(4))} and {shown.Count - 4} more" : string.Join(", ", shown);
        return $"{(days.Count == 1 ? "1 day has" : $"{days.Count} days have")} an error (open shifts or cover gaps): {list}";
    }
}
