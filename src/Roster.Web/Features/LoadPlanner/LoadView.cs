// Port of the types in src/app/load/[token]/LoadPlanner.tsx: what both layouts
// (the kiosk and the phone) draw, and what they can do.

using Roster.Engine.Load;

namespace Roster.Web.Features.LoadPlanner;

public enum LoadScreen { NoCrew, Count, Plan }

public enum LoadTone { Go, Wait, Edit }

/// <summary>The header's status pill: its tone, its words, and the phone's short form.</summary>
public sealed record LoadStatus(LoadTone Tone, string Text, string Short);

/// <summary>A line under the buttons: a saved message or a failure.</summary>
public sealed record LoadMessage(bool Good, string Text);

/// <summary>What both layouts draw (LoadView in the TypeScript).</summary>
public sealed record LoadView(
    LoadPageData D,
    double Now,
    LoadScreen Screen,
    bool FirstCount, // counting the day's first load: nothing to cancel back to
    bool Assigning,
    double? DoneAt, // marked complete on this screen, store minutes
    LoadPlan? Plan, // the saved plan, the live one while assigning, the estimate while counting
    LoadPlan? SavedPlan,
    IReadOnlyDictionary<string, string> Pins,
    IReadOnlyList<string> Changed, // people whose pin differs from the saved one
    IReadOnlyDictionary<string, double> Qty, // the counts being entered
    double Total, // pallets counted so far
    string? LastTapped,
    string? ChooserFor,
    bool Pending,
    LoadMessage? Message,
    LoadStatus Status,
    string FloorHref)
{
    /// <summary>Leaving now would lose counts or pins (the back link asks first).</summary>
    public bool Unsaved { get; init; }
}

/// <summary>What both layouts can do (LoadActions in the TypeScript).</summary>
public interface ILoadActions
{
    void StartCount();
    void CancelCount();
    void Bump(string id, int by);
    Task SaveCountAsync();
    void StartAssign();
    void CancelAssign();
    void OpenChooser(string? key);
    void Choose(string key, string? type);
    Task SaveAssignAsync();
    Task MarkCompleteAsync();
    /// <summary>The back link to the Floor Display (it may ask first, or close the tab).</summary>
    Task BackAsync();
}
