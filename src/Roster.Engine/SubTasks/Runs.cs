// Port of src/lib/subtasks/runs.ts.
// ─── Sub-tasks: one person's day in codes ────────────────────────────────────
// The printed roster has room for a short line under each shift: the sub-task
// codes the person works, in the order they first work them ("PR AM FE").
// Pure.

using Roster.Engine.Core;

namespace Roster.Engine.SubTasks;

public static class Runs
{
    /// <summary>The codes of a shift's sub-tasks, each once, in the order first worked.</summary>
    public static List<string> SubtaskRun(IEnumerable<DisplayBlock> blocks) => SubtaskRun(blocks.Select(b => (b.Time, b.Group, b.Sub)));

    /// <inheritdoc cref="SubtaskRun(IEnumerable{DisplayBlock})"/>
    public static List<string> SubtaskRun(IEnumerable<ShiftBlock> blocks) => SubtaskRun(blocks.Select(b => (b.Time, b.Group, b.Sub)));

    private static List<string> SubtaskRun(IEnumerable<(double Time, string Group, string? Sub)> blocks)
    {
        var out_ = new List<string>();
        foreach (var b in Js.Sorted(blocks, (x, y) => x.Time - y.Time))
        {
            if (!Catalogue.IsSubtaskOf(b.Group, b.Sub)) continue;
            var code = Catalogue.SubtaskByKey[b.Sub!].Code;
            if (!out_.Contains(code)) out_.Add(code);
        }
        return out_;
    }
}
