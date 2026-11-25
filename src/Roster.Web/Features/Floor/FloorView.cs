// New: the Floor Display's view models - the props page.tsx hands FloorScreen,
// FloorHeader, DriftBanner and WalkNotes in the TypeScript app.

using Roster.Engine.Walk;

namespace Roster.Web.Features.Floor;

/// <summary>The header, shared by the wall screen and the walk notes view.</summary>
public sealed record FloorHeaderInfo(string When, string Store, bool OtherDay);

/// <summary>The out-of-date banner's two times, preformatted.</summary>
public sealed record DriftNote(string Approved, string Changed);

/// <summary>What the roster column says when there is no approved roster to draw.</summary>
public sealed record EmptyMessage(string Title, string Detail);

/// <summary>One of the day's admin tasks on the tick-off list. Window is preformatted "HH:MM–HH:MM".</summary>
public sealed record ChecklistTask(string Name, double Minutes, string Window);

/// <summary>Someone on today's roster a walk note can be allocated to.</summary>
public sealed record CrewOption(string Id, string Name);

/// <summary>A run of premium time (Pay Rates), in minutes from midnight.</summary>
public sealed record PremiumBand(double Start, double End);

/// <summary>One person's row on the wall screen. State is null on another day: no "now" to stand at.</summary>
public sealed record ScreenRow(
    string Key,
    string Name,
    string Badge,
    double Start,
    double End,
    IReadOnlyList<TaskRun> Runs,
    NowState? State);

/// <summary>The wall screen (FloorScreen's props).</summary>
public sealed record FloorScreenView(
    FloorHeaderInfo Header,
    DriftNote? Drift,
    IReadOnlyList<ScreenRow> Rows,
    EmptyMessage? EmptyMessage,
    DayWindow? Span,
    double? Now, // store minutes, today only
    RowScale Scale,
    IReadOnlyList<PremiumBand> Premium,
    IReadOnlyList<ChecklistTask> AdminTasks,
    IReadOnlyList<string> CheckedNames,
    bool CanTick,
    IReadOnlyList<WalkNote> Notes, // the open notes; the panel sorts them
    DateOnly Today,
    string NotesHref,
    LoadSummary Load,
    // The store's Load Planner page, or null when the strip is not a link.
    string? LoadHref);

/// <summary>The walk notes view (?view=notes): the header with its back link, and the form.</summary>
public sealed record FloorNotesView(
    FloorHeaderInfo Header,
    DriftNote? Drift,
    string BackHref,
    IReadOnlyList<WalkNote> Notes, // the open notes
    IReadOnlyList<CrewOption> Crew,
    DateOnly Today,
    IReadOnlyList<WalkNote> CompletedToday);

/// <summary>
/// What /floor/{token} shows: the wall screen, or the walk notes view when the
/// link asks for it (exactly one is set).
/// </summary>
public sealed record FloorPageData(string Token, FloorScreenView? Screen, FloorNotesView? Notes)
{
    public bool ShowNotes => Notes is not null;
}

/// <summary>A kiosk action's answer (the TypeScript's { ok, error? }).</summary>
public sealed record KioskResult(bool Ok, string? Error = null)
{
    public static readonly KioskResult Success = new(true);
    public static KioskResult Fail(string error) => new(false, error);
}
