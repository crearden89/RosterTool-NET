// New: the one place the Floor Display reads the roster grid's tile colours and
// legend (Features/RosterPage/Grid, the TypeScript RosterGrid's TILE and
// LEGEND_ITEMS), so a task has the same colour on every grid in the app.

using Roster.Engine.Core;
using Roster.Web.Features.RosterPage.Grid;

namespace Roster.Web.Features.Floor;

internal static class FloorTiles
{
    /// <summary>The legend's label for a task, or its key (the TypeScript's label()).</summary>
    public static string Label(string group) =>
        Tiles.LegendItems.FirstOrDefault(i => i.Key == group)?.Label ?? group;

    /// <summary>The tile colour of a task.</summary>
    public static string Color(string group) => Tiles.Tile[group].Color;

    /// <summary>The text colour on a task's tile (white unless the tile is light).</summary>
    public static string Text(string group) => Tiles.Tile[group].Text ?? "#ffffff";

    /// <summary>The legend, in order.</summary>
    public static IEnumerable<(string Key, string Label)> Legend => Tiles.LegendItems.Select(i => (i.Key, i.Label));

    /// <summary>A number as JavaScript writes it into a style (`${n}`).</summary>
    public static string N(double n) => Js.NumberToString(n);

    /// <summary>A percentage as the TypeScript's `${pct}%` writes it.</summary>
    public static string Pct(double n) => Js.NumberToString(n) + "%";
}
