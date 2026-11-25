// Port of src/lib/walk/notes.ts.
// ─── Store walk notes (pure) ─────────────────────────────────────────────────
// Things spotted on a walk of the store, captured on the kiosk and worked off
// there. Notes carry over until completed, so the only ordering that matters is
// "what has been sitting longest" - see SortOpen below.

using Roster.Engine.Core;

namespace Roster.Engine.Walk;

public sealed record WalkNote(
    string Id,
    string Note,
    // Null once the assignee leaves - AssignedName still carries the label.
    string? AssignedEmployeeId,
    string? AssignedName,
    // Store-local date the note was raised.
    DateOnly RaisedDate,
    DateTimeOffset? CompletedAt);

/// <summary>
/// <see cref="Notes.CleanNote"/>'s answer: the cleaned <see cref="Text"/>, or an
/// <see cref="Error"/> to show against the field (exactly one is set).
/// </summary>
public sealed record CleanedNote(string? Text, string? Error)
{
    public bool Ok => Error is null;

    public static CleanedNote Valid(string text) => new(text, null);
    public static CleanedNote Invalid(string error) => new(null, error);
}

public sealed record NotesSummary(
    int Raised,
    int Completed,
    int Open,
    // Median days to close across COMPLETED notes; null when none are closed.
    double? MedianDaysToClose,
    // Longest a still-open note has been outstanding; null when none are open.
    int? OldestOpenDays);

public static class Notes
{
    /// <summary>Matches the column check in migration 0034.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>
    /// Trim and validate a typed note. Returns the cleaned text, or an error to show
    /// against the field.
    ///
    /// Whitespace-only is rejected rather than stored: on a tablet it is easy to
    /// fat-finger the space bar and "save" an invisible note that then sits in the
    /// open list forever with nothing to read.
    /// </summary>
    public static CleanedNote CleanNote(string raw)
    {
        var text = JsTrim(raw);
        if (text.Length == 0) return CleanedNote.Invalid("Type a note first.");
        if (text.Length > MaxNoteLength)
            return CleanedNote.Invalid($"Keep it under {MaxNoteLength} characters (currently {text.Length}).");
        return CleanedNote.Valid(text);
    }

    /// <summary>Whole days since a note was raised. 0 = raised today.</summary>
    public static int AgeInDays(DateOnly raisedDate, DateOnly today) =>
        Math.Max(0, today.DayNumber - raisedDate.DayNumber);

    /// <summary>"today", "yesterday", "3 days ago" - how long this has been outstanding.</summary>
    public static string AgeLabel(DateOnly raisedDate, DateOnly today)
    {
        var days = AgeInDays(raisedDate, today);
        if (days == 0) return "today";
        if (days == 1) return "yesterday";
        return $"{days} days ago";
    }

    /// <summary>
    /// Open notes, OLDEST FIRST. The point of the list is what still is not done, so
    /// the thing that has been deferred longest belongs at the top where it gets
    /// looked at - a newest-first list buries exactly the note that needs chasing.
    /// Ties break on creation order so the list never reshuffles between renders.
    /// </summary>
    public static List<WalkNote> SortOpen(IEnumerable<WalkNote> notes) =>
        Js.Sorted(
            notes.Where(n => n.CompletedAt is null),
            // ISO dates (yyyy-mm-dd) compare as strings in date order, so the
            // TypeScript's raisedDate.localeCompare is the date comparison.
            (a, b) => ((double)a.RaisedDate.CompareTo(b.RaisedDate)).Or(Js.LocaleCompare(a.Id, b.Id)));

    // ─── History / analysis ──────────────────────────────────────────────────

    /// <summary>
    /// Calendar days from raising a note to completing it. 0 = done the same day.
    /// Null while it is still open - an unfinished note has no close time, and
    /// counting it as 0 would flatter the numbers below.
    ///
    /// Measured on the DATE, not the timestamp: a note raised Monday morning and
    /// closed Tuesday afternoon took one day, not 1.2. The completion date is the
    /// timestamp's UTC date, as the TypeScript reads it (completedAt.slice(0, 10)
    /// of the stored ISO timestamp).
    /// </summary>
    public static int? DaysToClose(DateOnly raisedDate, DateTimeOffset? completedAt)
    {
        if (completedAt is not { } done) return null;
        return AgeInDays(raisedDate, CompletedDate(done));
    }

    /// <summary>The date a completion timestamp counts on: its UTC date.</summary>
    public static DateOnly CompletedDate(DateTimeOffset completedAt) =>
        DateOnly.FromDateTime(completedAt.UtcDateTime);

    /// <summary>
    /// Headline figures for a set of notes.
    ///
    /// The close time is a MEDIAN rather than a mean on purpose: one note that sat
    /// for six months would drag an average far enough to hide that the store
    /// normally closes things within a day.
    /// </summary>
    public static NotesSummary SummariseNotes(IReadOnlyList<WalkNote> notes, DateOnly today)
    {
        var closed = Js.Sorted(
            notes.Select(n => DaysToClose(n.RaisedDate, n.CompletedAt)).Where(d => d is not null).Select(d => d!.Value),
            (a, b) => a - b);

        var openAges = notes
            .Where(n => n.CompletedAt is null)
            .Select(n => AgeInDays(n.RaisedDate, today))
            .ToList();

        double? median = null;
        if (closed.Count > 0)
        {
            var mid = closed.Count / 2;
            median = closed.Count % 2 == 0 ? (closed[mid - 1] + closed[mid]) / 2.0 : closed[mid];
        }

        return new NotesSummary(
            Raised: notes.Count,
            Completed: closed.Count,
            Open: openAges.Count,
            MedianDaysToClose: median,
            OldestOpenDays: openAges.Count > 0 ? openAges.Max() : null);
    }

    // JavaScript's String.prototype.trim: WhiteSpace (tab, VT, FF, ZWNBSP and
    // every Space_Separator) and LineTerminator (LF, CR, LS, PS). .NET's Trim()
    // differs on two characters (it trims U+0085 and keeps U+FEFF).
    private static readonly char[] JsWhiteSpace = new[]
    {
        0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x00A0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007,
        0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
    }.Select(c => (char)c).ToArray();

    private static string JsTrim(string s) => s.Trim(JsWhiteSpace);
}
