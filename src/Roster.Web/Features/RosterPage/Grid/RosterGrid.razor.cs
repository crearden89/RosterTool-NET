// Port of src/components/roster/RosterGrid.tsx (RosterGrid's props and the values it
// works out before drawing).

using Microsoft.AspNetCore.Components;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Web.Components.Ui;
using CoreGrid = Roster.Engine.Core.Grid;

namespace Roster.Web.Features.RosterPage.Grid;

/// <summary>
/// The single tile-grid used by the Plan view, Roster view and Floor Display.
/// <list type="bullet">
/// <item><see cref="ShowName"/>: adds a Name column before the role/position column.</item>
/// <item><see cref="TillsByTime"/>: when provided, renders the "Front End Team Members"
/// summary row (Plan view) - the bodies needed on checkout in each 15-minute block.</item>
/// <item><see cref="CostByKey"/>: when provided, adds a per-row Cost column (keyed by
/// row key) plus a day-total footer row. Opt-in only - the public Floor Display and the
/// virtual-pool Plan/Task view never pass this, so wage data is never shown there.</item>
/// </list>
/// </summary>
public partial class RosterGrid
{
    [Parameter, EditorRequired] public IReadOnlyList<RosterGridRow> Rows { get; set; } = [];

    [Parameter] public bool ShowName { get; set; }

    /// <summary>
    /// Inline row editing (Roster view): the Name cell becomes a type-ahead person
    /// picker and Start/Finish become time inputs, so a shift is changed where it is
    /// read instead of in a separate panel below the grid.
    /// </summary>
    [Parameter] public RowEditApi? RowEdit { get; set; }

    /// <summary>The checkout headcount needed per quarter-hour, keyed by its start (Plan view).</summary>
    [Parameter] public IReadOnlyDictionary<double, double>? TillsByTime { get; set; }

    /// <summary>Each row's wage cost, keyed by row key (Roster view).</summary>
    [Parameter] public IReadOnlyDictionary<string, double>? CostByKey { get; set; }

    [Parameter] public string EmptyLabel { get; set; } = "No shifts.";

    /// <summary>
    /// Kiosk "now" band: columns in [Start, End) get a pale-yellow wash (the Floor
    /// Display highlights the current-time → +2h window).
    /// </summary>
    [Parameter] public MinuteWindow? HighlightRange { get; set; }

    /// <summary>
    /// Edit mode (Plan view): clicking a 15-min cell inside a shift's span paints or
    /// erases it. Its presence alone turns on the focus-cell wash.
    /// </summary>
    [Parameter] public EventCallback<GridCell> OnCellClick { get; set; }

    /// <summary>
    /// Also make the cells OUTSIDE each row's span clickable, so painting one can
    /// stretch the shift out to reach it. The caller decides what that means; the
    /// grid only opens the cell up and hints it differently on hover.
    /// </summary>
    [Parameter] public bool Extendable { get; set; }

    /// <summary>Edit mode: a ✕ on each row.</summary>
    [Parameter] public EventCallback<string> OnRemoveRow { get; set; }

    /// <summary>Edit mode: a role badge that cycles when clicked.</summary>
    [Parameter] public EventCallback<string> OnRoleClick { get; set; }

    /// <summary>
    /// A strip of coloured 15-minute cells above the Front End row - the Plan view
    /// heats it with the sales curve, so each column shows how busy the store is in
    /// that quarter-hour.
    /// </summary>
    [Parameter] public HeatRow? HeatRow { get; set; }

    /// <summary>
    /// The day's load running periods (Plan view): a dark blue rule at each start
    /// and finish, and Load Running tiles outside every period drawn as error tiles.
    /// </summary>
    [Parameter] public IReadOnlyList<LoadRunning.Period>? LoadRunningPeriods { get; set; }

    /// <summary>
    /// Break column in hours to two decimals (Plan and Roster pages), rather than
    /// minutes ("30m") for the floor.
    /// </summary>
    [Parameter] public bool BreakAsHours { get; set; }

    /// <summary>
    /// Sub-task labels (the Plan page, and the Roster page with "Show sub-tasks"
    /// on): hover text names the tile's sub-task, and tiles of the groups in
    /// <see cref="GridSubtasks.Open"/> show the sub-task's code. Absent, the grid
    /// shows groups only, as on the Roster page by default and the floor.
    /// </summary>
    [Parameter] public GridSubtasks? Subtasks { get; set; }

    private const string ColBorder = "border-r border-[var(--brand-border)]";

    // Pale-yellow wash for the highlighted band; it outranks the premium-time
    // red tint so the "now" window stays readable at a glance.
    private const string HlBg = "bg-[var(--brand-yellow-wash)]";

    private const string LabelHeadCls = "bg-[var(--brand-navy)] px-3 py-2 align-middle font-medium text-white " + ColBorder;

    private static readonly IReadOnlyList<double> Cols = CoreGrid.DisplayBlocks();

    // Columns with a load running rule on their left edge; a period running to the
    // end of the grid closes on the last column's right edge instead.
    private static readonly double LastCol = Cols[^1];

    private bool EditMode => OnCellClick.HasDelegate;
    private HashSet<double> _lrEdges = [];
    private string _lrText = "";
    private int _leadingCols;
    private double _dayTotal;
    private JsMap<double, double> _checkoutTilesAt = new();

    protected override void OnParametersSet()
    {
        var periods = LoadRunningPeriods ?? [];
        _lrEdges = periods.SelectMany(p => new[] { p.Start, p.End }).ToHashSet();
        _lrText = string.Join(", ", periods.Select(p => $"{CoreGrid.MinToTime(p.Start)}–{CoreGrid.MinToTime(p.End)}"));
        _leadingCols = (ShowName ? 6 : 5) + (CostByKey is not null ? 1 : 0); // label cols before the time grid
        _dayTotal = 0;
        if (CostByKey is not null)
            foreach (var r in Rows)
                _dayTotal += CostByKey.TryGetValue(r.Key, out var c) ? c : 0;
        // Actual Checkout (CO) tiles per column, to reconcile against the front-end
        // requirement - a mismatch gets a corner-triangle marker on the count cell.
        // Counted by the same module that decides what a checkout tile IS, so the
        // two can't drift apart again.
        _checkoutTilesAt = TillsByTime is not null
            ? Display.CheckoutTilesByTime(Rows.Select(r => r.Blocks.Select(b => (b.Time, b.Group))))
            : new JsMap<double, double>();
    }

    private string? LrRule(double m) =>
        _lrEdges.Contains(m) ? Tiles.LrStrip("left")
        : m == LastCol && _lrEdges.Contains(m + 15) ? Tiles.LrStrip("right")
        : null;

    private bool InHl(double m) => HighlightRange is { } hr && m >= hr.Start && m < hr.End;

    private static bool IsCostFlag(double m) => m < 7 * 60 || m >= 18 * 60;

    /// <summary>A number as JavaScript writes it into the page (<c>data-c</c>, keys, counts).</summary>
    private static string C(double m) => Js.NumberToString(m);

    /// <summary>React's style object: each declaration joined with ";", missing ones skipped.</summary>
    private static string? Style(params string?[] parts)
    {
        var s = string.Join(";", parts.Where(p => !string.IsNullOrEmpty(p)));
        return s.Length == 0 ? null : s;
    }

    private string? Misplaced(string? group, double m)
    {
        if (LoadRunningPeriods is null || group != "load_running" || !LoadRunning.OutsidePeriods(LoadRunningPeriods, m)) return null;
        var noun = LoadRunningPeriods.Count == 1 ? "period" : "periods";
        return $"Load Running outside the store's load running {noun} ({(_lrText.Length > 0 ? _lrText : "none today")})";
    }

    private string CellClass(double m, bool clickable, bool inSpan)
    {
        var wash = InHl(m) ? HlBg : IsCostFlag(m) ? "bg-[var(--brand-red-tint)]" : "";
        var hover = clickable
            ? inSpan
                ? "cursor-pointer hover:bg-[var(--brand-navy-tint)] hover:shadow-inner"
                // Distinct hover: this click CHANGES the shift's span, not just a
                // tile's colour.
                : "cursor-cell hover:bg-[var(--brand-yellow)]/40 hover:shadow-inner"
            : "";
        return $"px-1 py-1.5 align-middle {ColBorder} {wash} {hover}";
    }

    private static Dictionary<double, string> BlockMap(RosterGridRow r)
    {
        var map = new Dictionary<double, string>();
        foreach (var b in r.Blocks) map[b.Time] = b.Group;
        return map;
    }

    private static Dictionary<double, string?> SubMap(RosterGridRow r)
    {
        var map = new Dictionary<double, string?>();
        foreach (var b in r.Blocks) map[b.Time] = b.Sub;
        return map;
    }

    // Edit mode's focus cell, as in a spreadsheet: the row and the column under
    // the mouse wash pale yellow, so it's plain which person and which
    // quarter-hour a click will paint. Pure CSS - the grid stays a plain display -
    // with one rule per time column (a column has no element of its own to
    // hover). The hovered cell keeps its own hover fill (navy to paint, yellow to
    // stretch a shift) and gets a navy outline, so it stands out where the two
    // washes cross.
    internal static string CrosshairCss(IEnumerable<double> cols) =>
        string.Join(",", new[] { ".rg-xhair tbody tr[data-row]:hover > td:not([data-c]:hover)" }
            .Concat(cols.Select(m => $".rg-xhair:has([data-c=\"{C(m)}\"]:hover) [data-c=\"{C(m)}\"]:not(:hover)"))) +
        "{background-color:var(--brand-yellow-wash)}" +
        ".rg-xhair tbody td[data-c]:hover{box-shadow:inset 0 0 0 2px var(--brand-navy)}";

    // The role badge's tone: MGR navy, SA blue, JSA amber.
    private static BadgeTone RoleTone(string badge) => badge switch
    {
        RoleBadges.Sa => BadgeTone.Blue,
        RoleBadges.Jsa => BadgeTone.Amber,
        _ => BadgeTone.Navy,
    };
}
