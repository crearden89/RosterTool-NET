// Port of src/lib/walk/notes.test.ts.

using Roster.Engine.Walk;

namespace Roster.Engine.Tests.Walk;

public class NotesTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");
    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static WalkNote Note(string id, string raisedDate, string? completedAt = null) =>
        new(id, $"note {id}", null, null, D(raisedDate), completedAt is null ? null : T(completedAt));

    private static IEnumerable<string> Ids(IEnumerable<WalkNote> notes) => notes.Select(n => n.Id);

    // ── cleaning a typed note ──

    [Fact]
    public void Trims_and_accepts_real_text()
    {
        Assert.Equal(CleanedNote.Valid("Chiller 3 needs re-facing"), Notes.CleanNote("  Chiller 3 needs re-facing  "));
    }

    [Fact]
    public void Rejects_empty_and_whitespace_only()
    {
        // Easy to fat-finger the space bar on a tablet; an invisible note would
        // then sit in the open list forever with nothing to read.
        Assert.Equal(CleanedNote.Invalid("Type a note first."), Notes.CleanNote(""));
        Assert.Equal(CleanedNote.Invalid("Type a note first."), Notes.CleanNote("   "));
        Assert.Equal(CleanedNote.Invalid("Type a note first."), Notes.CleanNote("\n\t "));
    }

    [Fact]
    public void Rejects_anything_over_the_column_limit_and_says_by_how_much()
    {
        var res = Notes.CleanNote(new string('x', Notes.MaxNoteLength + 1));
        Assert.False(res.Ok);
        Assert.Contains((Notes.MaxNoteLength + 1).ToString(), res.Error);
    }

    [Fact]
    public void Accepts_exactly_the_limit()
    {
        Assert.Equal(CleanedNote.Valid(new string('x', Notes.MaxNoteLength)), Notes.CleanNote(new string('x', Notes.MaxNoteLength)));
    }

    [Fact]
    public void Measures_the_trimmed_length_so_trailing_spaces_cannot_trip_the_limit()
    {
        Assert.True(Notes.CleanNote(new string('x', Notes.MaxNoteLength) + "    ").Ok);
    }

    [Fact]
    public void Trims_as_JavaScript_does()
    {
        // String.prototype.trim: a byte-order mark, an ideographic space and a
        // line separator are white space; NEL (U+0085) is not.
        var jsSpace = new string(new[] { (char)0xFEFF, (char)0x3000, (char)0x2028 });
        var nel = ((char)0x0085).ToString();
        Assert.Equal(CleanedNote.Invalid("Type a note first."), Notes.CleanNote(jsSpace));
        Assert.Equal(CleanedNote.Valid(nel), Notes.CleanNote(" " + nel + " "));
    }

    // ── age ──

    [Fact]
    public void Counts_whole_days_since_the_note_was_raised()
    {
        Assert.Equal(0, Notes.AgeInDays(D("2026-08-11"), D("2026-08-11")));
        Assert.Equal(1, Notes.AgeInDays(D("2026-08-10"), D("2026-08-11")));
        Assert.Equal(7, Notes.AgeInDays(D("2026-08-04"), D("2026-08-11")));
    }

    [Fact]
    public void Never_goes_negative_if_a_clock_or_timezone_puts_a_note_in_the_future()
    {
        Assert.Equal(0, Notes.AgeInDays(D("2026-08-12"), D("2026-08-11")));
    }

    [Fact]
    public void Reads_as_plain_English()
    {
        Assert.Equal("today", Notes.AgeLabel(D("2026-08-11"), D("2026-08-11")));
        Assert.Equal("yesterday", Notes.AgeLabel(D("2026-08-10"), D("2026-08-11")));
        Assert.Equal("3 days ago", Notes.AgeLabel(D("2026-08-08"), D("2026-08-11")));
    }

    // ── open list ──

    [Fact]
    public void Drops_completed_notes()
    {
        var rows = new[] { Note("a", "2026-08-10"), Note("b", "2026-08-10", "2026-08-11T00:00:00Z") };
        Assert.Equal(["a"], Ids(Notes.SortOpen(rows)));
    }

    [Fact]
    public void Puts_the_longest_outstanding_note_first()
    {
        // The point of the list is what still is not done - newest-first would
        // bury exactly the note that needs chasing.
        var rows = new[] { Note("new", "2026-08-11"), Note("old", "2026-08-04"), Note("mid", "2026-08-09") };
        Assert.Equal(["old", "mid", "new"], Ids(Notes.SortOpen(rows)));
    }

    [Fact]
    public void Breaks_ties_stably_so_the_list_does_not_reshuffle_between_renders()
    {
        var rows = new List<WalkNote> { Note("c", "2026-08-10"), Note("a", "2026-08-10"), Note("b", "2026-08-10") };
        Assert.Equal(["a", "b", "c"], Ids(Notes.SortOpen(rows)));
        rows.Reverse();
        Assert.Equal(["a", "b", "c"], Ids(Notes.SortOpen(rows)));
    }

    [Fact]
    public void Does_not_mutate_the_array_it_is_given()
    {
        var rows = new List<WalkNote> { Note("b", "2026-08-11"), Note("a", "2026-08-04") };
        Notes.SortOpen(rows);
        Assert.Equal(["b", "a"], Ids(rows));
    }

    // ── time to close ──

    [Fact]
    public void Counts_calendar_days_from_raised_to_completed()
    {
        Assert.Equal(1, Notes.DaysToClose(D("2026-08-10"), T("2026-08-11T09:00:00Z")));
        // Raised in the morning, closed that afternoon: same day, not a fraction.
        Assert.Equal(0, Notes.DaysToClose(D("2026-08-10"), T("2026-08-10T23:00:00Z")));
        Assert.Equal(10, Notes.DaysToClose(D("2026-08-01"), T("2026-08-11T09:00:00Z")));
    }

    [Fact]
    public void Is_null_while_the_note_is_still_open()
    {
        // Counting an unfinished note as 0 would flatter the median below.
        Assert.Null(Notes.DaysToClose(D("2026-08-10"), null));
    }

    [Fact]
    public void Reads_the_completion_on_its_UTC_date_as_the_stored_timestamp_does()
    {
        // 08:30 in Sydney on the 11th is still the 10th in UTC.
        Assert.Equal(0, Notes.DaysToClose(D("2026-08-10"), T("2026-08-11T08:30:00+10:00")));
    }

    // ── summary ──

    private static WalkNote Closed(string id, string raised, string done) => Note(id, raised) with { CompletedAt = T(done) };

    [Fact]
    public void Counts_raised_completed_and_open()
    {
        var s = Notes.SummariseNotes([Closed("a", "2026-08-01", "2026-08-02T00:00:00Z"), Note("b", "2026-08-05")], D("2026-08-11"));
        Assert.Equal((2, 1, 1), (s.Raised, s.Completed, s.Open));
    }

    [Fact]
    public void Uses_a_median_close_time_so_one_ancient_note_cannot_hide_the_norm()
    {
        var rows = new[]
        {
            Closed("a", "2026-08-10", "2026-08-11T00:00:00Z"), // 1 day
            Closed("b", "2026-08-10", "2026-08-11T00:00:00Z"), // 1
            Closed("c", "2026-01-01", "2026-08-11T00:00:00Z"), // 222
        };
        var s = Notes.SummariseNotes(rows, D("2026-08-11"));
        Assert.Equal(1, s.MedianDaysToClose);
        // The mean would read ~75 and imply a store that never closes anything.
        Assert.True(s.MedianDaysToClose < 2);
    }

    [Fact]
    public void Averages_the_middle_pair_on_an_even_count()
    {
        var rows = new[]
        {
            Closed("a", "2026-08-09", "2026-08-11T00:00:00Z"), // 2
            Closed("b", "2026-08-07", "2026-08-11T00:00:00Z"), // 4
        };
        Assert.Equal(3, Notes.SummariseNotes(rows, D("2026-08-11")).MedianDaysToClose);
    }

    [Fact]
    public void Reports_the_longest_outstanding_open_note()
    {
        var s = Notes.SummariseNotes([Note("a", "2026-08-01"), Note("b", "2026-08-10")], D("2026-08-11"));
        Assert.Equal(10, s.OldestOpenDays);
    }

    [Fact]
    public void Returns_nulls_rather_than_zeros_when_there_is_nothing_to_measure()
    {
        // 0 would read as "closed same day" / "nothing outstanding", both false.
        var none = Notes.SummariseNotes([], D("2026-08-11"));
        Assert.Null(none.MedianDaysToClose);
        Assert.Null(none.OldestOpenDays);
        Assert.Null(Notes.SummariseNotes([Note("a", "2026-08-01")], D("2026-08-11")).MedianDaysToClose);
    }
}
