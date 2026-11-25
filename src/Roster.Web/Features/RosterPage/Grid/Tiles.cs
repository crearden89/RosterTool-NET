// Port of src/components/roster/RosterGrid.tsx (TILE, LEGEND_ITEMS, the tile hover names,
// the error tile and the load running rule).

using Roster.Engine.Core;

namespace Roster.Web.Features.RosterPage.Grid;

/// <summary>A tile's background colour, its two-letter code, and its text colour on a light background.</summary>
public sealed record TileStyle(string Color, string Abbrev, string? Text = null);

/// <summary>One entry of the tile legend.</summary>
public sealed record LegendItem(string Key, string Label);

/// <summary>
/// The tile map shared by the Plan view, the Roster view and the Floor Display:
/// each display group's colour and code, the legend, and the inline styles the
/// grid draws with.
/// </summary>
public static class Tiles
{
    private const string Navy = "var(--brand-navy)";

    // Tile-specific 2-letter codes (deliberately distinct from Constants' own
    // 3-letter Abbrev field, which is used elsewhere) - sized to fit a small
    // square tile.
    // Colours per the client's current tool (the brand colours where one
    // exists): R light green / SC dark green / LR brand navy / RR brand light
    // blue / AD purple / SB brand yellow / CL brand orange / BR brand red / N
    // grey. Light backgrounds carry navy text for contrast.
    public static readonly IReadOnlyDictionary<string, TileStyle> Tile = new Dictionary<string, TileStyle>
    {
        // One Checkout tile (CO). registers/scos remain as aliases so any persisted
        // block that slips through un-normalised still renders as Checkout rather
        // than as a blank tile.
        ["checkout"] = new(LabourGroups.CheckoutColorLeft, "CO", Navy),
        ["registers"] = new(LabourGroups.CheckoutColorLeft, "CO", Navy),
        ["scos"] = new(LabourGroups.CheckoutColorLeft, "CO", Navy),
        ["load_running"] = new(LabourGroups.ByKey["load_running"].Color, "LR"),
        ["reset"] = new(LabourGroups.ByKey["reset"].Color, "RR", Navy),
        ["admin"] = new(LabourGroups.ByKey["admin"].Color, "AD"),
        ["specials"] = new(LabourGroups.ByKey["specials"].Color, "SB", Navy),
        ["cleaning"] = new(LabourGroups.ByKey["cleaning"].Color, "CL"),
        ["non_op"] = new(LabourGroups.ByKey["non_op"].Color, "N"),
        ["break"] = new("var(--brand-red)", "BR"),
    };

    public static readonly IReadOnlyList<LegendItem> LegendItems =
    [
        new("checkout", "Checkout"),
        new("load_running", LabourGroups.ByKey["load_running"].Label),
        new("reset", LabourGroups.ByKey["reset"].Label),
        new("admin", LabourGroups.ByKey["admin"].Label),
        new("specials", LabourGroups.ByKey["specials"].Label),
        new("cleaning", LabourGroups.ByKey["cleaning"].Label),
        new("non_op", LabourGroups.ByKey["non_op"].Label),
        new("break", "Break"),
    ];

    // Hover names come from the legend, never the raw group key - a tooltip
    // reading "load_running" (or the display-retired "scos") is debugger output,
    // not a label.
    public static readonly IReadOnlyDictionary<string, string> TileTitle = BuildTileTitle();

    private static Dictionary<string, string> BuildTileTitle()
    {
        var titles = LegendItems.ToDictionary(i => i.Key, i => i.Label);
        titles["registers"] = "Checkout";
        titles["scos"] = "Checkout";
        return titles;
    }

    // A tile in the wrong place: white, red border, struck through in red. Used
    // for Load Running planned outside the store's load running periods.
    public const string ErrorTileStyle =
        "background:#ffffff;border:1.5px solid var(--brand-red);color:var(--brand-red);" +
        "background-image:linear-gradient(to top right, transparent calc(50% - 0.75px), var(--brand-red) calc(50% - 0.75px), var(--brand-red) calc(50% + 0.75px), transparent calc(50% + 0.75px))";

    /// <summary>A tile's own colours.</summary>
    public static string TileColourStyle(TileStyle t) => $"background:{t.Color};color:{t.Text ?? "#fff"}";

    // The dark blue dashed rule drawn at the start and finish of each load
    // running period - Load Running's own tile colour, so it reads as that
    // group's. A 2px strip of 6px dashes rather than a dashed border: a border
    // restarts its dashes in every cell, doubling them up where rows meet, while
    // the strip's 12px repeat fits a 48px shift row exactly, so the dashes run on
    // evenly down the shifts.
    public static readonly string LrColor = LabourGroups.ByKey["load_running"].Color;

    /// <summary>The load running rule on a cell's left or right edge.</summary>
    public static string LrStrip(string side) =>
        $"background-image:repeating-linear-gradient(to bottom, {LrColor} 0 6px, transparent 6px 12px);" +
        $"background-size:2px 100%;background-repeat:no-repeat;background-position:{side} top";

    /// <summary>The legend's sample of the load running rule.</summary>
    public static readonly string LrLegendStyle =
        $"background-image:repeating-linear-gradient(to bottom, {LrColor} 0 3px, transparent 3px 5px)";
}
