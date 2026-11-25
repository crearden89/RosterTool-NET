// Port of src/lib/subtasks/carry.ts.
// ─── Sub-tasks: the held plan's labels carried to the roster ─────────────────
// The owner (15/11/2026): sub-tasks set on the plan carry to the roster.
//
// The roster is built from the held plan: each plan shift is filled by a
// person and keeps its tiles where they are legal for them; tiles the roster
// adds or changes are painted afresh (Rostering/Fortnight). A roster tile
// that comes unchanged from the plan - its shift fills the plan shift, and it
// is at the same quarter-hour in the same group - keeps the plan's label. The
// rest are labelled by the normal rules, which keep the carried labels and
// share out the others (SubTasks/Label).
//
// A roster shift fills the plan shift with its times. The roster can trim a
// shift (a quarter-hour cut to fill a break's gap), grow one (time added so a
// break doesn't cost work) or hand it to someone else, so the pairing is: the
// same times first, then the most work tiles in common, then the nearest
// times. Who holds the shift plays no part: a label stays with its tile when
// the shift changes hands. Pure.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.SubTasks;

/// <summary>A shift as the carry reads it: its times and its tiles.</summary>
public sealed record CarryShift(double Start, double End, IReadOnlyList<DisplayBlock> Blocks)
{
    public static CarryShift Of(PlanShift s) => new(s.Start, s.End, s.Blocks);

    public static CarryShift Of(Shift s) => new(s.Start, s.End, ShiftTiles.BlocksOf(s));
}

public static class Carry
{
    /// <summary>Registers and SCOs are one group, Checkout, as the roster paints it.</summary>
    private static string GroupOf(string g) => g is "registers" or "scos" ? TaskGroups.Checkout : g;

    private static bool IsWork(string g) => g != TaskGroups.Break && g != TaskGroups.Flex;

    /// <summary>
    /// Which plan shift each roster shift fills, by index (roster -> plan). Each
    /// shift pairs once; a roster shift with other times and no work tile in
    /// common with any plan shift left fills none.
    /// </summary>
    public static JsMap<int, int> ShiftPairs(IReadOnlyList<CarryShift> plan, IReadOnlyList<CarryShift> roster)
    {
        var options = new List<(int R, int P, double Same, double Common, double Apart)>();
        for (var ri = 0; ri < roster.Count; ri++)
        {
            var r = roster[ri];
            var mine = r.Blocks.Where(b => IsWork(b.Group)).ToList();
            for (var pi = 0; pi < plan.Count; pi++)
            {
                var p = plan[pi];
                if (Math.Min(r.End, p.End) <= Math.Max(r.Start, p.Start)) continue;
                var at = new JsMap<double, string>(p.Blocks.Select(b => (b.Time, GroupOf(b.Group))));
                double common = mine.Count(b => at.Get(b.Time) == GroupOf(b.Group));
                double same = r.Start == p.Start && r.End == p.End ? 1 : 0;
                if (same > 0 || common > 0) options.Add((ri, pi, same, common, Math.Abs(r.Start - p.Start) + Math.Abs(r.End - p.End)));
            }
        }
        Js.Sort(options, (a, b) => (b.Same - a.Same).Or(b.Common - a.Common).Or(a.Apart - b.Apart).Or(a.R - b.R).Or(a.P - b.P));
        var pairs = new JsMap<int, int>();
        var taken = new JsSet<int>();
        foreach (var o in options)
        {
            if (pairs.Has(o.R) || taken.Has(o.P)) continue;
            pairs.Set(o.R, o.P);
            taken.Add(o.P);
        }
        return pairs;
    }

    /// <summary>
    /// Each roster shift's tiles with the plan's label on every tile that comes
    /// unchanged from the plan (same shift, same quarter-hour, same group); null
    /// for a shift that carries nothing, so it is returned as it came.
    /// </summary>
    private static List<List<DisplayBlock>?> Carried(IReadOnlyList<CarryShift> plan, IReadOnlyList<CarryShift> roster)
    {
        var pairs = ShiftPairs(plan, roster);
        return roster.Select((s, ri) =>
        {
            if (!pairs.TryGet(ri, out var pi)) return null;
            var at = new JsMap<double, DisplayBlock>(plan[pi].Blocks.Select(b => (b.Time, b)));
            var carried = false;
            var blocks = s.Blocks.Select(b =>
            {
                var p = at.Get(b.Time);
                if (p is null || p.Group != b.Group || !Catalogue.IsSubtaskOf(b.Group, p.Sub)) return b;
                carried = true;
                return b with { Sub = p.Sub };
            }).ToList();
            return carried ? blocks : null;
        }).ToList();
    }

    /// <summary>
    /// The roster's shifts with the plan's label on every tile that comes
    /// unchanged from the plan: same shift, same quarter-hour, same group. Every
    /// other tile is left as it came, for the rules. Only <c>Sub</c> is written;
    /// a shift nothing carries to is returned itself.
    /// </summary>
    public static List<CarryShift> CarryLabels(IReadOnlyList<CarryShift> plan, IReadOnlyList<CarryShift> roster)
    {
        var carried = Carried(plan, roster);
        return roster.Select((s, i) => carried[i] is { } b ? s with { Blocks = b } : s).ToList();
    }

    /// <inheritdoc cref="CarryLabels(IReadOnlyList{CarryShift}, IReadOnlyList{CarryShift})"/>
    public static List<Shift> CarryLabels(IReadOnlyList<CarryShift> plan, IReadOnlyList<Shift> roster)
    {
        var carried = Carried(plan, roster.Select(CarryShift.Of).ToList());
        return roster.Select((s, i) => carried[i] is { } b ? ShiftTiles.WithBlocks(s, b) : s).ToList();
    }

    private static List<DisplayBlock> Unlabelled(IEnumerable<DisplayBlock> blocks) => blocks.Select(b => b with { Sub = null }).ToList();

    /// <summary>The shifts with no labels, as a plan comes out of the painter.</summary>
    public static List<CarryShift> WithoutLabels(IReadOnlyList<CarryShift> shifts) => shifts.Select(s => s with { Blocks = Unlabelled(s.Blocks) }).ToList();

    /// <inheritdoc cref="WithoutLabels(IReadOnlyList{CarryShift})"/>
    public static List<PlanShift> WithoutLabels(IReadOnlyList<PlanShift> shifts) => shifts.Select(s => s with { Blocks = Unlabelled(s.Blocks) }).ToList();

    /// <inheritdoc cref="WithoutLabels(IReadOnlyList{CarryShift})"/>
    public static List<Shift> WithoutLabels(IReadOnlyList<Shift> shifts) =>
        shifts.Select(s => ShiftTiles.WithBlocks(s, s.Blocks.Select(b => new DisplayBlock(b.Time, b.Group)).ToList())).ToList();

    /// <summary>Do two copies of the same shifts carry the same labels, tile for tile?</summary>
    public static bool SameLabels(IReadOnlyList<CarryShift> a, IReadOnlyList<CarryShift> b)
    {
        static string? Lbl(DisplayBlock x) => Catalogue.IsSubtaskOf(x.Group, x.Sub) ? x.Sub : null;
        return a.Count == b.Count &&
            a.Select((s, i) => s.Blocks.Count == b[i].Blocks.Count && s.Blocks.Select((x, j) => Lbl(x) == Lbl(b[i].Blocks[j])).All(t => t)).All(t => t);
    }
}
