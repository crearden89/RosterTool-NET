// Port of src/lib/roster/display.ts.
// ─── Shared roster-display model ──────────────────────────────────────────────
// The Plan (Task) view, the Roster view and the public Floor Display all render
// the same tile grid. This is the single source for the display block shape and
// the two display-only decorations applied to any set of shifts:
//   1. splitCheckout - one merged Checkout demand group (the Registers/SCO split
//      is retired; see SplitCheckout).
//   2. overlayBreaks - a shift's break minutes are shown as their own tiles at
//      the shift midpoint rather than left as a blank gap.

using Roster.Engine.Pay;

namespace Roster.Engine.Core;

/// <summary>
/// One tile as the screens show it. Groups: checkout, registers, scos,
/// load_running, reset, admin, specials, cleaning, non_op, break, flex.
/// </summary>
public sealed record DisplayBlock : ITile
{
    public double Time { get; init; } // minutes from midnight
    public string Group { get; init; } = TaskGroups.Flex;
    // The sub-task the tile is labelled with (Reset, Load Running, Cleaning and
    // Specials; SubTasks). A label only: nothing that plans or checks a day
    // reads it. Plan tiles and roster tiles both carry one.
    public string? Sub { get; init; }

    public DisplayBlock() { }

    public DisplayBlock(double time, string group, string? sub = null)
    {
        Time = time;
        Group = group;
        Sub = sub;
    }
}

/// <summary>The plan's slot types: "MGR", "SA" or "JSA".</summary>
public static class RoleBadges
{
    public const string Mgr = "MGR";
    public const string Sa = "SA";
    public const string Jsa = "JSA";

    public static string RoleBadge(string role)
    {
        if (Roles.IsManager(role)) return Mgr;
        if (role == Roles.JSA) return Jsa;
        return Sa;
    }
}

public sealed record RawBlock(double Time, string Group, string? Sub = null);

public sealed record RawShift(
    string Key,
    string Name,
    string Role,
    double Start,
    double End,
    double BreakMinutes,
    // Raw per-block groups as produced by the engine or read from the DB. May
    // include "checkout", task groups, "flex", or the already-split
    // "registers"/"scos" (when read back from persisted, decorated data).
    IReadOnlyList<RawBlock> RawBlocks);

public sealed record RosterRow(
    string Key,
    string Name,
    string Badge,
    double Start,
    double End,
    double BreakMinutes, // total time off the floor (both segments)
    double UnpaidBreakMinutes, // portion deducted from paid hours
    List<DisplayBlock> Blocks);

public static class Display
{
    private static double Clamp(double n, double lo, double hi) => Math.Max(lo, Math.Min(n, hi));

    // The block-time range(s) a shift's break(s) occupy, snapped to the grid.
    // A single break sits at the shift midpoint; two breaks (>6h tiered) sit at
    // roughly the 1/3 and 2/3 marks. Returns [] when a break can't be shown (e.g.
    // the shift is too short to fit it).
    public static List<(double Start, double End)> BreakWindows(double start, double end, BreakResult brk)
    {
        var shiftBlocks = (end - start) / Grid.BlockMin;
        var segBlocks = Js.Round(brk.TotalMinutes / brk.Segments / Grid.BlockMin);
        if (segBlocks <= 0 || segBlocks * brk.Segments >= shiftBlocks) return [];

        if (brk.Segments == 1)
        {
            var idx = Clamp(Math.Floor(shiftBlocks / 2 - segBlocks / 2), 0, shiftBlocks - segBlocks);
            var s = start + idx * Grid.BlockMin;
            return [(s, s + segBlocks * Grid.BlockMin)];
        }

        var windows = new List<(double Start, double End)>();
        foreach (var frac in new[] { 1.0 / 3, 2.0 / 3 })
        {
            var idx = Clamp(Math.Floor(shiftBlocks * frac - segBlocks / 2), 0, shiftBlocks - segBlocks);
            var s = start + idx * Grid.BlockMin;
            windows.Add((s, s + segBlocks * Grid.BlockMin));
        }
        // Keep the two segments from touching/overlapping.
        if (windows[1].Start < windows[0].End)
        {
            var s = Math.Min(windows[0].End, end - segBlocks * Grid.BlockMin);
            windows[1] = (s, s + segBlocks * Grid.BlockMin);
        }
        return windows;
    }

    // ONE Checkout tile. This used to split "checkout" into Registers vs SCO
    // tiles from the Till engine's per-block counts; the stores read that as two
    // different jobs when it is one till team, so the split is retired. Both the
    // engine's merged "checkout" and the previously-persisted "registers"/"scos"
    // rows resolve to the single checkout group - old approved rosters render
    // correctly without a data migration. The Till engine itself still plans the
    // MCO/SCO staffing split; this is display only, as the split before it was.
    // The tillByTime parameter stays so persisted data and callers are untouched.
    public static List<List<string>> SplitCheckout<TBlock>(
        IEnumerable<IEnumerable<TBlock>> blockLists,
        Func<TBlock, string> groupOf,
        object? tillByTime = null)
    {
        _ = tillByTime; // kept for caller compatibility - see comment above
        return blockLists.Select(list => list.Select(b => IsCheckoutGroup(groupOf(b)) ? TaskGroups.Checkout : groupOf(b)).ToList()).ToList();
    }

    // The one place that answers "is this block someone on checkout?". Both the
    // merged "checkout" group and the pre-merge "registers"/"scos" names count -
    // SplitCheckout normalises the latter away, but persisted rows can still carry
    // them, and a block that reads as NOT checkout is worse than one read as the
    // wrong kind of checkout: it makes a manned till look unmanned.
    public static bool IsCheckoutGroup(string group) => group is "checkout" or "registers" or "scos";

    /// <summary>
    /// How many people are actually on checkout in each 15-minute block, across all
    /// rostered rows. Reconciled against the front-end requirement on the Plan
    /// view's "Front End Team Members" row.
    /// </summary>
    public static JsMap<double, double> CheckoutTilesByTime(IEnumerable<IEnumerable<(double Time, string Group)>> rows)
    {
        var out_ = new JsMap<double, double>();
        foreach (var r in rows)
            foreach (var b in r)
                if (IsCheckoutGroup(b.Group)) out_.Set(b.Time, out_.GetOr(b.Time, 0) + 1);
        return out_;
    }

    // Hierarchy order for roster rows: most senior at the top (spec: SM first).
    private static readonly IReadOnlyDictionary<string, double> RoleRank = new Dictionary<string, double>
    {
        [Roles.SM] = 0, [Roles.ASM] = 1, [Roles.DSM] = 2, [Roles.RSM] = 3, [Roles.SA] = 4, [Roles.JSA] = 5,
    };

    // Build fully-decorated rows (checkout split + break overlay) ready to render.
    public static List<RosterRow> BuildRosterRows(IReadOnlyList<RawShift> shifts, object? tillByTime = null)
    {
        var resolved = SplitCheckout(shifts.Select(s => s.RawBlocks), b => b.Group, tillByTime);
        var rankByKey = new JsMap<string, double>();
        foreach (var s in shifts) rankByKey.Set(s.Key, RoleRank.GetValueOrDefault(s.Role, 4));
        var rows = shifts.Select((s, si) =>
        {
            var byTime = new JsMap<double, string>();
            for (var bi = 0; bi < s.RawBlocks.Count; bi++) byTime.Set(s.RawBlocks[bi].Time, resolved[si][bi]);
            // Sub-task labels ride along on the tiles of their own group only.
            var subByTime = new JsMap<double, string>();
            foreach (var b in s.RawBlocks)
                if (b.Sub is not null && SubTasks.Catalogue.IsSubtaskOf(b.Group, b.Sub)) subByTime.Set(b.Time, b.Sub);
            var brk = Breaks.BreakForRole(s.Role, (s.End - s.Start) / 60);
            // Engine-painted blocks already carry till-safe break tiles; only shifts
            // WITHOUT them (e.g. persisted Floor Display data) get the midpoint
            // overlay fallback.
            if (!s.RawBlocks.Any(b => b.Group == TaskGroups.Break))
                foreach (var (ws, we) in BreakWindows(s.Start, s.End, brk))
                    for (var m = ws; m < we; m += Grid.BlockMin) byTime.Set(m, TaskGroups.Break);
            var blocks = Js.Sort(
                byTime.Select(kv =>
                {
                    var sub = subByTime.Get(kv.Key);
                    return sub is not null && SubTasks.Catalogue.IsSubtaskOf(kv.Value, sub)
                        ? new DisplayBlock(kv.Key, kv.Value, sub)
                        : new DisplayBlock(kv.Key, kv.Value);
                }).ToList(),
                (a, b) => a.Time - b.Time);
            return new RosterRow(s.Key, s.Name, RoleBadges.RoleBadge(s.Role), s.Start, s.End, brk.TotalMinutes, brk.UnpaidMinutes, blocks);
        }).ToList();
        // Hierarchy first (SM at the top), then start time, then name.
        return Js.Sort(rows, (a, b) =>
            (rankByKey.GetOr(a.Key, 4) - rankByKey.GetOr(b.Key, 4)).Or(a.Start - b.Start).Or(Js.LocaleCompare(a.Name, b.Name)));
    }
}
