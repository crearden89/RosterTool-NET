// New: shared builders for the ported sub-task tests (the TypeScript tests'
// `shift(...)`, `seq`, `switches` and expectAddsUp helpers).

using Roster.Engine.Core;
using Roster.Engine.SubTasks;

namespace Roster.Engine.Tests.SubTasks;

internal static class SubTaskTestKit
{
    public static readonly DateOnly Mon = DateOnlyExtensions.ParseIso("2026-11-09");
    public static readonly DateOnly Tue = DateOnlyExtensions.ParseIso("2026-11-10");
    public static readonly DateOnly Fri = DateOnlyExtensions.ParseIso("2026-11-13");

    public static LabelContext Ctx(DateOnly? date = null, ResetSource? stage1 = null, LoadSplit? load = null) =>
        new() { Date = date ?? Mon, Stage1 = stage1, Load = load, Rules = RosteringRules.Default };

    /// <summary>A shift's tiles: `group` from `from` for `n` quarter-hours, run after run.</summary>
    public static List<DisplayBlock> Tiles(params (string Group, double From, int N)[] runs)
    {
        var blocks = new List<DisplayBlock>();
        foreach (var (group, from, n) in runs)
            for (var k = 0; k < n; k++) blocks.Add(new DisplayBlock(from + k * 15, group));
        return blocks;
    }

    public static JsMap<string, double> Map(params (string K, double V)[] kv) => new(kv.Select(x => (x.K, x.V)));

    /// <summary>Labels of one shift's tiles of a group, in time order.</summary>
    public static List<string?> Seq(IReadOnlyList<DisplayBlock> s, string group) =>
        s.Where(b => b.Group == group).OrderBy(b => b.Time).Select(b => b.Sub).ToList();

    /// <summary>How many times the label changes along a run.</summary>
    public static int Switches(IReadOnlyList<string?> xs) => xs.Skip(1).Where((x, i) => x != xs[i]).Count();

    public static int Count(IEnumerable<IEnumerable<DisplayBlock>> shifts, string group) => shifts.Sum(s => s.Count(b => b.Group == group));

    public static void ExpectAddsUp(IReadOnlyList<IReadOnlyList<DisplayBlock>> before, IReadOnlyList<IReadOnlyList<DisplayBlock>> after)
    {
        // Groups and times exactly as they came; labels only.
        Assert.Equal(before.Select(s => s.Select(b => $"{b.Time}:{b.Group}")), after.Select(s => s.Select(b => $"{b.Time}:{b.Group}")));
        var subs = Label.SubtaskTiles(after);
        foreach (var (g, sts) in Catalogue.Subtasks)
            Assert.Equal(Count(after, g), sts.Sum(st => subs.GetOr(st.Key, 0)));
        foreach (var s in after)
            foreach (var b in s)
                if (Catalogue.IsSubtaskGroup(b.Group)) Assert.True(Catalogue.IsSubtaskOf(b.Group, b.Sub), $"{b.Group} {b.Sub}");
                else Assert.Null(b.Sub);
        Assert.False(Label.NeedsLabels(after));
    }
}
