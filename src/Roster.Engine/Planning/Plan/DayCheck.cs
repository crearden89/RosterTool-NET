// Port of src/lib/plan/dayCheck.ts.
// ─── The day checker ─────────────────────────────────────────────────────────
// One check of a planned day, read from the shifts as they stand - when the
// plan is generated, after every edit, on the Plan page and in the Review
// (owner decisions 8, R2 and R4; build phase 4). The planner used to write its
// flags as it went, and some went stale by the end: "0.75h planned above the
// curve" for time it later trimmed, "lost to breaks" for work no break touched
// (review RC-29). An edit changed nothing at all (RC-15). Everything here is
// worked out from the finished shifts, so a flag can't outlive its cause.
//
// Pure: shared by the server (snapshots, edits, Review) and the Plan page.
// Wording decides severity - see ErrorPatterns in ReviewChecks. A rule broken
// is an error; an allowed overage or a rule a later build phase enforces is a
// warning.

using System.Text.Json.Serialization;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Painter;
using PainterCover = Roster.Engine.Rostering.Painter.CoverRules;

namespace Roster.Engine.Planning.Plan;

public sealed record DayCheckOptions
{
    /// <summary>The floor-cover rules (Rostering rules page).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoverRules? Cover { get; init; }

    /// <summary>Longest legal shift by the clock, minutes, per slot type ("MGR", "SA", "JSA").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double>? MaxClock { get; init; }

    /// <summary>Shortest legal shift, minutes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinShiftMin { get; init; }

    /// <summary>Shortest manager shift, minutes (owner decision R7).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ManagerMinShiftMin { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSchoolDay { get; init; }

    /// <summary>
    /// Hours the planner added so breaks don't cost work (master plan decision
    /// 4.2), from its note on the day: counted as explained in the hours check.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BreakTimeAdded { get; init; }
}

/// <param name="LostAt">The quarter-hours of work on nobody's shift, tills included, one per unit.</param>
public sealed record DayCheck(List<string> Flags, HoursCheck Hours, List<double> LostAt);

/// <summary>dayCheck.ts's functions (the result type keeps the TypeScript name DayCheck).</summary>
public static class DayChecker
{
    public static readonly IReadOnlyDictionary<string, string> GroupLabel = new Dictionary<string, string>
    {
        ["checkout"] = "Tills",
        ["load_running"] = "Load running",
        ["reset"] = "Reset",
        ["admin"] = "Admin",
        ["specials"] = "Specials",
        ["cleaning"] = "Cleaning",
        ["non_op"] = "Non-operational hours",
    };

    private static readonly IReadOnlyDictionary<string, string> SlotLabel = new Dictionary<string, string>
    {
        [RoleBadges.Mgr] = "Manager",
        [RoleBadges.Sa] = "Store assistant",
        [RoleBadges.Jsa] = "Junior",
    };

    private const double LateBreakCutoff = 18 * 60;
    private const double B = Grid.BlockMin;

    private static string Fmt(double m) => Grid.MinToTime(m);
    private static string Hrs(double qh) => Js.ToFixed(qh * B / 60, 2);
    private static string Label(CheckedShift s) => $"{SlotLabel[s.PlanRole]} {Fmt(s.Start)}–{Fmt(s.End)}";
    private static string GroupName(string g) => GroupLabel.TryGetValue(g, out var l) ? l : g;

    private static bool MayDo(CheckedShift s, string g) =>
        g == "admin" ? DayHours.IsManagerShift(s) : g == "checkout" ? s.PlanRole != RoleBadges.Jsa : true;

    /// <summary>
    /// The latest a shift's last break can end, as the painter may place breaks:
    /// no break in the first or last hour, the meal by the fifth hour, two
    /// segments one in each half and an hour apart. Mirrors breakPlan in the
    /// painter (lib/stage2/engine.ts).
    /// </summary>
    private static double? EarliestBreakEnd(CheckedShift s)
    {
        var len = s.End - s.Start;
        var brk = Breaks.BreakForRole(DayHours.EngineRole[s.PlanRole], len / 60);
        if (brk.TotalMinutes <= 0) return null;
        var seg = Js.Round(brk.TotalMinutes / brk.Segments / B) * B;
        if (seg <= 0 || seg * brk.Segments >= len) return null;
        double edge = 60;
        var needed = seg * brk.Segments + (brk.Segments > 1 ? 60 : 0);
        while (edge > B && len - 2 * edge < needed) edge -= B;
        var lo = s.Start + edge;
        if (brk.Segments == 1) return lo + seg;
        var mid = s.Start + Math.Floor(len / 2 / B) * B;
        return Math.Max(mid, lo + seg + 60) + seg;
    }

    private sealed class Unit
    {
        public double T;
        public double Lo;
        public double Hi;
        public bool Done;
    }

    /// <summary>
    /// Demand units matched to tiles, each unit only to a tile it may legally be
    /// done at: its own quarter-hour, or anywhere in a task window that holds it.
    /// Every window holding a time overlaps there, so a unit's reach is one
    /// stretch, and handing each tile (in time order) to the open unit whose reach
    /// closes first matches the most. Returns the unmatched units' times.
    /// </summary>
    private static List<double> Unmatched(JsMap<double, double> demand, JsMap<double, double> tiles, Func<double, int, (double Lo, double Hi)> reach)
    {
        var units = new List<Unit>();
        foreach (var (t, n) in demand)
            for (var i = 0; i < n; i++)
            {
                var (lo, hi) = reach(t, i);
                units.Add(new Unit { T = t, Lo = lo, Hi = hi });
            }
        foreach (var t in Js.Sorted(tiles.Keys(), (a, b) => a - b))
        {
            for (var k = tiles.Get(t); k > 0; k--)
            {
                Unit? best = null;
                foreach (var u in units)
                    if (!u.Done && u.Lo <= t && t < u.Hi && (best is null || u.Hi < best.Hi)) best = u;
                if (best is null) break;
                best.Done = true;
            }
        }
        return units.Where(u => !u.Done).Select(u => u.T).ToList();
    }

    private static readonly IReadOnlyDictionary<string, string> CauseText = new Dictionary<string, string>
    {
        ["depth"] = "the plan asks for more people at once than can do it",
        ["break"] = "it falls in the breaks of the people who can do it, and nobody was free to make it up inside its window",
        ["till"] = "the people who can do it were on the tills",
        ["busy"] = "everyone who can do it was busy right through its window",
    };

    private sealed class Job
    {
        public double Start;
        public double End;
        public double Minutes;
        public double Got;
        public bool Llc;
        public List<string> Names = [];
    }

    /// <summary>Every finding on the day's shifts as they stand, and its hours check.</summary>
    public static DayCheck CheckPlanDay(Stage1Computation s1, IReadOnlyList<CheckedShift> shifts, DayCheckOptions? opts = null)
    {
        opts ??= new DayCheckOptions();
        var flags = new List<string>();
        var lostAt = new List<double>();
        var cover = opts.Cover ?? DayHours.DefaultCover;
        var minShift = opts.MinShiftMin ?? 180;
        var managerMin = opts.ManagerMinShiftMin ?? 360;
        var tiles = DayHours.TileMaps(shifts);
        List<(CheckedShift S, int I)> Present(double t) =>
            shifts.Select((s, i) => (S: s, I: i)).Where(x => x.S.Start <= t && t < x.S.End).ToList();

        // ── Cover: nobody alone, a manager on, three adults with a junior (F1-F3).
        flags.AddRange(Coverage.CoverFlags(
            shifts.Select(s => new CoverShift(DayHours.EngineRole[s.PlanRole], s.Start, s.End)),
            new PainterCover(cover.MinPeopleOnFloor, cover.MinAdultsWithJsa)));

        // ── Who may do each task, and when (A1, T7; W2; J1/J2).
        for (var i = 0; i < shifts.Count; i++)
        {
            var s = shifts[i];
            var byGroup = new JsMap<string, List<double>>();
            foreach (var (t, g) in tiles[i])
                if (g != TaskGroups.Flex && g != TaskGroups.Break) byGroup.Set(g, [.. byGroup.GetOr(g, []), t]);
            foreach (var (g, times) in byGroup)
            {
                if (!MayDo(s, g))
                    flags.Add(g == "admin"
                        ? $"{Label(s)}: Admin at {DayHours.Spans(times)} - only managers may do Admin"
                        : $"{Label(s)}: on the tills at {DayHours.Spans(times)} - juniors may not work a till");
                List<(double Start, double End)>? wins = g switch
                {
                    "checkout" => [(s1.TradingOpen, s1.TradingClose)],
                    "load_running" => LoadRunning.LoadRunningPeriods(s1).Select(p => (p.Start, p.End)).ToList(),
                    "non_op" => null,
                    _ => s1.Windows.Where(w => w.Group == g).Select(w => (w.Start, w.End)).ToList(),
                };
                if (wins is null) continue;
                var outside = times.Where(t => !wins.Any(w => t >= w.Start && t < w.End)).ToList();
                if (outside.Count > 0)
                {
                    var winText = string.Join(", ", wins.Select(w => $"{Fmt(w.Start)}–{Fmt(w.End)}"));
                    flags.Add($"{Label(s)}: {GroupName(g)} at {DayHours.Spans(outside)} is outside its window ({(winText.Length > 0 ? winText : "none today")})");
                }
            }
        }

        // ── Shift lengths and junior hours (G1, G2; J1-J3).
        foreach (var s in shifts)
        {
            var len = s.End - s.Start;
            if (len < minShift) flags.Add($"{Label(s)} is under the {Js.NumberToString(minShift / 60)}-hour minimum");
            else if (DayHours.IsManagerShift(s) && len < managerMin)
                flags.Add($"{Label(s)} is under the {Js.NumberToString(managerMin / 60)}-hour minimum for a manager");
            double max = 0;
            if (opts.MaxClock is not null && opts.MaxClock.TryGetValue(s.PlanRole, out var m)) max = m;
            if (Js.Truthy(max) && len > max) flags.Add($"{Label(s)} is longer than the {Js.ToFixed(max / 60, 2)}-hour maximum");
            if (s.PlanRole == RoleBadges.Jsa)
            {
                if (s.Start < 6 * 60 || s.End > 21 * 60) flags.Add($"{Label(s)} works outside a junior's 06:00–21:00");
                if (opts.IsSchoolDay == true && s.Start < 16 * 60) flags.Add($"{Label(s)} starts before 16:00 on a school day");
            }
        }

        // ── Work: what Stage 1 planned against what the shifts carry.
        var demand = new JsMap<string, JsMap<double, double>>();
        foreach (var b in s1.Blocks)
        {
            if (b.Headcount <= 0) continue;
            var dm = demand.GetOr(b.Group, new JsMap<double, double>());
            dm.Set(b.Time, dm.GetOr(b.Time, 0) + b.Headcount);
            demand.Set(b.Group, dm);
        }
        var painted = new JsMap<string, JsMap<double, double>>();
        foreach (var tm in tiles)
        {
            foreach (var (t, g) in tm)
            {
                if (g == TaskGroups.Flex || g == TaskGroups.Break) continue;
                var p = painted.GetOr(g, new JsMap<double, double>());
                p.Set(t, p.GetOr(t, 0) + 1);
                painted.Set(g, p);
            }
        }
        // Why work is on nobody's shift, at a time it was wanted (review RC-29):
        // more wanted at once than can do it there, the people who can do it on
        // their breaks, on the tills, or busy with other work all through.
        string Cause(string g, double t, double want)
        {
            var able = Present(t).Where(x => MayDo(x.S, g)).ToList();
            if (able.Count < want) return "depth";
            if (able.Any(x => tiles[x.I].GetOr(t, "") == TaskGroups.Break)) return "break";
            if (g != "checkout" && able.Any(x => tiles[x.I].GetOr(t, "") == "checkout")) return "till";
            return "busy";
        }
        string Dominant(string g, IEnumerable<double> times)
        {
            var n = new JsMap<string, double>();
            var want = demand.GetOr(g, new JsMap<double, double>());
            foreach (var t in times)
            {
                var c = Cause(g, t, want.GetOr(t, 1));
                n.Set(c, n.GetOr(c, 0) + 1);
            }
            return Js.Sorted(n.Entries(), (a, b) => b.Value - a.Value)[0].Key;
        }

        // Tills: manned in their own quarter-hour or not at all.
        var tillDemand = demand.GetOr("checkout", new JsMap<double, double>());
        var tillTiles = painted.GetOr("checkout", new JsMap<double, double>());
        var shortTills = new List<double>();
        foreach (var (t, n) in tillDemand)
            for (var k = tillTiles.GetOr(t, 0); k < n; k++) shortTills.Add(t);
        lostAt.AddRange(shortTills);
        if (shortTills.Count > 0)
        {
            var c = Dominant("checkout", shortTills);
            var why = c == "depth"
                ? "fewer people who can work a till are on than the registers need"
                : c == "break"
                    ? "the crew's breaks left nobody to take the till"
                    : "the people who could take it were on other work";
            flags.Add($"Tills: {Hrs(shortTills.Count)}h of register time has nobody on it - {why} ({DayHours.Spans(shortTills)})");
        }

        // A trading quarter-hour with no till manned at all - a hard stop (owner
        // decision R3): the doors are open and nobody can serve.
        var noTill = new List<double>();
        for (var t = s1.TradingOpen; t < s1.TradingClose; t += 15)
            if (tillTiles.GetOr(t, 0) == 0) noTill.Add(t);
        if (noTill.Count > 0) flags.Add($"No till manned while the store is trading ({DayHours.Spans(noTill)})");

        // Everything else: done inside its window, or on nobody's shift. Named
        // Admin tasks are held to a window of their own; Reset may be made up
        // anywhere in the Reset window (owner decision 11: strict Admin, loose
        // Reset).
        foreach (var (g, want) in demand)
        {
            if (g == "checkout") continue;
            var have = painted.GetOr(g, new JsMap<double, double>());
            var tasks = g == "admin" ? s1.AdminTasks : null;
            List<double> lost;
            if (tasks is { Count: > 0 })
            {
                // Admin hours past the tasks (Stage 1's generic time) are tied to no
                // task: they may be done anywhere in the staffed day, and match last.
                var namedAt = new JsMap<double, double>();
                foreach (var u in s1.UnitWindows ?? [])
                    if (u.Group == "admin" && u.Named) namedAt.Set(u.Time, namedAt.GetOr(u.Time, 0) + 1);
                bool Generic(double t, int i) => s1.UnitWindows is not null && i >= namedAt.GetOr(t, 0);
                // Each named quarter-hour Stage 1 planned belongs to its own job and is
                // matched inside that job's window only. Matching it inside any window
                // that held its time let a long-window job (Safe Handover, 09:00-20:30)
                // carry Fresh Ordering's quarter-hours to the early afternoon, where they
                // count for nothing (G001 13/11: End of Day 0 of 23 minutes done).
                var ownAt = new JsMap<double, List<(double Start, double End)>>();
                foreach (var u in s1.UnitWindows ?? [])
                    if (u.Group == "admin" && u.Named) ownAt.Set(u.Time, [.. ownAt.GetOr(u.Time, []), (u.Start, u.End)]);
                foreach (var (k, ws) in ownAt.Entries()) ownAt.Set(k, Js.Sorted(ws, (a, b) => (a.End - b.End).Or(a.Start - b.Start)));
                lost = Unmatched(want, have, (t, i) =>
                {
                    if (Generic(t, i)) return (s1.StaffOpen, s1.StaffClose);
                    if (ownAt.TryGet(t, out var own) && i < own.Count) return own[i];
                    var holding = tasks.Where(x => x.WindowStart <= t && t < x.WindowEnd).ToList();
                    return holding.Count > 0
                        ? (Js.Min(holding.Select(x => x.WindowStart)), Js.Max(holding.Select(x => x.WindowEnd)))
                        : (t, t + B);
                });
            }
            else
            {
                static double Total(JsMap<double, double> m) => m.Values().Aggregate(0.0, (a, b) => a + b);
                var missing = Total(want) - Total(have);
                // Which quarter-hours went without: those painted short, latest first.
                var shortAt = new List<double>();
                foreach (var e in Js.Sorted(want.Entries(), (a, b) => b.Key - a.Key))
                    for (var k = have.GetOr(e.Key, 0); k < e.Value; k++) shortAt.Add(e.Key);
                lost = missing > 0 ? shortAt.Take((int)Math.Min(shortAt.Count, Math.Truncate(missing))).ToList() : [];
            }
            lostAt.AddRange(lost);
            if (lost.Count > 0)
                flags.Add($"{GroupName(g)}: {Hrs(lost.Count)}h of today's plan is on nobody's shift - {CauseText[Dominant(g, lost)]} ({DayHours.Spans(lost)})");
        }

        // Monday's date checks by their deadline (A12; decision 3): the named job's
        // minutes inside its own window, sharing the Admin tiles there with the
        // jobs whose windows close first.
        var llc = (s1.AdminTasks ?? []).FirstOrDefault(t => t.Name == "Longlife Date Checks" && t.Minutes > 0);
        if (llc is not null)
        {
            var jobs = new JsMap<string, Job>();
            foreach (var x in s1.AdminTasks ?? [])
            {
                if (x.Minutes <= 0) continue;
                var k = $"{Js.NumberToString(x.WindowStart)}-{Js.NumberToString(x.WindowEnd)}";
                var j = jobs.GetOr(k, new Job { Start = x.WindowStart, End = x.WindowEnd });
                j.Minutes += x.Minutes;
                j.Llc = j.Llc || ReferenceEquals(x, llc);
                j.Names.Add(x.Name);
                jobs.Set(k, j);
            }
            var adminAt = painted.GetOr("admin", new JsMap<double, double>());
            foreach (var t in Js.Sorted(adminAt.Keys(), (a, b) => a - b))
                for (var n = adminAt.Get(t); n > 0; n--)
                {
                    var j = Js.Sorted(jobs.Values().Where(o => o.Got * B < o.Minutes && t >= o.Start && t < o.End), (a, b) => a.End - b.End)
                        .FirstOrDefault();
                    if (j is null) break;
                    j.Got++;
                }
            // Tasks sharing the window are one job (as Stage 1 sizes them), so the
            // flag names them with it.
            var job = jobs.Values().First(j => j.Llc);
            var done = Math.Min(job.Minutes, job.Got * B);
            var others = job.Names.Where(n => n != llc.Name).ToList();
            if (job.Minutes - done >= B)
                flags.Add($"Monday date checks{(others.Count > 0 ? $" (with {string.Join(", ", others)})" : "")}: {Js.NumberToString(done)} of {Js.NumberToString(job.Minutes)} min done by {Fmt(job.End)}");
        }

        // The quoted hour inside its own window - the morning Cleaning hour
        // (decisions 10 and 12). A warning until the painter holds it (phase 6).
        foreach (var w in s1.Windows.Where(x => (x.Hours ?? 0) > 0))
        {
            var quota = Js.Round(w.Hours!.Value * 4);
            double inWin = 0;
            foreach (var (t, n) in painted.GetOr(w.Group, new JsMap<double, double>()))
                if (t >= w.Start && t < w.End) inWin += n;
            // Work short in its quoted hour is lost there, wherever the total-based
            // count above put it (the planner aims its added time by these times).
            for (var k = inWin; k < quota; k++) lostAt.Add(w.End - B);
            if (inWin < quota)
                flags.Add($"{GroupName(w.Group)} {Fmt(w.Start)}–{Fmt(w.End)}: {Hrs(inWin)}h of its {Hrs(quota)}h is worked in its hour");
        }

        // The manager handover (decisions 5, R7): at least an hour, with no upper
        // limit - a rule, now that trimming never cuts it (an error).
        var mgrs = Js.Sorted(shifts.Where(DayHours.IsManagerShift), (a, b) => (a.Start - b.Start).Or(a.End - b.End));
        if (mgrs.Count >= 2)
        {
            var opener = mgrs[0];
            var closer = Js.Sorted(mgrs, (a, b) => (b.End - a.End).Or(b.Start - a.Start))[0];
            if (!ReferenceEquals(opener, closer))
            {
                var from = Math.Max(opener.Start, closer.Start);
                var to = Math.Min(opener.End, closer.End);
                if (to - from <= 0) flags.Add("Managers do not overlap - no handover (the rule is at least an hour)");
                else if (to - from < 60)
                    flags.Add($"Manager handover is {Js.NumberToString(to - from)} minutes ({Fmt(from)}–{Fmt(to)}) - the rule is at least an hour");
            }
        }

        // Breaks done by 18:00 where the shift can manage it (decision 6; work
        // wins, decision 16). A warning until the break placement is rebuilt (7).
        if (s1.TradingClose < 21 * 60)
        {
            for (var i = 0; i < shifts.Count; i++)
            {
                var s = shifts[i];
                var brk = tiles[i].Where(e => e.Value == TaskGroups.Break).Select(e => e.Key).ToList();
                if (brk.Count == 0) continue;
                var last = Js.Max(brk) + B;
                var earliest = EarliestBreakEnd(s);
                if (last > LateBreakCutoff && earliest is not null && earliest <= LateBreakCutoff)
                    flags.Add($"{Label(s)}: break ends {Fmt(last)} - it could have been taken by 18:00");
            }
        }

        // Till demand the register count cut off (R2).
        if (s1.TillCapped is { Hours: > 0 } capped)
            flags.Add($"Till demand above the store's {Js.NumberToString(capped.Registers)} registers: {Js.ToFixed(capped.Hours, 2)}h of register time not planned ({Fmt(capped.Start)}–{Fmt(capped.End)})");

        // ── Shift time with no work, and why (G10, F6; decisions 3 and 15).
        var held = DayHours.HeldTimeOf(shifts, cover, minShift, tiles, (60, managerMin));
        for (var i = 0; i < held.Other.Count; i++)
        {
            var times = held.Other[i];
            if (times.Count > 0)
                flags.Add($"{Label(shifts[i])}: {times.Count} quarter-hour{(times.Count == 1 ? "" : "s")} with no work ({DayHours.Spans(times)})");
        }
        if (held.Cover.Count > 0)
            flags.Add($"{Hrs(held.Cover.Count)}h of shift time is held for cover - nobody alone, a manager on, three adults with a junior ({DayHours.Spans(held.Cover)})");
        // Of that, the time where the day has less work than the people the cover
        // rules need on (decision 3: "a day with less work in total than two people
        // need should not occur in real stores; if it does, it is flagged as an
        // hours issue") - the close-down after a 21:00 close, with only the closing
        // manager's End of Day to do and a second person who must be in.
        var workAt = new JsMap<double, double>();
        foreach (var b in s1.Blocks) workAt.Set(b.Time, workAt.GetOr(b.Time, 0) + b.Headcount);
        var thin = held.Cover.Where(t =>
        {
            var juniors = Present(t).Count(x => x.S.PlanRole == RoleBadges.Jsa);
            var need = Math.Max(cover.MinPeopleOnFloor, juniors > 0 ? cover.MinAdultsWithJsa + juniors : 0);
            return workAt.GetOr(t, 0) < need;
        }).ToList();
        if (thin.Count > 0)
            flags.Add($"Hours issue: {Hrs(thin.Count)}h of shift time has no work because there is less work than the people who must be on ({DayHours.Spans(thin)}) - a benchmark matter (owner decision 3), not a planning fault");
        if (held.Minimum.Count > 0)
            flags.Add($"{Hrs(held.Minimum.Count)}h of shift time is held by the 3-hour minimum ({DayHours.Spans(held.Minimum)})");
        if (held.Managers.Count > 0)
            flags.Add($"{Hrs(held.Managers.Count)}h of shift time is held by the managers' hour-long handover and six-hour shifts ({DayHours.Spans(held.Managers)})");
        if (held.Fixed.Count > 0)
            flags.Add($"{Hrs(held.Fixed.Count)}h of fixed-roster shift time has no work in it ({DayHours.Spans(held.Fixed)}) - review the fixed roster with the people on it");

        // ── Hours: worked hours against the benchmark file (R2).
        var hours = DayHours.HoursCheckOf(s1, shifts, new DayHours.HeldHours(
            held.Cover.Count / 4.0, held.Minimum.Count / 4.0, held.Managers.Count / 4.0, held.Fixed.Count / 4.0, opts.BreakTimeAdded ?? 0));
        if (!string.IsNullOrEmpty(hours.Message)) flags.Add(hours.Message);

        return new DayCheck(flags, hours, lostAt);
    }

    /// <summary>OK (decision 8): no error from any planning step, the hours check included.</summary>
    public static bool DayIsOk(IEnumerable<string> flags) => !flags.Any(f => ReviewChecks.SeverityOf(f) == Severity.Error);
}
