// Port of src/lib/stage2/engine.ts.
// ─── Stage 2 shared primitives: roles, eligibility, task painting ────────────
// The shift STRUCTURE comes from the plan shaper (shaper.ts) and real names are
// attached by the fortnight filler (fortnight.ts). This module holds what both
// sides (and the Task view) share: role/task vocabulary, eligibility rules and
// the task painter that colours each shift's 15-minute blocks from the demand
// curve - one painter, so plan tiles and roster tiles can never disagree.
//
// The vocabulary (Role, TaskGroup, MANAGER_ROLES, TASK_PRIORITY, eligibleFor,
// BreakRule, ShiftBlock, Shift, PaintableShift, Stage2Result) is Core's
// (EngineTypes.cs, Constants.cs); this is the painter, its rules and its flags.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Painter;

public static partial class TaskPainter
{
    /// <summary>
    /// The order a person of this role should pick up work in.
    ///
    /// Scarcest skill FIRST, then the spec's priority order. Straight
    /// TASK_PRIORITY put Load Running above Admin for everyone, so on a
    /// load-heavy morning the only manager on the floor was pulled into Load
    /// Running - work any team member could have done - and the manager-only
    /// Admin demand behind it went unfilled. Worse, the make-up pass then
    /// relocated that Admin to a spare block later in the day, which is how a
    /// task windowed to finish by 08:30 (the Monday date checks) ended up painted
    /// at 11:00. Giving the scarce resource the scarce work first is both what a
    /// store actually does and what keeps windowed tasks inside their window.
    /// </summary>
    public static List<string> CandidateOrder(string role, IReadOnlyList<string> priority) =>
        Js.Sort(
            priority.Where(g => g != TaskGroups.Checkout && TaskGroups.EligibleFor(role, g)).ToList(),
            (a, b) => ((double)(TaskGroups.EligibleRoleCount(a) - TaskGroups.EligibleRoleCount(b)))
                .Or(IndexOf(priority, a) - IndexOf(priority, b)));

    /// <summary>The flag for a day whose no-blank pass ran past its work limit.</summary>
    public const string SearchCapFlag =
        "Clearing blank time hit its work limit and took a shortcut - some of the blank quarter-hours flagged today might have been clearable";

    private static readonly IReadOnlyDictionary<string, string> LostLabel = new Dictionary<string, string>
    {
        [TaskGroups.Checkout] = "Tills",
        [TaskGroups.LoadRunning] = "Load running",
        [TaskGroups.Reset] = "Reset",
        [TaskGroups.Admin] = "Admin",
        [TaskGroups.Specials] = "Specials",
        [TaskGroups.Cleaning] = "Cleaning",
        [TaskGroups.NonOp] = "Non-operational hours",
    };

    /// <summary>
    /// The flag for planned work that is on nobody's shift. Every such quarter-hour
    /// used to vanish without a word - the load not run, a till unmanned - while
    /// the shaper's own flag sometimes said the crew could break "without losing
    /// the work" (Roster Engine Review 07/11/2026, A01-7, A02-5).
    /// </summary>
    public static string LostWorkFlag(string group, double blocks)
    {
        var h = Js.ToFixed(blocks * Grid.BlockMin / 60, 2);
        return group == TaskGroups.Checkout
            ? $"Tills: {h}h of register time has nobody on it - no one could take the till while the crew took its breaks"
            : $"{(LostLabel.TryGetValue(group, out var label) ? label : "undefined")}: {h}h of today's plan is on nobody's shift - no one was free inside its window to make up the work breaks displaced";
    }

    /// <summary>The flag for blank quarter-hours left in one shift, e.g. "Rory Nolan 07:00–14:15: ...".</summary>
    public static string BlankFlag(string who, double start, double end, IEnumerable<double> times)
    {
        var sorted = Js.Sorted(times, (a, b) => a - b);
        var runs = new List<double[]>();
        foreach (var t in sorted)
        {
            var last = runs.Count > 0 ? runs[^1] : null;
            if (last != null && last[1] == t) last[1] = t + Grid.BlockMin;
            else runs.Add([t, t + Grid.BlockMin]);
        }
        var at = string.Join(", ", runs.Select(r => $"{Grid.MinToTime(r[0])}–{Grid.MinToTime(r[1])}"));
        var n = sorted.Count;
        return $"{who} {Grid.MinToTime(start)}–{Grid.MinToTime(end)}: {n} quarter-hour{(n == 1 ? "" : "s")} with no work " +
               $"({at}) that could not be cleared within the rules";
    }

    /// <summary>
    /// May work demanded at `from` be made up at `to`, by the windows of the day's
    /// named tasks? The make-up pass's rule (see PaintTasks), shared with the
    /// roster's step that moves a shift's edge work into a half-hour a break left
    /// empty, so both judge a move the same way. The rule reads (group, from, to).
    /// </summary>
    public static Func<string, double, double, bool> TaskWindowRule(PaintOptions? opts) =>
        TaskWindowRule(opts?.TaskWindows, opts?.UnitWindows, opts?.AdminDeadline);

    /// <inheritdoc cref="TaskWindowRule(PaintOptions?)"/>
    public static Func<string, double, double, bool> TaskWindowRule(
        JsMap<string, List<MinuteWindow>>? taskWindows,
        JsMap<string, JsMap<double, List<JobWindow>>>? unitWindows,
        MinuteWindow? adminDeadline)
    {
        var plannedWins = new Dictionary<string, List<MinuteWindow>>();
        var genericAt = new HashSet<double>();
        // The job windows of the Admin quarter-hours Stage 1 planned at each time.
        var adminOwnAt = new Dictionary<double, List<JobWindow>>();
        var unitAdmin = unitWindows?.Get(TaskGroups.Admin);
        if (unitAdmin != null)
            foreach (var (t, ws) in unitAdmin)
                adminOwnAt[t] = ws.Where(w => w.Named != false).ToList();
        if (unitWindows != null)
            foreach (var (g, byTime) in unitWindows)
            {
                var seen = new JsMap<(double, double), MinuteWindow>();
                foreach (var (t, ws) in byTime)
                    foreach (var w in ws)
                    {
                        if (w.Named == false)
                        {
                            if (g == TaskGroups.Admin) genericAt.Add(t);
                            continue;
                        }
                        seen.Set((w.Start, w.End), new MinuteWindow(w.Start, w.End));
                    }
                plannedWins[g] = seen.Values().ToList();
            }
        // `from` is where the work is now, `to` where it would go.
        // Moving Admin out of the date checks' window took the checks past their
        // deadline - the opening manager went onto the load at 07:30 and the
        // quarter-hour was made up at 09:45, inside a later job's window (G258
        // 09/11: 105 of 120 minutes by 09:30).
        var deadline = adminDeadline;
        return (g, from, to) =>
        {
            if (g == TaskGroups.NonOp || g == TaskGroups.Reset) return true;
            if (g == TaskGroups.Admin && deadline != null && from >= deadline.Start && from < deadline.End &&
                (to < deadline.Start || to >= deadline.End))
                return false;
            if (g == TaskGroups.Admin && genericAt.Contains(from)) return true;
            // Admin planned at `from` stays inside its own job's window: another
            // job's long window (Safe Handover's whole day) is no licence to move it.
            var own = g == TaskGroups.Admin && adminOwnAt.TryGetValue(from, out var o) ? o : null;
            if (own is { Count: > 0 }) return own.Any(w => to >= w.Start && to < w.End);
            IReadOnlyList<MinuteWindow>? wins = plannedWins.TryGetValue(g, out var planned) ? planned : taskWindows?.Get(g);
            if (wins == null || (wins.Count == 0 && !(g == TaskGroups.Admin && unitAdmin != null))) return true;
            return wins.Any(w => from >= w.Start && from < w.End && to >= w.Start && to < w.End);
        };
    }

    /// <summary>
    /// Paints each shift's 15-minute blocks with a task group from the day's
    /// demand, places every shift's break, marks leftover undirected demand as
    /// explicit non_op tiles, makes up the work breaks displaced, and clears
    /// blank quarter-hours by moving shift edges within the rules. Mutates the
    /// shifts (blocks, and the start/end of shifts that are not fixed); returns
    /// what could not be finished. Unfilled shifts are left untouched.
    /// </summary>
    /// <param name="allowedAt">The task groups permitted at a minute (labour-group windows); null at a minute = no restriction.</param>
    public static PaintResult<S> PaintTasks<S>(
        IReadOnlyList<S> shifts,
        IReadOnlyList<DemandBlock> demandByBlock,
        IReadOnlyList<string> priority,
        Func<double, IReadOnlyList<string>?>? allowedAt = null,
        PaintOptions? opts = null) where S : PaintableShift =>
        Paint(shifts, demandByBlock, priority, allowedAt, opts);

    // priority.indexOf(g): the first index, -1 when absent.
    private static double IndexOf(IReadOnlyList<string> list, string g)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == g) return i;
        return -1;
    }
}
