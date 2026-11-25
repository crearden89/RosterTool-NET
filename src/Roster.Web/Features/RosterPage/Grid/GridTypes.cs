// Port of src/components/roster/RosterGrid.tsx (the shapes the grid's props take).

using Roster.Engine.Core;

namespace Roster.Web.Features.RosterPage.Grid;

/// <summary>
/// One row of the tile grid: a shift, its role badge, its span and its painted
/// quarter-hours. The Roster page passes E1's <see cref="RosterRow"/>s
/// (<see cref="From(RosterRow)"/>); the Plan page builds its rows from the
/// plan's shifts, without names.
/// </summary>
public sealed record RosterGridRow
{
    public required string Key { get; init; }
    public string? Name { get; init; }

    /// <summary>"MGR", "SA" or "JSA" (<see cref="RoleBadges"/>).</summary>
    public required string Badge { get; init; }

    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>Total off-floor break (shown in the Break column).</summary>
    public double BreakMinutes { get; init; }

    /// <summary>Deducted from paid hours (the Hrs column).</summary>
    public double UnpaidBreakMinutes { get; init; }

    public IReadOnlyList<DisplayBlock> Blocks { get; init; } = [];

    /// <summary>A decorated roster row (<see cref="Display.BuildRosterRows"/>) as a grid row.</summary>
    public static RosterGridRow From(RosterRow r) => new()
    {
        Key = r.Key,
        Name = r.Name,
        Badge = r.Badge,
        Start = r.Start,
        End = r.End,
        BreakMinutes = r.BreakMinutes,
        UnpaidBreakMinutes = r.UnpaidBreakMinutes,
        Blocks = r.Blocks,
    };
}

/// <summary>A cell the user clicked in edit mode: the row's key and the quarter-hour's start.</summary>
public readonly record struct GridCell(string RowKey, double Time);

/// <summary>One coloured quarter-hour of the heat row, with its hover text.</summary>
public sealed record HeatCell(string Color, string Title);

/// <summary>
/// A strip of coloured 15-minute cells above the Front End row - the Plan view
/// heats it with the sales curve, so each column shows how busy the store is in
/// that quarter-hour. Cells are keyed by the quarter-hour's start.
/// </summary>
public sealed record HeatRow(string Label, IReadOnlyDictionary<double, HeatCell> Cells, string? Hint = null);

/// <summary>
/// Sub-task labels (the Plan page, and the Roster page with "Show sub-tasks"
/// on): hover text names the tile's sub-task, and tiles of the groups in
/// <see cref="Open"/> show the sub-task's code. Absent, the grid shows groups
/// only, as on the Roster page by default and the floor.
/// </summary>
public sealed record GridSubtasks(IReadOnlySet<string> Open)
{
    /// <summary>Hover text names the sub-task; every tile keeps its group's code.</summary>
    public static readonly GridSubtasks NoneOpen = new(new HashSet<string>());
}
