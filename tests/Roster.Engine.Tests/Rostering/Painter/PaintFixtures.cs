// New: the small builders the painter's TypeScript tests declare inline (shift,
// demandRange, demand, legalBreaks), shared by the ported test classes.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Painter;

internal static class PaintFixtures
{
    public const double H = 60;

    public static readonly IReadOnlyList<string> Priority = TaskGroups.TaskPriority;

    /// <summary><c>{ role, start, end, blocks: [], ...over }</c> (times in minutes).</summary>
    public static PaintableShift Shift(string role, double start, double end, bool fix = false, bool unfilled = false, params string[] restricted) =>
        new()
        {
            Role = role,
            Start = start,
            End = end,
            Blocks = [],
            Fixed = fix,
            Unfilled = unfilled,
            Restricted = restricted.Length > 0 ? restricted.ToList() : null,
        };

    /// <summary>A task-group record, in the key order given.</summary>
    public static JsMap<string, double> G(params (string Group, double N)[] groups)
    {
        var m = new JsMap<string, double>();
        foreach (var (g, n) in groups) m.Set(g, n);
        return m;
    }

    public static DemandBlock D(double time, double total, JsMap<string, double> groups) => new(time, total, groups);

    /// <summary>demandRange: one entry per quarter-hour in [start, end), each with a copy of the groups.</summary>
    public static List<DemandBlock> DemandRange(double start, double end, double total, JsMap<string, double> groups)
    {
        var d = new List<DemandBlock>();
        for (var m = start; m < end; m += 15) d.Add(new DemandBlock(m, total, groups.Copy()));
        return d;
    }

    /// <summary>
    /// engine.noblank.test.ts' demand(): one entry per quarter-hour in [from, to)
    /// HOURS, adding each part's groups to that minute; total = the groups' sum.
    /// </summary>
    public static List<DemandBlock> Demand(params (double From, double To, JsMap<string, double> Groups)[] parts)
    {
        var byTime = new JsMap<double, JsMap<string, double>>();
        foreach (var (from, to, groups) in parts)
            for (var m = from * H; m < to * H; m += 15)
            {
                var g = byTime.GetOr(m, new JsMap<string, double>());
                foreach (var (k, n) in groups) g.Set(k, g.GetOr(k, 0) + n);
                byTime.Set(m, g);
            }
        return Js.Sorted(byTime.Entries(), (a, b) => a.Key - b.Key)
            .Select(e => new DemandBlock(e.Key, e.Value.Values().Sum(), e.Value))
            .ToList();
    }

    public static int Count(PaintableShift s, string group) => s.Blocks.Count(b => b.Group == group);

    public static int Count(IEnumerable<PaintableShift> shifts, string group) => shifts.Sum(s => Count(s, group));

    public static List<double> BreakTimes(PaintableShift s) => s.Blocks.Where(b => b.Group == "break").Select(b => b.Time).ToList();

    /// <summary>Every shift's break tiles are legal for its entitlement and timing.</summary>
    public static void LegalBreaks(IEnumerable<PaintableShift> shifts)
    {
        foreach (var s in shifts)
        {
            var entitled = Breaks.BreakForRole(s.Role, (s.End - s.Start) / 60).TotalMinutes;
            var error = Breaks.BreakTileError(BreakTimes(s), entitled, s.Start, s.End);
            Assert.True(error == null, $"{s.Role} {s.Start}-{s.End}: {error}");
        }
    }

    public static PaintResult<PaintableShift> Paint(
        List<PaintableShift> shifts,
        List<DemandBlock> demand,
        Func<double, IReadOnlyList<string>?>? allowedAt = null,
        PaintOptions? opts = null) =>
        TaskPainter.PaintTasks(shifts, demand, Priority, allowedAt, opts);
}
