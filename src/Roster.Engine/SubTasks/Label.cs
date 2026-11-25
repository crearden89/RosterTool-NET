// Port of src/lib/subtasks/label.ts.
// ─── Sub-tasks: labelling a day's painted tiles ──────────────────────────────
// One pass after painting (master plan section 8). It reads the tiles and
// writes a `sub` on each tile of Reset, Load Running, Cleaning and Specials;
// it never moves, adds or removes a tile, so a plan's results are the same
// with or without it. Every tile of those groups gets exactly one label, so
// each group's sub-tasks add up to the group's tiles. Pure.
//
//   - Reset: from the Reset tasks Stage 1 planned. Each planned quarter-hour
//     belongs to a job (Stage1Result.UnitWindows); a task named "re-run" is
//     Re-runs, any other Decarding. Painted tiles are matched to those
//     quarter-hours as the day checker matches Admin.
//   - Load Running, Cleaning, Specials: the group's tiles are split between
//     its sub-tasks by share (largest remainder), then handed out the way the
//     load board's allocator hands out pallets (Load/Allocate.cs): each
//     person owns one sub-task and works it through.
//
// The TypeScript labels any shape with `blocks`; here the labeller works on
// each shift's list of DisplayBlocks (LabelBlocks), with overloads for the
// roster's Shift and, through two delegates, for any other shift type.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.SubTasks;

/// <summary>A planned Reset quarter-hour: the window of the job it belongs to, and its sub-task.</summary>
public sealed record ResetUnit(double Lo, double Hi, string Sub); // Sub: "re_runs" | "decarding"

/// <summary>A Stage 1 planned block, as the Reset labels read it (group, time, headcount).</summary>
public sealed record ResetSourceBlock(string Group, double Time, double Headcount);

/// <summary>A Stage 1 unit window (the job a planned quarter-hour belongs to), as the Reset labels read it.</summary>
public sealed record ResetSourceUnit(string Group, double Time, double Start, double End, bool Named, IReadOnlyList<string>? Names = null);

/// <summary>A resolved Reset task of the day, as the Reset labels read it.</summary>
public sealed record ResetSourceTask(string Name, double Minutes, double WindowStart, double WindowEnd);

/// <summary>What Stage 1 left behind about Reset: enough for a held plan too.</summary>
public sealed record ResetSource
{
    public double StaffOpen { get; init; }
    public double StaffClose { get; init; }
    public IReadOnlyList<ResetSourceBlock>? Blocks { get; init; }
    public IReadOnlyList<ResetSourceUnit>? UnitWindows { get; init; }
    public IReadOnlyList<ResetSourceTask>? ResetTasks { get; init; }

    /// <summary>Stage 1's record of the day, as the Reset labels read it.</summary>
    public static ResetSource From(Stage1Computation s1) => new()
    {
        StaffOpen = s1.StaffOpen,
        StaffClose = s1.StaffClose,
        Blocks = s1.Blocks.Select(b => new ResetSourceBlock(b.Group, b.Time, b.Headcount)).ToList(),
        UnitWindows = s1.UnitWindows?.Select(u => new ResetSourceUnit(u.Group, u.Time, u.Start, u.End, u.Named, u.Names)).ToList(),
        ResetTasks = s1.ResetTasks?.Select(t => new ResetSourceTask(t.Name, t.Minutes, t.WindowStart, t.WindowEnd)).ToList(),
    };
}

/// <summary>The load board's average day for one weekday (LoadBoard.LoadBoardAverages).</summary>
public sealed record LoadSplit
{
    /// <summary>Average minutes of each part of the load arriving on this weekday (load board), by load part.</summary>
    public JsMap<string, double> Minutes { get; init; } = new();
    /// <summary>Each sub-task's place in the order the load is worked (lower first).</summary>
    public JsMap<string, double> Priority { get; init; } = new();
}

public sealed record LabelContext
{
    /// <summary>The day: Specials is built on Tuesdays and Fridays only.</summary>
    public DateOnly Date { get; init; }
    /// <summary>Stage 1's record of the day, for Reset.</summary>
    public ResetSource? Stage1 { get; init; }
    /// <summary>The load board's average day for this weekday; null = no history.</summary>
    public LoadSplit? Load { get; init; }
    /// <summary>The share settings (Rostering rules).</summary>
    public RosteringRules Rules { get; init; } = RosteringRules.Default;
    /// <summary>
    /// Which sub-task each Reset task counts under, by task name: the mapping
    /// saved on the Reset task (reset_tasks.sub_task). A task not in it counts by
    /// its name (Catalogue.ResetSubtaskFor). Null = every task by its name.
    /// </summary>
    public JsMap<string, string?>? ResetMapping { get; init; }
}

public static class Label
{
    // ─── The split ───────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="total"/> whole tiles shared out in proportion to <paramref name="weights"/>, by largest
    /// remainder: the counts are whole and add up to <paramref name="total"/>. Ties go to the
    /// earlier key. All-zero weights share equally.
    /// </summary>
    public static JsMap<string, double> LargestRemainder(double total, IReadOnlyList<(string Key, double Weight)> weights)
    {
        var out_ = new JsMap<string, double>(weights.Select(w => (w.Key, 0.0)));
        if (total <= 0 || weights.Count == 0) return out_;
        var clean = weights.Select(w => (w.Key, Weight: double.IsFinite(w.Weight) && w.Weight > 0 ? w.Weight : 0)).ToList();
        var sum = clean.Aggregate(0.0, (a, w) => a + w.Weight);
        var ws = sum > 0 ? clean : clean.Select(w => (w.Key, Weight: 1.0)).ToList();
        var wsum = sum > 0 ? sum : ws.Count;
        var rem = new List<(string K, double R, int I)>();
        double given = 0;
        for (var i = 0; i < ws.Count; i++)
        {
            var (k, w) = ws[i];
            var exact = (total * w) / wsum;
            var n = Math.Floor(exact + 1e-9);
            out_.Set(k, n);
            given += n;
            rem.Add((k, w > 0 ? exact - n : -1, i));
        }
        Js.Sort(rem, (a, b) => (b.R - a.R).Or(a.I - b.I));
        for (var j = 0; given < total; j = (j + 1) % rem.Count, given++) out_.Set(rem[j].K, out_[rem[j].K] + 1);
        return out_;
    }

    // ─── The hand-out (mirrors Load/Allocate.cs) ────────────────────────────

    /// <summary>A tile being labelled: its shift's index, the quarter-hour, and the label (written in place).</summary>
    internal sealed class Tile
    {
        public int Shift; // index of the shift
        public double Start; // the shift's first tile of this group
        public double Time;
        public string? Sub;
    }

    /// <summary>
    /// Give each tile one sub-task so that <paramref name="counts"/> are met, the way the load
    /// board's allocator gives out pallets: categories in priority order, then
    /// biggest first; one owner each, spare people to the category with the most
    /// work per head; owners picked earliest start first. Then, quarter-hour by
    /// quarter-hour, each person works their own category through and, when it
    /// runs out, moves to the best-priority category still outstanding (most work
    /// left among equals). So one person's tiles read as runs, not confetti.
    /// </summary>
    internal static void HandOut(List<Tile> tiles, JsMap<string, double> counts, JsMap<string, double> priority, IReadOnlyList<string> order)
    {
        var remaining = new JsMap<string, double>(counts.Where(kv => kv.Value > 0));
        if (tiles.Count == 0 || remaining.Size == 0) return;
        double Rank(string k)
        {
            for (var i = 0; i < order.Count; i++)
                if (order[i] == k) return i;
            return -1;
        }
        double Pr(string k) => priority.GetOr(k, 100);
        double CatOrder(string a, string b) =>
            (Pr(a) - Pr(b)).Or(remaining[b] - remaining[a]).Or(Rank(a) - Rank(b));
        var cats = Js.Sorted(remaining.Keys(), CatOrder);

        // The crew: everyone with a tile of the group, earliest first.
        var byShift = new JsMap<int, List<Tile>>();
        foreach (var t in tiles) byShift.Set(t.Shift, [.. byShift.GetOr(t.Shift, []), t]);
        var crew = Js.Sorted(
            byShift.Entries().Select(e => new CrewTiles
            {
                Shift = e.Key,
                Queue = new Queue<Tile>(Js.Sort(e.Value, (a, b) => a.Time - b.Time)),
                Start = Js.Min(e.Value.Select(t => t.Time)),
            }),
            (a, b) => (a.Start - b.Start).Or(a.Shift - b.Shift));

        var headcount = new JsMap<string, double>(cats.Select(c => (c, 0.0)));
        if (crew.Count >= cats.Count)
        {
            foreach (var c in cats) headcount.Set(c, 1);
            for (var spare = crew.Count - cats.Count; spare > 0; spare--)
            {
                var worst = cats[0];
                foreach (var c in cats)
                    if (remaining[c] / headcount[c] > remaining[worst] / headcount[worst]) worst = c;
                headcount.Set(worst, headcount[worst] + 1);
            }
        }
        else
        {
            for (var i = 0; i < crew.Count; i++) headcount.Set(cats[i], 1);
        }
        var active = new JsMap<int, string>();
        var unassigned = new Queue<CrewTiles>(crew);
        foreach (var c in cats)
            for (var k = 0; k < headcount[c] && unassigned.Count > 0; k++) active.Set(unassigned.Dequeue().Shift, c);

        CrewTiles? Next()
        {
            CrewTiles? best = null;
            foreach (var p in crew)
                if (p.Queue.Count > 0 && (best is null || p.Queue.Peek().Time < best.Queue.Peek().Time ||
                        (p.Queue.Peek().Time == best.Queue.Peek().Time && (p.Start < best.Start || (p.Start == best.Start && p.Shift < best.Shift)))))
                    best = p;
            return best;
        }
        for (var p = Next(); p is not null && remaining.Size > 0; p = Next())
        {
            var cat = active.Get(p.Shift);
            if (cat is null || !remaining.Has(cat))
            {
                cat = Js.Sorted(remaining.Keys(), CatOrder)[0];
                active.Set(p.Shift, cat);
            }
            p.Queue.Dequeue().Sub = cat;
            var left = remaining[cat] - 1;
            if (left > 0) remaining.Set(cat, left);
            else remaining.Delete(cat);
        }
    }

    private sealed class CrewTiles
    {
        public int Shift;
        public Queue<Tile> Queue = new();
        public double Start;
    }

    // ─── Reset: matched to the tasks Stage 1 planned ────────────────────────

    /// <summary>
    /// One unit per quarter-hour of Reset Stage 1 planned, with the window of the
    /// job it belongs to and whether it is Re-runs or Decarding. A job holding
    /// both kinds of task (tasks sharing a window are planned as one job) is split
    /// between them by the tasks' minutes. A plan held from before Stage 1
    /// recorded Reset's jobs is read from its Reset task list instead.
    /// </summary>
    public static List<ResetUnit> ResetUnits(ResetSource s1, JsMap<string, string?>? mapping = null)
    {
        string SubOf(string name) => Catalogue.ResetSubtaskFor(name, mapping?.Get(name));
        bool IsReRun(string name) => SubOf(name) == SubtaskKeys.ReRuns;
        var minutesOf = new JsMap<string, double>((s1.ResetTasks ?? []).Select(t => (t.Name, t.Minutes)));
        var recorded = (s1.UnitWindows ?? []).Where(u => u.Group == TaskGroups.Reset).ToList();
        var units = new List<ResetUnit>();
        if (recorded.Count > 0)
        {
            var jobs = new JsMap<string, ResetJob>();
            foreach (var u in recorded)
            {
                var names = u.Named && u.Names is { Count: > 0 } ? u.Names : null;
                var key = $"{Js.NumberToString(u.Start)}|{Js.NumberToString(u.End)}|{(names is null ? "" : string.Join("§", names))}";
                var j = jobs.Get(key) ?? new ResetJob { Lo = u.Start, Hi = u.End, Names = names };
                j.N++;
                jobs.Set(key, j);
            }
            foreach (var j in jobs.Values())
            {
                var rr = (j.Names ?? []).Where(IsReRun).Aggregate(0.0, (a, n) => a + minutesOf.GetOr(n, 1));
                var dc = (j.Names ?? []).Where(n => !IsReRun(n)).Aggregate(0.0, (a, n) => a + minutesOf.GetOr(n, 1));
                var split = j.Names is not null
                    ? LargestRemainder(j.N, [(SubtaskKeys.ReRuns, rr), (SubtaskKeys.Decarding, dc)])
                    : new JsMap<string, double>().Set(SubtaskKeys.Decarding, j.N);
                foreach (var sub in new[] { SubtaskKeys.ReRuns, SubtaskKeys.Decarding })
                    for (var k = 0; k < split.GetOr(sub, 0); k++) units.Add(new ResetUnit(j.Lo, j.Hi, sub));
            }
            return units;
        }
        // Held from before: the planned Reset quarter-hours shared between the
        // day's tasks by their minutes, each inside its own window.
        var planned = (s1.Blocks ?? []).Where(b => b.Group == TaskGroups.Reset).Aggregate(0.0, (a, b) => a + b.Headcount);
        var tasks = (s1.ResetTasks ?? []).Where(t => t.Minutes > 0).ToList();
        if (tasks.Count == 0) return [];
        var taskSplit = LargestRemainder(planned, tasks.Select((t, i) => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), t.Minutes)).ToList());
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            for (var k = 0; k < taskSplit.GetOr(i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0); k++)
                units.Add(new ResetUnit(t.WindowStart, t.WindowEnd, SubOf(t.Name)));
        }
        return units;
    }

    private sealed class ResetJob
    {
        public double Lo;
        public double Hi;
        public IReadOnlyList<string>? Names;
        public double N;
    }

    private sealed class OpenUnit
    {
        public required ResetUnit Unit;
        public bool Done;
    }

    /// <summary>
    /// Reset tiles take the label of the planned quarter-hour they do, matched as
    /// the day checker matches Admin: a tile goes to the open unit whose window
    /// holds it and closes first; a tile outside every window takes the nearest
    /// unmatched unit; any left over are Decarding. Tiles already labelled keep
    /// their label and use up a unit of their own kind.
    /// </summary>
    internal static void MatchReset(List<Tile> tiles, IReadOnlyList<ResetUnit> units)
    {
        var open = units.Select(u => new OpenUnit { Unit = u }).ToList();
        var sorted = Js.Sorted(tiles, (a, b) => (a.Time - b.Time).Or(a.Shift - b.Shift));
        static double Dist(ResetUnit u, double t) => t < u.Lo ? u.Lo - t : t >= u.Hi ? t - u.Hi + 1 : 0;
        OpenUnit? Take(double t, Func<ResetUnit, bool> ok, bool inside)
        {
            OpenUnit? best = null;
            foreach (var u in open)
            {
                if (u.Done || !ok(u.Unit)) continue;
                if (inside)
                {
                    if (u.Unit.Lo <= t && t < u.Unit.Hi && (best is null || u.Unit.Hi < best.Unit.Hi)) best = u;
                }
                else if (best is null || Dist(u.Unit, t) < Dist(best.Unit, t)) best = u;
            }
            if (best is not null) best.Done = true;
            return best;
        }
        foreach (var t in sorted.Where(x => !string.IsNullOrEmpty(x.Sub)).ToList())
            if (Take(t.Time, u => u.Sub == t.Sub, true) is null) Take(t.Time, u => u.Sub == t.Sub, false);
        var todo = sorted.Where(x => string.IsNullOrEmpty(x.Sub)).ToList();
        var left = new List<Tile>();
        foreach (var t in todo)
        {
            var u = Take(t.Time, _ => true, true);
            if (u is not null) t.Sub = u.Unit.Sub;
            else left.Add(t);
        }
        foreach (var t in left) t.Sub = Take(t.Time, _ => true, false)?.Unit.Sub ?? SubtaskKeys.Decarding;
    }

    // ─── A day ───────────────────────────────────────────────────────────────

    /// <summary>Each sub-task's share of its group (not Reset): settings, or the load board's average day.</summary>
    public static List<(string Key, double Weight)> GroupWeights(string group, LabelContext ctx)
    {
        var subs = Catalogue.SubtasksOf(group);
        if (group == TaskGroups.LoadRunning && ctx.Load is not null)
        {
            // Ambient's last part (D's) is weighed on its own and labelled Ambient.
            var w = subs.Select(s => s.Key).Append(Catalogue.AmbientLastPart)
                .Select(k => (Key: k, Weight: ctx.Load.Minutes.GetOr(k, 0)))
                .ToList();
            if (w.Any(x => x.Weight > 0)) return w.Where(x => x.Key != Catalogue.AmbientLastPart || x.Weight > 0).ToList();
        }
        // No building except on build days: the day's Specials is Tidy and
        // Running, in the ratio of their two shares.
        var build = Catalogue.SpecialsBuildingDays.Contains(Period.DowMon0(ctx.Date));
        return subs
            .Where(s => group != TaskGroups.Specials || build || s.Key != SubtaskKeys.Building)
            .Select(s => (s.Key, s.ShareRule is not null ? (ctx.Rules.Get(s.ShareRule) ?? double.NaN).Or(0) : 0))
            .ToList();
    }

    /// <summary>
    /// The day's shifts' tiles with every tile of the four groups labelled, one
    /// list of blocks per shift in the order given. Labels the tiles carry
    /// already are kept when they belong to the tile's group (an edit's choice);
    /// any other label is dropped. Only <c>Sub</c> is ever written: groups and
    /// times are returned exactly as they came, as new DisplayBlocks.
    /// </summary>
    public static List<List<DisplayBlock>> LabelBlocks(IReadOnlyList<IReadOnlyList<DisplayBlock>> shifts, LabelContext ctx)
    {
        // Every tile as a mutable label; a label of another group is dropped.
        var tilesByShift = shifts
            .Select((blocks, i) => blocks
                .Select(b => new Tile { Shift = i, Time = b.Time, Sub = Catalogue.IsSubtaskOf(b.Group, b.Sub) ? b.Sub : null })
                .ToList())
            .ToList();
        List<Tile> TilesOf(string group)
        {
            var tiles = new List<Tile>();
            for (var i = 0; i < shifts.Count; i++)
            {
                var mine = new List<Tile>();
                for (var j = 0; j < shifts[i].Count; j++)
                    if (shifts[i][j].Group == group) mine.Add(tilesByShift[i][j]);
                if (mine.Count == 0) continue;
                var start = Js.Min(mine.Select(t => t.Time));
                foreach (var t in mine)
                {
                    t.Start = start;
                    tiles.Add(t);
                }
            }
            return tiles;
        }

        MatchReset(TilesOf(TaskGroups.Reset), ctx.Stage1 is not null ? ResetUnits(ctx.Stage1, ctx.ResetMapping) : []);

        foreach (var group in new[] { TaskGroups.LoadRunning, TaskGroups.Cleaning, TaskGroups.Specials })
        {
            var tiles = TilesOf(group);
            var todo = tiles.Where(t => string.IsNullOrEmpty(t.Sub)).ToList();
            if (todo.Count == 0) continue;
            var weights = GroupWeights(group, ctx);
            var ideal = LargestRemainder(tiles.Count, weights);
            var have = new JsMap<string, double>();
            foreach (var t in tiles)
                if (!string.IsNullOrEmpty(t.Sub)) have.Set(t.Sub, have.GetOr(t.Sub, 0) + 1);
            var deficit = weights.Select(w => (w.Key, Js.Max(0, ideal[w.Key] - have.GetOr(w.Key, 0)))).ToList();
            var counts = LargestRemainder(todo.Count, deficit.Any(d => d.Item2 > 0) ? deficit : weights);
            var priority = new JsMap<string, double>();
            if (group == TaskGroups.LoadRunning)
            {
                foreach (var (k, v) in Catalogue.DefaultLoadPriority) priority.Set(k, v);
                if (ctx.Load is not null)
                    foreach (var (k, v) in ctx.Load.Priority) priority.Set(k, v);
                // Ambient's last part (D's) after every other part of the load.
                priority.Set(Catalogue.AmbientLastPart, 9007199254740991); // Number.MAX_SAFE_INTEGER
            }
            HandOut(todo, counts, priority, [.. Catalogue.SubtasksOf(group).Select(s => s.Key), Catalogue.AmbientLastPart]);
            foreach (var t in todo)
                if (t.Sub == Catalogue.AmbientLastPart) t.Sub = SubtaskKeys.Ambient;
        }

        return shifts
            .Select((blocks, i) => blocks.Select((b, j) => new DisplayBlock(b.Time, b.Group, tilesByShift[i][j].Sub)).ToList())
            .ToList();
    }

    /// <summary>
    /// LabelBlocks for any shift type: <paramref name="blocksOf"/> reads a shift's tiles and
    /// <paramref name="withBlocks"/> gives a copy of the shift with the labelled ones.
    /// </summary>
    public static List<T> LabelShifts<T>(
        IReadOnlyList<T> shifts,
        LabelContext ctx,
        Func<T, IReadOnlyList<DisplayBlock>> blocksOf,
        Func<T, List<DisplayBlock>, T> withBlocks)
    {
        var labelled = LabelBlocks(shifts.Select(blocksOf).ToList(), ctx);
        return shifts.Select((s, i) => withBlocks(s, labelled[i])).ToList();
    }

    /// <summary>The roster's shifts, copied, with every tile of the four groups labelled (see LabelBlocks).</summary>
    public static List<Shift> LabelShifts(IReadOnlyList<Shift> shifts, LabelContext ctx) =>
        LabelShifts(shifts, ctx, ShiftTiles.BlocksOf, ShiftTiles.WithBlocks);

    /// <summary>The plan's shifts, copied, with every tile of the four groups labelled (see LabelBlocks).</summary>
    public static List<PlanShift> LabelShifts(IReadOnlyList<PlanShift> shifts, LabelContext ctx) =>
        LabelShifts(shifts, ctx, s => s.Blocks, (s, b) => s with { Blocks = b });

    /// <summary>Does any tile of the four groups still lack a label?</summary>
    public static bool NeedsLabels(IEnumerable<IEnumerable<DisplayBlock>> shifts) =>
        shifts.Any(s => s.Any(b => Catalogue.IsSubtaskGroup(b.Group) && !Catalogue.IsSubtaskOf(b.Group, b.Sub)));

    /// <inheritdoc cref="NeedsLabels(IEnumerable{IEnumerable{DisplayBlock}})"/>
    public static bool NeedsLabels(IEnumerable<Shift> shifts) =>
        shifts.Any(s => s.Blocks.Any(b => Catalogue.IsSubtaskGroup(b.Group) && !Catalogue.IsSubtaskOf(b.Group, b.Sub)));

    /// <inheritdoc cref="NeedsLabels(IEnumerable{IEnumerable{DisplayBlock}})"/>
    public static bool NeedsLabels(IEnumerable<PlanShift> shifts) => NeedsLabels(shifts.Select(s => s.Blocks));

    /// <summary>Tiles of each sub-task, counted from the shifts.</summary>
    public static JsMap<string, double> SubtaskTiles(IEnumerable<IEnumerable<DisplayBlock>> shifts) =>
        SubtaskTiles(shifts.SelectMany(s => s).Select(b => (b.Group, b.Sub)));

    /// <inheritdoc cref="SubtaskTiles(IEnumerable{IEnumerable{DisplayBlock}})"/>
    public static JsMap<string, double> SubtaskTiles(IEnumerable<Shift> shifts) =>
        SubtaskTiles(shifts.SelectMany(s => s.Blocks).Select(b => (b.Group, b.Sub)));

    /// <inheritdoc cref="SubtaskTiles(IEnumerable{IEnumerable{DisplayBlock}})"/>
    public static JsMap<string, double> SubtaskTiles(IEnumerable<PlanShift> shifts) => SubtaskTiles(shifts.Select(s => s.Blocks));

    private static JsMap<string, double> SubtaskTiles(IEnumerable<(string Group, string? Sub)> tiles)
    {
        var out_ = new JsMap<string, double>();
        foreach (var (group, sub) in tiles)
            if (!string.IsNullOrEmpty(sub) && Catalogue.SubtaskByKey.TryGetValue(sub, out var s) && s.Group == group)
                out_.Set(sub, out_.GetOr(sub, 0) + 1);
        return out_;
    }
}

/// <summary>The roster Shift's tiles as DisplayBlocks, and back: what the labeller reads and writes.</summary>
public static class ShiftTiles
{
    public static IReadOnlyList<DisplayBlock> BlocksOf(Shift s) => s.Blocks.Select(b => new DisplayBlock(b.Time, b.Group, b.Sub)).ToList();

    /// <summary>A copy of the shift with these tiles (the TypeScript's <c>{ ...s, blocks }</c>).</summary>
    public static Shift WithBlocks(Shift s, IReadOnlyList<DisplayBlock> blocks)
    {
        var copy = CopyOf(s);
        copy.Blocks = blocks.Select(b => new ShiftBlock(b.Time, b.Group, b.Sub)).ToList();
        return copy;
    }

    // A field-for-field copy of a roster shift (its blocks list is replaced by
    // the caller).
    private static Shift CopyOf(Shift s) => new()
    {
        Role = s.Role,
        Start = s.Start,
        End = s.End,
        Blocks = s.Blocks,
        Restricted = s.Restricted,
        Unfilled = s.Unfilled,
        Fixed = s.Fixed,
        EmployeeId = s.EmployeeId,
        Name = s.Name,
        BreakMinutes = s.BreakMinutes,
        Locked = s.Locked,
    };
}
