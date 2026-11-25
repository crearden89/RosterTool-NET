// Port of src/lib/stage2/breakGap.ts.
// ─── A half-hour a break left empty, filled from another shift's edge ────────
// The plan prices each shift with the breaks of the role it was cut for, and
// the roster puts real people in those shifts. A store, assistant or duty
// manager takes one 30-minute break at any length; a store assistant on a
// shift over six hours takes two. The roster puts managers in store
// assistants' shifts (each store has 4 to 6 managers and two manager shifts a
// day), and there the half-hour where the plan priced the second break has no
// work in it.
//
// The owner's decision (14/11/2026): "Fill the gap and cut from the tail/head
// of another shift". After a day is painted, each quarter-hour with no work in
// a shift whose holder is owed less break than the plan priced for it (at
// most the difference, and never time the held plan had already left blank)
// takes real work from the first or last quarter-hour of ANOTHER shift that
// day, and that shift starts a quarter-hour later or finishes a quarter-hour
// earlier. The work moved is work the person may do, at a time the painter's
// own make-up pass would allow it (the group's window there and the named
// tasks' windows: Reset and non-op loose, Specials, Cleaning and Load Running
// in their windows). Never a till (a till is manned in its own quarter-hour),
// a break, a blank or Admin (its named jobs are matched to their own windows).
//
// A cut is kept only when every rule the roster keeps still holds: the shift
// is at least its minimum length, its break tiles are still legal for its new
// length (a cut across a break tier is refused), the day's cover flags are
// exactly as they were (a manager on, nobody alone, juniors supervised), the
// staffed day still opens and closes when it did, the managers' handover is
// no shorter than its rule, and nobody is left far short of contract.
// Fixed-roster and locked shifts, juniors' shifts and the two manager shifts
// are never cut. Donors are ranked by hours above contract (a cut only saves
// pay when the donor is above it), most first, then by fewest cuts so far,
// so the cuts spread. No day is painted again: tiles move and the rules are
// checked directly. Pure.
//
// The TypeScript's GapShift is the roster's shift: FShift here (the fortnight
// passes its own shifts, which this changes in place).

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

/// <summary>The parts of a day the break-gap step reads (BreakGapDay): DayContext has them all.</summary>
public interface IBreakGapDay
{
    JsMap<double, List<string>>? AllowedGroups { get; }
    JsMap<string, List<MinuteWindow>>? TaskWindows { get; }
    JsMap<string, JsMap<double, List<JobWindow>>>? UnitWindows { get; }
    MinuteWindow? AdminDeadline { get; }
    List<HeldShift>? HeldShifts { get; }
}

/// <summary>A day for the break-gap step on its own (its tests; the fortnight passes its DayContext).</summary>
public sealed record BreakGapDay : IBreakGapDay
{
    public JsMap<double, List<string>>? AllowedGroups { get; init; }
    public JsMap<string, List<MinuteWindow>>? TaskWindows { get; init; }
    public JsMap<string, JsMap<double, List<JobWindow>>>? UnitWindows { get; init; }
    public MinuteWindow? AdminDeadline { get; init; }
    public List<HeldShift>? HeldShifts { get; init; }
}

public sealed record BreakGapRules
{
    public double MinPeopleOnFloor { get; init; }
    public double MinAdultsWithJsa { get; init; }
    /// <summary>Shortest shift, minutes (the role's own minimum applies too, the longer of the two).</summary>
    public double MinShiftMin { get; init; }
    /// <summary>The managers' handover, minutes.</summary>
    public double OverlapMinMin { get; init; }
    /// <summary>Breaks done by this time where they were (18:00 unless the store trades to 21:00); null: no cut-off.</summary>
    public double? BreakEndBy { get; init; }

    public static implicit operator CoverRules(BreakGapRules r) => new(r.MinPeopleOnFloor, r.MinAdultsWithJsa);
}

/// <summary>Where someone stands against contract for the fortnight, before today's cuts.</summary>
/// <param name="AboveH">Paid plus credited hours less contract: above contract when positive.</param>
/// <param name="FarShortH">The shortfall past which they are far short: max(4 h, 10% of contract).</param>
public sealed record Standing(double AboveH, double FarShortH);

public sealed record BreakGapCut
{
    public string Who { get; init; } = "";
    public string EmployeeId { get; init; } = "";
    public MinuteWindow From { get; init; } = new(0, 0);
    public MinuteWindow To { get; init; } = new(0, 0);
    /// <summary>Minutes the shift's break moved to stay legal (later after a later start, earlier after an earlier finish).</summary>
    public double? BreakMovedMin { get; init; }
}

public sealed record BreakGapFill(string Who, List<double> Times, List<string> Groups);

public sealed record BreakGapResult(List<BreakGapCut> Cuts, List<BreakGapFill> Fills, int Filled);

public static class BreakGaps
{
    private const double BLOCK_MIN = Grid.BlockMin;

    /// <summary>The role whose breaks the plan priced for each kind of slot.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlanBreakRole = new Dictionary<string, string>
    {
        [SlotRoles.MGR] = Roles.ASM,
        [SlotRoles.SA] = Roles.SA,
        [SlotRoles.JSA] = Roles.JSA,
    };

    private static double BreakOf(BreakRule rule, double start, double end) =>
        Breaks.BreakForShift(rule.BreakType, (end - start) / 60).TotalMinutes;

    private static double PaidOf(BreakRule rule, double start, double end) =>
        (end - start - Breaks.BreakForShift(rule.BreakType, (end - start) / 60).UnpaidMinutes) / 60;

    /// <summary>Break minutes the plan priced for a shift less what its holder is owed (0 when not less).</summary>
    public static double BreakShortfallMin(FShift s, IReadOnlyDictionary<string, BreakRule> breakRules)
    {
        if (s.Unfilled) return 0;
        var priced = BreakOf(breakRules[PlanBreakRole[s.SlotRole ?? SlotRoles.SA]], s.Start, s.End);
        return Math.Max(0, priced - BreakOf(breakRules[s.Role], s.Start, s.End));
    }

    // The opening and closing managers' overlap, as the day checker reads it.
    private static double? HandoverMin(List<FShift> shifts)
    {
        var mgrs = shifts.Where(s => !s.Unfilled && Roles.IsManager(s.Role)).ToList();
        if (mgrs.Count < 2) return null;
        var opener = Js.Sorted(mgrs, (a, b) => (a.Start - b.Start).Or(a.End - b.End))[0];
        var closer = Js.Sorted(mgrs, (a, b) => (b.End - a.End).Or(b.Start - a.Start))[0];
        if (ReferenceEquals(opener, closer)) return null;
        return Math.Min(opener.End, closer.End) - Math.Max(opener.Start, closer.Start);
    }

    private static ShiftBlock? BlockAt(FShift s, double t)
    {
        if (!(t >= s.Start && t < s.End)) return null;
        var idx = (t - s.Start) / BLOCK_MIN;
        if (idx != Math.Floor(idx) || idx >= s.Blocks.Count) return null;
        return s.Blocks[(int)idx];
    }

    private static double Cmp(double[] a, double[] b)
    {
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var d = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
            if (d != 0) return d;
        }
        return 0;
    }

    /// <summary>
    /// Fill the quarter-hours a shorter break left empty with edge work from other
    /// shifts, cutting those shifts to match. Changes `shifts` in place and
    /// returns what it did, or null when nothing could be done within the rules.
    /// `standing` gives each person's hours against contract as they stand before
    /// this day's cuts (null: never cut them); `cutsSoFar` counts each person's
    /// cuts this fortnight and is updated.
    /// </summary>
    public static BreakGapResult? FillBreakGaps(
        List<FShift> shifts,
        IBreakGapDay day,
        IReadOnlyDictionary<string, BreakRule> breakRules,
        BreakGapRules rules,
        Func<string, IReadOnlyList<string>> restrictedOf,
        Func<string, bool> isFixed,
        Func<string, Standing?> standing,
        JsMap<string, double> cutsSoFar)
    {
        var real = shifts.Where(s => !s.Unfilled).ToList();
        var gaps = real
            .Select(s => (S: s, Room: Math.Floor(BreakShortfallMin(s, breakRules) / BLOCK_MIN)))
            .Where(g => g.Room > 0 && g.S.Blocks.Any(b => b.Group == TaskGroups.Flex))
            .ToList();
        if (gaps.Count == 0) return null;

        var sameWindow = TaskPainter.TaskWindowRule(day.TaskWindows, day.UnitWindows, day.AdminDeadline);
        bool AllowedAt(string g, double t)
        {
            if (g == TaskGroups.NonOp) return true;
            if (day.AllowedGroups is null) return true;
            return (day.AllowedGroups.Get(t) ?? []).Contains(g);
        }
        string Cover(List<FShift> list) => string.Join("\n", Coverage.CoverFlags(list, rules));
        var coverBefore = Cover(real);
        var openAt = Js.Min(real.Select(s => s.Start));
        var closeAt = Js.Max(real.Select(s => s.End));
        var handoverBefore = HandoverMin(real);
        var paidLost = new JsMap<string, double>();
        var firstTimes = new JsMap<FShift, MinuteWindow>();
        var fills = new JsMap<FShift, (List<double> Times, List<string> Groups)>();
        var breakMoved = new JsMap<FShift, double>();

        // After cutting `d` to [ns, ne): its break run nearest the cut, moved a
        // quarter-hour away from it, when that makes the breaks legal again. The
        // work beside the run takes the break's old quarter-hour: never a till or
        // Admin, and only where its window allows it there. Breaks done by the
        // cut-off stay done by it.
        (double Work, double Brk)? BreakMove(FShift d, List<double> brk, bool head, double ns, double ne, BreakRule rule)
        {
            if (brk.Count == 0) return null;
            var sorted = Js.Sorted(brk, (a, b) => a - b);
            var runs = new List<double[]>();
            foreach (var t in sorted)
            {
                var last = runs.Count > 0 ? runs[^1] : null;
                if (last is not null && last[1] == t) last[1] = t + BLOCK_MIN;
                else runs.Add([t, t + BLOCK_MIN]);
            }
            var run = head ? runs[0] : runs[^1];
            var work = head ? run[1] : run[0] - BLOCK_MIN;
            var freed = head ? run[0] : run[1] - BLOCK_MIN;
            var w = BlockAt(d, work);
            if (w is null || w.Group == TaskGroups.Break || w.Group == TaskGroups.Checkout || w.Group == TaskGroups.Admin) return null;
            if (w.Group != TaskGroups.Flex && (!AllowedAt(w.Group, freed) || !sameWindow(w.Group, work, freed))) return null;
            var moved = sorted.Where(x => x != freed).Append(work).ToList();
            if (Breaks.BreakTileError(moved, BreakOf(rule, ns, ne), ns, ne) is not null) return null;
            var endBy = rules.BreakEndBy;
            if (endBy is { } eb && Js.Max(moved) + BLOCK_MIN > eb && Js.Max(sorted) + BLOCK_MIN <= eb) return null;
            return (work, freed);
        }

        bool MayGive(FShift d) =>
            !d.Unfilled &&
            !d.Locked &&
            d.SlotRole != SlotRoles.MGR &&
            d.SlotRole != SlotRoles.JSA &&
            d.Role != Roles.JSA &&
            !isFixed(d.EmployeeId);

        var filled = 0;
        foreach (var (g, room) in gaps)
        {
            // Time the held plan already had blank is not this step's to clear.
            var held = (day.HeldShifts ?? []).Where(h => h.Start == g.Start && h.End == g.End).ToList();
            bool HeldBlankAt(double t) =>
                held.Count > 0 && held.All(h => h.Blocks.Find(b => b.Time == t)?.Group == TaskGroups.Flex);
            var times = g.Blocks.Where(b => b.Group == TaskGroups.Flex && !HeldBlankAt(b.Time)).Select(b => b.Time).ToList();
            var left = room;
            foreach (var t in times)
            {
                if (left <= 0) break;
                (FShift D, bool Head, string Group, (double Work, double Brk)? Swap, double[] Key)? best = null;
                foreach (var d in real)
                {
                    if (ReferenceEquals(d, g) || !MayGive(d)) continue;
                    var st = standing(d.EmployeeId);
                    if (st is null) continue;
                    var rule = breakRules[d.Role];
                    foreach (var head in new[] { false, true })
                    {
                        var u = head ? d.Start : d.End - BLOCK_MIN;
                        var b = BlockAt(d, u);
                        if (b is null || b.Group == TaskGroups.Flex || b.Group == TaskGroups.Break || b.Group == TaskGroups.Checkout || b.Group == TaskGroups.Admin) continue;
                        var grp = b.Group;
                        if (grp != TaskGroups.NonOp && (!TaskGroups.EligibleFor(g.Role, grp) || restrictedOf(g.EmployeeId).Contains(grp))) continue;
                        if (!AllowedAt(grp, t) || !sameWindow(grp, u, t)) continue;
                        var ns = head ? d.Start + BLOCK_MIN : d.Start;
                        var ne = head ? d.End : d.End - BLOCK_MIN;
                        if (ne - ns < Math.Max(rules.MinShiftMin, Js.Round(rule.MinShiftHours * 60).Or(0))) continue;
                        // Its break tiles must still be legal for the new length: a cut
                        // across a break tier changes what the shift is owed, and is
                        // refused. A break the cut brings inside the first or last hour
                        // moves a quarter-hour inward, trading places with the work beside
                        // it where that work may be done a quarter-hour earlier or later.
                        var brk = d.Blocks.Where(x => x.Group == TaskGroups.Break).Select(x => x.Time).ToList();
                        (double Work, double Brk)? swap = null;
                        if (Breaks.BreakTileError(brk, BreakOf(rule, ns, ne), ns, ne) is not null)
                        {
                            swap = BreakMove(d, brk, head, ns, ne, rule);
                            if (swap is null) continue;
                        }
                        // Nobody tipped into far short of contract.
                        var lost = paidLost.GetOr(d.EmployeeId, 0) + PaidOf(rule, d.Start, d.End) - PaidOf(rule, ns, ne);
                        var above = st.AboveH - lost;
                        if (above < -st.FarShortH - 1e-9) continue;
                        // Cover exactly as it was, the day's open and close kept, and the
                        // managers' handover no shorter than its rule.
                        var trial = real.Select(x =>
                        {
                            if (!ReferenceEquals(x, d)) return x;
                            var c = x.Copy();
                            c.Start = ns;
                            c.End = ne;
                            return c;
                        }).ToList();
                        if (Js.Min(trial.Select(x => x.Start)) != openAt || Js.Max(trial.Select(x => x.End)) != closeAt) continue;
                        if (Cover(trial) != coverBefore) continue;
                        if (handoverBefore is { } hb)
                        {
                            var h = HandoverMin(trial);
                            if (h is null || h < Math.Min(hb, rules.OverlapMinMin)) continue;
                        }
                        // Most above contract first (a cut only saves pay there), then the
                        // fewest cuts so far, then a finish before a start.
                        double[] key = [-(st.AboveH - paidLost.GetOr(d.EmployeeId, 0)), cutsSoFar.GetOr(d.EmployeeId, 0), head ? 1 : 0];
                        if (best is null || Cmp(key, best.Value.Key) < 0) best = (d, head, grp, swap, key);
                    }
                }
                if (best is not { } pick) continue;
                {
                    var (d, head, group, swap, _) = pick;
                    var rule = breakRules[d.Role];
                    if (!firstTimes.Has(d)) firstTimes.Set(d, new MinuteWindow(d.Start, d.End));
                    if (swap is { } sw)
                    {
                        BlockAt(d, sw.Brk)!.Group = BlockAt(d, sw.Work)!.Group;
                        BlockAt(d, sw.Work)!.Group = TaskGroups.Break;
                        breakMoved.Set(d, breakMoved.GetOr(d, 0) + BLOCK_MIN);
                    }
                    var ns = head ? d.Start + BLOCK_MIN : d.Start;
                    var ne = head ? d.End : d.End - BLOCK_MIN;
                    paidLost.Set(d.EmployeeId, paidLost.GetOr(d.EmployeeId, 0) + PaidOf(rule, d.Start, d.End) - PaidOf(rule, ns, ne));
                    cutsSoFar.Set(d.EmployeeId, cutsSoFar.GetOr(d.EmployeeId, 0) + 1);
                    d.Blocks = head ? d.Blocks.Skip(1).ToList() : d.Blocks.Take(d.Blocks.Count - 1).ToList();
                    d.Start = ns;
                    d.End = ne;
                    BlockAt(g, t)!.Group = group;
                    (List<double> Times, List<string> Groups) f = fills.TryGet(g, out var existing) ? existing : (new List<double>(), new List<string>());
                    f.Times.Add(t);
                    f.Groups.Add(group);
                    fills.Set(g, f);
                    filled++;
                    left--;
                }
            }
        }
        if (filled == 0) return null;
        return new BreakGapResult(
            firstTimes.Select(kv => new BreakGapCut
            {
                Who = kv.Key.Name,
                EmployeeId = kv.Key.EmployeeId,
                From = kv.Value,
                To = new MinuteWindow(kv.Key.Start, kv.Key.End),
                BreakMovedMin = breakMoved.TryGet(kv.Key, out var bm) ? bm : null,
            }).ToList(),
            fills.Select(kv => new BreakGapFill(kv.Key.Name, kv.Value.Times, kv.Value.Groups)).ToList(),
            filled);
    }

    // ─── The note on the day ─────────────────────────────────────────────────────
    // In the family of the notes on time given to breaks ("Longer shift so a
    // break doesn't cost work: ..."), a warning: it names the shift that got
    // shorter with its new times, and who picked the work up and when.

    private static readonly IReadOnlyDictionary<string, string> WorkLabel = new Dictionary<string, string>
    {
        [TaskGroups.Checkout] = "Tills",
        [TaskGroups.LoadRunning] = "Load running",
        [TaskGroups.Reset] = "Reset",
        [TaskGroups.Admin] = "Admin",
        [TaskGroups.Specials] = "Specials",
        [TaskGroups.Cleaning] = "Cleaning",
        [TaskGroups.NonOp] = "Non-operational hours",
    };

    /// <summary>"starts 15 minutes later", "finishes 30 minutes earlier".</summary>
    private static string HowCut(MinuteWindow from, MinuteWindow to)
    {
        var parts = new List<string>();
        static string Words(double m) => m == 60 ? "1 hour" : m > 60 ? $"1 hour {Js.NumberToString(m - 60)} minutes" : $"{Js.NumberToString(m)} minutes";
        if (to.Start > from.Start) parts.Add($"starts {Words(to.Start - from.Start)} later");
        if (to.End < from.End) parts.Add($"finishes {Words(from.End - to.End)} earlier");
        return string.Join(" and ", parts);
    }

    private static string Spans(IEnumerable<double> times)
    {
        var sorted = Js.Sorted(times, (a, b) => a - b);
        var runs = new List<double[]>();
        foreach (var t in sorted)
        {
            var last = runs.Count > 0 ? runs[^1] : null;
            if (last is not null && last[1] == t) last[1] = t + BLOCK_MIN;
            else runs.Add([t, t + BLOCK_MIN]);
        }
        return string.Join(", ", runs.Select(r => $"{Grid.MinToTime(r[0])}–{Grid.MinToTime(r[1])}"));
    }

    /// <summary>
    /// The roster's note when it cut shifts to fill time a shorter break left
    /// empty: "Shorter shift on the roster so a break leaves no time empty: 0.50 h
    /// - Sam now 13:30–17:00 (finishes 30 minutes earlier); Alex does that work
    /// (Reset) at 13:45–14:15, where the plan priced a break Alex is not owed".
    /// The hours check reads the hours back (BreakTime.RosterBreakCutHours).
    /// </summary>
    public static string? RosterBreakCutNote(BreakGapResult result) => RosterBreakCutNote(result.Cuts, result.Fills);

    public static string? RosterBreakCutNote(IReadOnlyList<BreakGapCut> cuts, IReadOnlyList<BreakGapFill> fills)
    {
        double min = 0;
        foreach (var c in cuts) min = min + (c.From.End - c.From.Start) - (c.To.End - c.To.Start);
        if (min <= 0) return null;
        var cut = cuts.Select(c =>
        {
            var brk = c.BreakMovedMin is { } bm && Js.Truthy(bm)
                ? $", break {Js.NumberToString(bm)} minutes {(c.To.Start > c.From.Start ? "later" : "earlier")}"
                : "";
            return $"{c.Who} now {Grid.MinToTime(c.To.Start)}–{Grid.MinToTime(c.To.End)} ({HowCut(c.From, c.To)}{brk})";
        });
        var took = fills.Select(f =>
        {
            var groups = string.Join(", ", new JsSet<string>(f.Groups).Select(g => WorkLabel[g]));
            return $"{f.Who} does that work ({groups}) at {Spans(f.Times)}, where the plan priced a break {f.Who} is not owed";
        });
        var head =
            cuts.Count == 1
                ? "Shorter shift on the roster so a break leaves no time empty"
                : "Shorter shifts on the roster so breaks leave no time empty";
        return $"{head}: {Js.ToFixed(min / 60, 2)} h - {string.Join("; ", cut)}; {string.Join("; ", took)}";
    }
}
