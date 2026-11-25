// Port of src/components/roster/RosterGrid.tsx (RosterTile's choices: whether a tile shows,
// its colours, hover text and code).

using Roster.Engine.SubTasks;

namespace Roster.Web.Features.RosterPage.Grid;

/// <summary>What one tile shows: its inline style, hover text and code.</summary>
public readonly record struct TileView(string Style, string Title, string Text)
{
    /// <summary>The tile's classes (a small square, centred in its cell).</summary>
    public const string Class =
        "mx-auto flex h-6 w-6 items-center justify-center rounded text-[9px] font-semibold leading-none";

    /// <summary>
    /// The tile for a quarter-hour, or null for none (no group, flex, or a group
    /// with no tile). <paramref name="sub"/> (Plan page, and the Roster page with
    /// "Show sub-tasks" on): the tile's sub-task. Its hover text names the group
    /// and the sub-task ("Reset · Re-runs"); <paramref name="showSub"/> shows the
    /// sub-task's code in place of the group's, for a group opened in the
    /// Benchmark Hours table. <paramref name="error"/> draws it as an error tile.
    /// </summary>
    public static TileView? For(string? group, string? error = null, string? sub = null, bool showSub = false)
    {
        if (string.IsNullOrEmpty(group) || group == "flex") return null;
        if (!Tiles.Tile.TryGetValue(group, out var t)) return null;
        var label = Tiles.TileTitle.TryGetValue(group, out var l) ? l : group;
        var subtask = !string.IsNullOrEmpty(sub) && Catalogue.SubtaskByKey.TryGetValue(sub, out var s) ? s : null;
        var hasError = !string.IsNullOrEmpty(error);
        var title = hasError
            ? subtask is not null ? $"{Catalogue.SubtaskTitle(label, sub)}: {error}" : error!
            : Catalogue.SubtaskTitle(label, sub);
        return new TileView(
            hasError ? Tiles.ErrorTileStyle : Tiles.TileColourStyle(t),
            title,
            showSub && subtask is not null ? subtask.Code : t.Abbrev);
    }
}
