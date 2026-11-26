// Port of src/test/rosterInvariants.ts, with rosterScenario.ts's isSchoolDay and
// isTermWeek (read from the dumped ScenarioParams).
// ─── The owner's rules, checked on a finished day ───────────────────────────
// Not a test itself. CheckDay() reads one day as the store sees it - the Task
// view's shifts or the named roster's - and returns every place it breaks a
// rule. The rule references (R1-R20) are the requirement checklist of the
// 07/11/2026 review, which reads them from the owner's own instructions; the
// interview items come from plans/roster-generation-plan.md.
//
// Ported from the review's harness (reviewer A07). Changes:
//   - a break no longer counts as leaving a colleague alone (owner decision 1:
//     breaks are taken in the building), so that check is gone;
//   - breaks finishing by 18:00 and the manager handover overlap are
//     preferences (decision 2; the interview calls the overlap "soft-
//     preferred, not a hard minimum"), reported but not rules.

using System.Text.Json;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Tests.Services.Harness;

internal sealed record CheckShift(string Label, string Role, double Start, double End, IReadOnlyList<ITile> Blocks);

internal sealed record Violation(string Inv, string Detail);

internal sealed record CheckDayContext
{
    public string Date { get; init; } = "";
    /// <summary>"slots", "plan" or "roster".</summary>
    public string Surface { get; init; } = "plan";
    public Stage1Computation S1 { get; init; } = new();
    /// <summary>The store-facing benchmark (before borrowing and PH moves).</summary>
    public IReadOnlyDictionary<string, double> Bench { get; init; } = new Dictionary<string, double>();
    /// <summary>Null when the scenario books nothing for the date (no check either way).</summary>
    public double? NonOpMin { get; init; }
    public bool IsSchoolDay { get; init; }
    /// <summary>Plan only: the clock maximum of each slot type.</summary>
    public (double MGR, double SA, double JSA)? ShaperMax { get; init; }
    /// <summary>Roster only: paid maximum by role.</summary>
    public IReadOnlyDictionary<string, double> BreakRulesMaxPaid { get; init; } = new Dictionary<string, double>();
    /// <summary>The eve of a public holiday whose load was moved to the evening.</summary>
    public bool EveningLR { get; init; }
}

/// <summary>The dumped ScenarioParams (rosterScenario.ts), as the checks and the tests read them.</summary>
internal sealed class ScenarioParams(JsonElement p)
{
    public int? Seed => p.GetProperty("seed").GetInt32() is var s && s != 0 ? s : null;
    public DateOnly PeriodStart => DateOnly.Parse(p.GetProperty("periodStart").GetString()!);
    public double SaMax => p.GetProperty("saMax").GetDouble();
    public string School => p.GetProperty("school").GetString()!;
    public string? SchoolException => p.GetProperty("schoolException").ValueKind == JsonValueKind.String ? p.GetProperty("schoolException").GetString() : null;
    public (string Date, bool Realloc)? Ph => p.GetProperty("ph").ValueKind == JsonValueKind.Object
        ? (p.GetProperty("ph").GetProperty("date").GetString()!, p.GetProperty("ph").GetProperty("realloc").GetBoolean())
        : null;
    public IReadOnlyList<(string EmpName, int Dow, double Start, double End)> Fixed =>
        p.GetProperty("fixed").EnumerateArray()
            .Select(f => (f.GetProperty("empName").GetString()!, f.GetProperty("dow").GetInt32(), f.GetProperty("start").GetDouble(), f.GetProperty("end").GetDouble()))
            .ToList();

    public IReadOnlyDictionary<string, double> BenchOn(string date) =>
        p.GetProperty("bench").TryGetProperty(date, out var b)
            ? b.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble())
            : new Dictionary<string, double>();

    public double? NonOpMinOn(string date) =>
        p.GetProperty("nonOpMinByDate").TryGetProperty(date, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public bool IsSchoolDay(string date)
    {
        var dates = Period.PeriodDatesFrom(PeriodStart).Select(d => d.ToIso()).ToList();
        if (Period.DowMon0(DateOnlyExtensions.ParseIso(date)) >= 5 || School == "none" || date == SchoolException) return false;
        return School == "all" || string.CompareOrdinal(date, dates[6]) <= 0;
    }

    /// <summary>Whether week `w` (0 or 1) of the fortnight is a school-term week.</summary>
    public bool IsTermWeek(int w) => School == "all" || (School == "half" && w == 0);
}

internal static class RosterInvariants
{
    private const double B = Grid.BlockMin;

    private static string Fmt(double m) => Grid.MinToTime(m);

    private static string N(double x) => Js.NumberToString(x);

    /// <summary>What each check means, by key prefix (the longest matching prefix wins).</summary>
    private static readonly (string Prefix, string Kind, string Rule, string Means)[] Catalogue =
    [
        ("a_tills", "rule", "R9/R10", "the exact number of people on the tills in every quarter-hour"),
        ("a_no_till_while_trading", "rule", "T9, decision 18", "someone on a till in every quarter-hour the doors are open"),
        ("b_blank_", "rule", "R13", "no blank tile anywhere in a shift - start, middle or end"),
        ("b_times_disagree_with_tiles", "rule", "R13", "a shift's times match its tiles (no paid quarter-hour without one)"),
        ("b_block_outside_shift", "rule", "R13", "no tile outside its shift's own times"),
        ("c_painted_above_stage1_", "rule", "R2", "no group painted beyond what Stage 1 allocated it"),
        ("c_painted_below_stage1_", "rule", "R2/U2", "every hour Stage 1 planned is painted - work a break displaces is made up, not lost"),
        ("c_stage1_above_benchmark_", "rule", "R2", "no group allocated more than its benchmark"),
        ("d_admin_outside_window", "rule", "R3", "admin only inside an admin window"),
        ("d_admin_by_non_manager", "rule", "R3", "admin only by managers"),
        ("d_monday_datechecks_short", "rule", "R3", "Monday's 2 hours of date checks done before 08:30"),
        ("d_admin_task_short", "rule", "A3/A6, decisions 1 and 11", "every named Admin task is worked inside its own window"),
        ("e_lr_before_window", "rule", "R4", "load running never starts before its window"),
        ("e_lr_after_window", "rule", "R4", "load running never bleeds past its finish"),
        ("e_ph_evening_lr_not_in_evening", "rule", "R4", "public-holiday load moved to the eve is worked in the evening"),
        ("e_quoted_window_lost", "rule", "R6", "quoted window hours (e.g. 1h cleaning 09:00-10:00) are worked there"),
        ("e_quoted_window_unfunded", "rule", "W3/C1, decision 10", "the quoted window's hours are planned in full (the borrow may not take them)"),
        ("e_quoted_window_overfilled", "rule", "C1, decision 12", "no more than the quoted hours inside a quoted window"),
        ("f_nonop_", "rule", "R7", "non-op tiles only when non-op hours are booked, never more"),
        ("g_below_3h", "rule", "Break & Work rules", "no shift under 3 hours"),
        ("g_above_max", "rule", "Break & Work rules", "no shift over its role's maximum"),
        ("h_break_illegal", "rule", "Break rules", "breaks are legal: length, and taken in time"),
        ("h_break_after_1800", "preference", "Owner decision 2", "breaks done by 18:00 where the shift allows it"),
        ("i_lone_worker", "rule", "Interview", "at least two people on whenever anyone is (lone-worker safety)"),
        ("i_no_manager_present", "rule", "Interview", "a manager on in every quarter-hour anyone is"),
        ("j_jsa_fewer_than_3_adults", "rule", "Interview", "a junior always has at least three adults with them"),
        ("j_jsa_ineligible_task", "rule", "Interview", "juniors never on the tills or admin"),
        ("j_", "rule", "Junior law", "junior hours: 06:00-21:00, not before 16:00 on a school day, day and week caps"),
        ("k_unfilled_shift", "info", "", "a slot nobody could legally take (an open shift)"),
        ("l_fixed_", "rule", "Fixed rosters", "a fixed roster is worked exactly as set"),
        ("l_mgr_handover_destroyed", "rule", "G4, decision 5", "the opening and closing managers overlap at least an hour"),
        ("q_hours_issue_", "info", "Decisions 1, R2", "hours planned over a benchmark on purpose - an hours issue, shown as a warning"),
        ("p_hours_", "rule", "H4, decisions R2 and 1", "worked hours within 1 hour of the benchmark file, past the overages the decisions allow"),
        ("s_stage1_error", "rule", "V2, decision 8", "no error from the first planning step (Stage 1)"),
        ("x_plan_vs_roster_hours_differ", "rule", "R13/R19", "the Task view and the roster agree on shift hours"),
    ];

    /// <summary>Quarter-hours Stage 1 planned (tills aside) that no shift's tiles carry.</summary>
    public static double LostQuarterHours(Stage1Computation s1, IReadOnlyList<CheckShift> shifts)
    {
        var tiles = new JsMap<string, double>();
        foreach (var s in shifts) foreach (var b in s.Blocks) tiles.Set(b.Group, tiles.GetOr(b.Group, 0) + 1);
        double n = 0;
        foreach (var a in s1.Allocations.Where(a => a.Group != TaskGroups.Checkout))
            n += Math.Max(0, Js.Round(a.AllocatedHours * 4) - tiles.GetOr(a.Group, 0));
        return n;
    }

    public static (string Kind, string Rule, string Means) DescribeInvariant(string key)
    {
        var hit = Js.Sorted(Catalogue.Where(c => key.StartsWith(c.Prefix, StringComparison.Ordinal)), (a, b) => b.Prefix.Length - a.Prefix.Length);
        if (hit.Count == 0) return ("rule", "", key);
        return (hit[0].Kind, hit[0].Rule, hit[0].Means);
    }

    public static List<Violation> CheckDay(IReadOnlyList<CheckShift> shifts, CheckDayContext ctx)
    {
        var v = new List<Violation>();
        void Add(string inv, string detail) => v.Add(new Violation(inv, detail));
        var s1 = ctx.S1;
        string? Group(CheckShift s, double m) => s.Blocks.FirstOrDefault(b => b.Time == m)?.Group;

        // (a) tills exact
        var tills = new JsMap<double, double>();
        foreach (var b in s1.Blocks) if (b.Group == "checkout") tills.Set(b.Time, tills.GetOr(b.Time, 0) + b.Headcount);
        var painted = new JsMap<double, double>();
        foreach (var s in shifts)
            foreach (var b in s.Blocks) if (b.Group == "checkout") painted.Set(b.Time, painted.GetOr(b.Time, 0) + 1);
        foreach (var t in new JsSet<double>(tills.Keys().Concat(painted.Keys())))
        {
            var want = tills.GetOr(t, 0);
            var got = painted.GetOr(t, 0);
            if (want != got) Add("a_tills", $"{Fmt(t)} {N(got)} on tills vs {N(want)} needed");
        }
        // ...and never nobody while the doors are open, whatever the curve asks for
        // (decision 18 - today the curve's lowest demand happens to keep one on).
        for (var m = s1.TradingOpen; m < s1.TradingClose; m += 15)
            if (!Js.Truthy(painted.GetOr(m, 0))) Add("a_no_till_while_trading", $"{Fmt(m)} nobody on a till, doors open");

        // (b) no blank quarter-hours anywhere in a shift: flex, or no tile at all
        foreach (var s in shifts)
        {
            static bool Work(string? g) => g is not null && g != "flex" && g != "break";
            var seq = new List<string?>();
            for (var m = s.Start; m < s.End; m += 15) seq.Add(Group(s, m));
            for (var i = 0; i < seq.Count; i++)
            {
                var g = seq[i];
                if (g is not null && g != "flex") continue;
                var before = seq.Take(i).Any(Work);
                var after = seq.Skip(i + 1).Any(Work);
                var pos = before && after ? "interior" : !before ? "leading" : "trailing";
                Add(g is null ? $"b_blank_missing_{pos}" : $"b_blank_flex_{pos}", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)} @{Fmt(s.Start + i * 15)}");
            }
            if (s.Blocks.Count > 0)
            {
                var bs = Js.Min(s.Blocks.Select(b => b.Time));
                var be = Js.Max(s.Blocks.Select(b => b.Time)) + 15;
                if (bs != s.Start || be != s.End)
                    Add("b_times_disagree_with_tiles", $"{s.Label} shows {Fmt(s.Start)}-{Fmt(s.End)}, tiles {Fmt(bs)}-{Fmt(be)}");
            }
            foreach (var b in s.Blocks)
                if (b.Time < s.Start || b.Time >= s.End)
                    Add("b_block_outside_shift", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)} block @{Fmt(b.Time)}");
        }

        // (c) each group: painted <= Stage 1 <= benchmark
        var tileBlocks = new JsMap<string, double>();
        foreach (var s in shifts) foreach (var b in s.Blocks) tileBlocks.Set(b.Group, tileBlocks.GetOr(b.Group, 0) + 1);
        foreach (var a in s1.Allocations)
        {
            if (a.Group == "checkout") continue;
            var allocB = Js.Round(a.AllocatedHours * 4);
            var tiles = tileBlocks.GetOr(a.Group, 0);
            if (tiles > allocB) Add($"c_painted_above_stage1_{a.Group}", $"{a.Group} {N(tiles / 4)}h painted vs {N(allocB / 4)}h Stage 1");
            // ...and nothing it planned goes missing: work a break displaces is made
            // up somewhere legal, or it is lost - the load not run, the date checks
            // not done (Roster Engine Review 07/11/2026, P6, P7).
            if (tiles < allocB) Add($"c_painted_below_stage1_{a.Group}", $"{a.Group} {N(tiles / 4)}h painted vs {N(allocB / 4)}h Stage 1");
            if (a.Group != "non_op")
            {
                var raw = ctx.Bench.GetValueOrDefault(a.Group);
                // Half-a-block rounding tolerance. A public holiday's moved load is in
                // the eve's allocation by design.
                var extra = a.Group == "load_running" && ctx.EveningLR ? double.PositiveInfinity : 0;
                // An hours issue is planned over the benchmark on purpose (owner
                // decisions 1 and R2: every Admin task, whatever the benchmark holds).
                double allowed = 0;
                foreach (var h in s1.HoursIssues ?? []) if (h.Group == a.Group) allowed += h.Hours;
                if (allowed >= 0.25 - 1e-9) Add($"q_hours_issue_{a.Group}", $"{a.Group} {Js.ToFixed(allowed, 2)}h over the benchmark, as an hours issue");
                if (a.AllocatedHours > raw + 0.125 + 1e-9 + extra + allowed)
                    Add($"c_stage1_above_benchmark_{a.Group}{(raw == 0 ? "_zeroed" : "")}", $"{a.Group} Stage 1 {Js.ToFixed(a.AllocatedHours, 2)}h vs benchmark {Js.ToFixed(raw, 2)}h");
            }
        }

        // (d) admin: managers only, inside an admin window; Monday's date checks
        var adminWin = s1.Windows.Where(w => w.Group == "admin").ToList();
        foreach (var s in shifts)
        {
            foreach (var b in s.Blocks)
            {
                if (b.Group != "admin") continue;
                if (!adminWin.Any(w => b.Time >= w.Start && b.Time < w.End)) Add("d_admin_outside_window", $"{s.Label} @{Fmt(b.Time)}");
                if (!Roles.IsManager(s.Role)) Add("d_admin_by_non_manager", $"{s.Label} ({s.Role}) @{Fmt(b.Time)}");
            }
        }
        // Every named task inside its OWN window (decisions 1 and 11): can the
        // admin tiles be shared out so each job gets its minutes inside its window?
        // Jobs with the same window are one job, as Stage 1 sizes them. Each time,
        // in order, gives its tiles to the open job whose window closes first -
        // optimal when every job's window is one stretch. A job is owed the
        // quarter-hours that bring it within a quarter-hour of its minutes, no more
        // (Stage 1 rounds with a carry from one job to the next), so the check
        // fails exactly when some task cannot be worked inside its window.
        {
            var jobs = new JsMap<string, AdminJob>();
            foreach (var x in s1.AdminTasks ?? [])
            {
                if (x.Minutes <= 0) continue;
                var k = $"{N(x.WindowStart)}-{N(x.WindowEnd)}";
                var j = jobs.Get(k) ?? new AdminJob(x.WindowStart, x.WindowEnd);
                j.Minutes += x.Minutes;
                j.Names.Add(x.Name);
                jobs.Set(k, j);
            }
            var adminAt = new JsMap<double, double>();
            foreach (var s in shifts)
                foreach (var b in s.Blocks) if (b.Group == "admin") adminAt.Set(b.Time, adminAt.GetOr(b.Time, 0) + 1);
            // Each job is owed what brings it within a quarter-hour of its minutes.
            static double Owed(AdminJob j) => j.Minutes < 15 ? 0 : Math.Floor((j.Minutes - 15) / 15 + 1e-9) + 1;
            foreach (var t in Js.Sorted(adminAt.Keys(), (a, b) => a - b))
            {
                for (var n = adminAt[t]; n > 0; n--)
                {
                    var candidates = Js.Sorted(jobs.Values().Where(o => o.Got < Owed(o) && t >= o.Start && t < o.End), (a, b) => a.End - b.End);
                    if (candidates.Count == 0) break;
                    candidates[0].Got++;
                }
            }
            var @short = jobs.Values().Where(j => j.Minutes - Math.Min(j.Minutes, j.Got * 15) >= 15).ToList();
            if (@short.Count > 0)
                Add(
                    "d_admin_task_short",
                    string.Join("; ", @short.Select(j => $"{string.Join(" + ", j.Names)} {Fmt(j.Start)}-{Fmt(j.End)}: {N(Math.Min(j.Minutes, j.Got * 15))} of {N(j.Minutes)} min")));
        }
        var llc = (s1.AdminTasks ?? []).FirstOrDefault(t => t.Name == "Longlife Date Checks");
        if (llc is not null)
        {
            double n = 0;
            foreach (var s in shifts)
                foreach (var b in s.Blocks) if (b.Group == "admin" && b.Time >= llc.WindowStart && b.Time < llc.WindowEnd) n++;
            var need = Js.Round(llc.Minutes / 15);
            if (n < need) Add("d_monday_datechecks_short", $"{N(n / 4)}h admin before {Fmt(llc.WindowEnd)} vs {N(need / 4)}h of date checks");
        }

        // (e) load running inside its window (or the public-holiday eve's evening)
        var lrWin = s1.Windows.Where(w => w.Group == "load_running").ToList();
        var lrStart = lrWin.Count > 0 ? Js.Min(lrWin.Select(w => w.Start)) : 0;
        foreach (var s in shifts)
            foreach (var b in s.Blocks)
            {
                if (b.Group != "load_running") continue;
                var inWin = lrWin.Any(w => b.Time >= w.Start && b.Time < w.End);
                var inEvening = ctx.EveningLR && b.Time >= 18 * 60;
                if (!inWin && !inEvening)
                    Add(
                        b.Time < lrStart ? "e_lr_before_window" : "e_lr_after_window",
                        $"{s.Label} @{Fmt(b.Time)} (window {string.Join(",", lrWin.Select(w => $"{Fmt(w.Start)}-{Fmt(w.End)}"))})");
            }
        if (ctx.EveningLR)
        {
            var s1Evening = s1.Blocks.Where(b => b.Group == "load_running" && b.Time >= 18 * 60).Aggregate(0.0, (n, b) => n + b.Headcount);
            double eve = 0;
            foreach (var s in shifts) foreach (var b in s.Blocks) if (b.Group == "load_running" && b.Time >= 18 * 60) eve++;
            if (eve < s1Evening)
                Add("e_ph_evening_lr_not_in_evening", $"{N(eve / 4)}h of LR after 18:00 painted vs {N(s1Evening / 4)}h Stage 1 placed there");
        }
        foreach (var w in s1.Windows.Where(x => x.Hours is > 0))
        {
            var s1In = s1.Blocks.Where(b => b.Group == w.Group && b.Time >= w.Start && b.Time < w.End).Aggregate(0.0, (n, b) => n + b.Headcount);
            double paintedIn = 0;
            foreach (var s in shifts)
                foreach (var b in s.Blocks) if (b.Group == w.Group && b.Time >= w.Start && b.Time < w.End) paintedIn++;
            if (paintedIn < s1In)
                Add("e_quoted_window_lost", $"{w.Group} {Fmt(w.Start)}-{Fmt(w.End)} quota {N(w.Hours!.Value)}h: Stage 1 {N(s1In / 4)}h, painted {N(paintedIn / 4)}h");
            // The quoted hour is a commitment (decision 10: the borrow may not take
            // it) and an exact one (decision 12: levelling may not add to it).
            var quota = Js.Round(w.Hours!.Value * 4);
            if (s1In < quota)
                Add("e_quoted_window_unfunded", $"{w.Group} {Fmt(w.Start)}-{Fmt(w.End)} quota {N(w.Hours.Value)}h: Stage 1 placed {N(s1In / 4)}h");
            if (ctx.Surface != "slots" && paintedIn > quota)
                Add("e_quoted_window_overfilled", $"{w.Group} {Fmt(w.Start)}-{Fmt(w.End)} quota {N(w.Hours.Value)}h: painted {N(paintedIn / 4)}h");
        }

        // (f) non-op only when booked, never more than booked
        var nonOpTiles = tileBlocks.GetOr("non_op", 0);
        if (ctx.NonOpMin == 0 && nonOpTiles > 0) Add("f_nonop_without_hours", $"{N(nonOpTiles / 4)}h of non-op tiles, 0 booked");
        if (ctx.NonOpMin > 0 && nonOpTiles > Js.Round(ctx.NonOpMin.Value / 15))
            Add("f_nonop_above_booked", $"{N(nonOpTiles / 4)}h vs {N(ctx.NonOpMin.Value / 60)}h");

        // (g) shift length
        foreach (var s in shifts)
        {
            var len = s.End - s.Start;
            if (len < 180) Add("g_below_3h", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)}");
            if (ctx.Surface != "roster" && ctx.ShaperMax is { } max)
            {
                var (kind, cap) = s.Label.StartsWith("MGR", StringComparison.Ordinal) ? ("MGR", max.MGR)
                    : s.Label.StartsWith("JSA", StringComparison.Ordinal) ? ("JSA", max.JSA) : ("SA", max.SA);
                if (len > cap) Add("g_above_max", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)} > {N(cap / 60)}h clock");
            }
            else if (ctx.Surface == "roster")
            {
                var unpaid = Breaks.BreakForRole(s.Role, len / 60).UnpaidMinutes;
                var maxPaid = ctx.BreakRulesMaxPaid[s.Role];
                if ((len - unpaid) / 60 > maxPaid + 1e-9)
                    Add("g_above_max", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)} paid {Js.ToFixed((len - unpaid) / 60, 2)}h > {N(maxPaid)}h");
            }
        }

        // (h) breaks: legal; done by 18:00 where the shift allows (a preference)
        if (ctx.Surface != "slots")
        {
            foreach (var s in shifts)
            {
                var len = s.End - s.Start;
                var times = s.Blocks.Where(b => b.Group == "break").Select(b => b.Time).ToList();
                var ent = Breaks.BreakForRole(s.Role, len / 60).TotalMinutes;
                var err = Breaks.BreakTileError(times, ent, s.Start, s.End);
                if (err is not null) Add("h_break_illegal", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)}: {err}");
                if (s1.TradingClose < 21 * 60 && times.Count > 0)
                {
                    var last = Js.Max(times) + 15;
                    if (last > 18 * 60)
                    {
                        var seg = ent >= 60 ? 30 : ent;
                        var feasible = s.Start + 60 + seg <= 18 * 60;
                        Add(feasible ? "h_break_after_1800_avoidable" : "h_break_after_1800_forced", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)} break ends {Fmt(last)}");
                    }
                }
            }
        }

        // (i) cover, by who is rostered (a person on a break is still present)
        if (shifts.Count > 0)
        {
            var lo = Js.Min(shifts.Select(s => s.Start));
            var hi = Js.Max(shifts.Select(s => s.End));
            for (var m = lo; m < hi; m += 15)
            {
                var present = shifts.Where(s => s.Start <= m && m < s.End).ToList();
                if (present.Count == 1) Add("i_lone_worker", $"{Fmt(m)} only {present[0].Label}");
                if (present.Count >= 1 && !present.Any(s => Roles.IsManager(s.Role)))
                    Add("i_no_manager_present", $"{Fmt(m)} {string.Join(",", present.Select(s => s.Label))}");
            }
        }

        // (j) juniors (the weekly cap is checked across the fortnight)
        foreach (var s in shifts)
        {
            if (s.Role != "JSA") continue;
            if (s.Start < 6 * 60) Add("j_jsa_before_0600", $"{s.Label} {Fmt(s.Start)}");
            if (s.End > 21 * 60) Add("j_jsa_after_2100", $"{s.Label} {Fmt(s.End)}");
            if (ctx.IsSchoolDay && s.Start < 16 * 60) Add("j_jsa_school_day_before_1600", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)}");
            if (s.End - s.Start > (ctx.IsSchoolDay ? 5 : 6) * 60) Add("j_jsa_too_long", $"{s.Label} {Fmt(s.Start)}-{Fmt(s.End)}");
            for (var m = s.Start; m < s.End; m += 15)
            {
                var adults = shifts.Count(o => !ReferenceEquals(o, s) && o.Role != "JSA" && o.Start <= m && m < o.End);
                if (adults < 3)
                {
                    Add("j_jsa_fewer_than_3_adults", $"{s.Label} @{Fmt(m)} {adults} adults");
                    break;
                }
            }
            foreach (var b in s.Blocks)
                if (b.Group == "checkout" || b.Group == "admin") Add("j_jsa_ineligible_task", $"{s.Label} {b.Group} @{Fmt(b.Time)}");
        }

        // (p) the day's hours exactly as the app checks them (decision R2, build
        // phase 4): worked hours against the benchmark file, past the overages the
        // decisions allow. An allowed overage is an hours issue (info); any other
        // gap over an hour, either way, a rule broken.
        if (ctx.Surface != "slots" && shifts.Count > 0)
        {
            var checkedShifts = shifts.Select(s => new CheckedShift
            {
                PlanRole = Roles.IsManager(s.Role) ? "MGR" : s.Role == "JSA" ? "JSA" : "SA",
                Start = s.Start,
                End = s.End,
                Blocks = s.Blocks,
            }).ToList();
            var held = DayHours.HeldTimeOf(checkedShifts);
            var h = DayHours.HoursCheckOf(s1, checkedShifts, new DayHours.HeldHours(held.Cover.Count / 4.0, held.Minimum.Count / 4.0, held.Managers.Count / 4.0, held.Fixed.Count / 4.0));
            if (h.Status == "error") Add(h.Gap > 0 ? "p_hours_above_benchmark" : "p_hours_below_benchmark", h.Message!);
            else if (h.Status == "warning") Add("q_hours_issue_day", h.Message!);
        }

        // (s) the first planning step's own errors, graded as the Review grades them
        // (decision 8: a day with any error is not OK).
        var s1Errors = s1.Flags.Where(f => ReviewChecks.SeverityOf(f) == Severity.Error).ToList();
        if (s1Errors.Count > 0) Add("s_stage1_error", string.Join(" | ", s1Errors));
        return v;
    }

    private sealed class AdminJob(double start, double end)
    {
        public double Start { get; } = start;
        public double End { get; } = end;
        public double Minutes { get; set; }
        public List<string> Names { get; } = new();
        public double Got { get; set; }
    }

    /// <summary>
    /// The shaper's clock maximum per slot type, as production configures it: a
    /// manager slot takes the smallest SM/ASM/DSM maximum on a flat 30-minute break.
    /// </summary>
    public static (double MGR, double SA, double JSA) PlanShaperMax(double saMax, bool isSchoolDay) => (
        Breaks.MaxClockMinutes("flat_30_unpaid", Math.Min(12, Math.Min(12, 9))),
        Breaks.MaxClockMinutes("tiered", saMax),
        Math.Min((isSchoolDay ? 5 : 6) * 60, Breaks.MaxClockMinutes("tiered", 6)));
}
