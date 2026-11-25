// Port of src/components/ui/DayChips.tsx.

namespace Roster.Web.Components.Ui;

/// <summary>A day chip's dot: green, red, or grey (another store's plan, a closed day).</summary>
public enum DayTone { Ok, Error, Neutral }

/// <summary>One day of <see cref="DayChips"/>.</summary>
public sealed record DayChip
{
    public required DateOnly Date { get; init; }

    /// <summary>Where the chip goes (app-relative, e.g. "plan/generate?store=...&amp;date=...").</summary>
    public required string Href { get; init; }

    public DayTone Tone { get; init; } = DayTone.Neutral;

    /// <summary>The figure under the date, e.g. "+0.09" or "11".</summary>
    public string? Note { get; init; }

    /// <summary>The same, spelt out where the chip has room, e.g. "11 shifts".</summary>
    public string? NoteWide { get; init; }

    /// <summary>What a screen reader and the hover say about the day, e.g. "OK, 11 shifts".</summary>
    public string? Status { get; init; }
}
