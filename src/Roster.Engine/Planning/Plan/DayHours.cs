// Port of src/lib/plan/dayHours.ts.
// ─── A planned day's hours, and the shift time it holds with no work ─────────
// The light half of the day checker (DayCheck.cs), kept apart so the Plan page
// can work the hours out live while a day is being edited. Pure.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Planning.Plan;

/// <summary>A tile as the checker reads it.</summary>
public sealed record CheckedBlock(double Time, string Group) : ITile;

/// <summary>A shift as the checker reads it: a plan shift, or one being edited.</summary>
public sealed record CheckedShift
{
    /// <summary>"MGR", "SA" or "JSA".</summary>
    public string PlanRole { get; init; } = RoleBadges.Sa;
    /// <summary>The plan slot; "fixed-…" is a fixed-roster shift (owner decision 13).</summary>
    public string? SlotId { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public IReadOnlyList<ITile> Blocks { get; init; } = [];
}

/// <summary>Overages the owner's decisions allow (R2), hours.</summary>
public sealed record AllowedOverages(double Admin, double Reset, double Checkout, double Cover, double Minimum, double Managers, double Fixed, double Breaks);

public sealed record HoursCheck
{
    /// <summary>Shift time less every break, hours.</summary>
    public double Worked { get; init; }
    /// <summary>The benchmark file, booked non-op hours and any public-holiday move.</summary>
    public double Benchmark { get; init; }
    /// <summary>Worked - Benchmark.</summary>
    public double Gap { get; init; }
    public AllowedOverages Allowed { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    /// <summary>"ok", "warning" or "error".</summary>
    public string Status { get; init; } = "ok";
    public string? Message { get; init; }
}

public sealed class HeldTime
{
    /// <summary>Quarter-hours with no work held for cover.</summary>
    public List<double> Cover { get; } = new();
    /// <summary>Quarter-hours with no work held by the shortest legal shift.</summary>
    public List<double> Minimum { get; } = new();
    /// <summary>
    /// Quarter-hours with no work a manager is held to by the pair's rules: the
    /// hour-long handover (decision 5) and the six-hour manager shift (R7).
    /// </summary>
    public List<double> Managers { get; } = new();
    /// <summary>
    /// Quarter-hours with no work on fixed-roster shifts: hours the store's agreed
    /// fixed roster holds beyond the work (owner decision 13 keeps them).
    /// </summary>
    public List<double> Fixed { get; } = new();
    /// <summary>Per shift (by index), quarter-hours with no work and no rule holding them.</summary>
    public List<List<double>> Other { get; init; } = new();
}

/// <summary>The floor-cover rules the checks read (the Rostering rules page).</summary>
public sealed record CoverRules(double MinPeopleOnFloor, double MinAdultsWithJsa);

public static class DayHours
{
    public static readonly IReadOnlyDictionary<string, string> EngineRole = new Dictionary<string, string>
    {
        [RoleBadges.Mgr] = Roles.ASM,
        [RoleBadges.Sa] = Roles.SA,
        [RoleBadges.Jsa] = Roles.JSA,
    };

    private static readonly IReadOnlyDictionary<string, string> GroupName = new Dictionary<string, string>
    {
        ["checkout"] = "tills",
        ["load_running"] = "load running",
        ["reset"] = "reset",
        ["admin"] = "Admin",
        ["specials"] = "specials",
        ["cleaning"] = "cleaning",
    };

    public static readonly CoverRules DefaultCover = new(2, 3);
    private const double ToleranceH = 1;
    private const double B = Grid.BlockMin;

    public static bool IsManagerShift(CheckedShift s) => Roles.IsManager(EngineRole[s.PlanRole]);

    /// <summary>The till tiles split for display (Registers / SCO) are one group here.</summary>
    public static string NormGroup(string g) => g is "registers" or "scos" ? "checkout" : g;

    /// <summary>"07:00–07:30, 09:15–09:30" from quarter-hour start times.</summary>
    public static string Spans(IEnumerable<double> times)
    {
        var sorted = Js.Sorted(new JsSet<double>(times), (a, b) => a - b);
        var runs = new List<(double A, double B)>();
        foreach (var t in sorted)
        {
            if (runs.Count > 0 && runs[^1].B == t) runs[^1] = (runs[^1].A, t + B);
            else runs.Add((t, t + B));
        }
        return string.Join(", ", runs.Select(r => $"{Grid.MinToTime(r.A)}–{Grid.MinToTime(r.B)}"));
    }

    /// <summary>Each shift's tile at each of its quarter-hours ("flex" where none).</summary>
    public static List<JsMap<double, string>> TileMaps(IReadOnlyList<CheckedShift> shifts) =>
        shifts.Select(s =>
        {
            var m = new JsMap<double, string>();
            for (var t = s.Start; t < s.End; t += B) m.Set(t, TaskGroups.Flex);
            foreach (var b in s.Blocks)
                if (b.Time >= s.Start && b.Time < s.End) m.Set(b.Time, NormGroup(b.Group));
            return m;
        }).ToList();

    /// <summary>
    /// Shift time with no work, and why (G10, F6; owner decisions 3 and 15).
    /// Cover first: a person who must be there for nobody to be alone, for a
    /// manager to be on, or for a junior's three adults. Then the 3-hour minimum:
    /// edge time the shift can't shed without falling under it. The rest is time
    /// the plan carries for no rule.
    /// </summary>
    public static HeldTime HeldTimeOf(
        IReadOnlyList<CheckedShift> shifts,
        CoverRules? cover = null,
        double minShiftMin = 180,
        List<JsMap<double, string>>? tiles = null,
        (double HandoverMin, double ManagerMinShiftMin)? pair = null)
    {
        cover ??= DefaultCover;
        tiles ??= TileMaps(shifts);
        var p = pair ?? (60, 360);
        var out_ = new HeldTime { Other = shifts.Select(_ => new List<double>()).ToList() };
        List<CheckedShift> OnAt(double t) => shifts.Where(o => o.Start <= t && t < o.End).ToList();
        // The handover's first hour: the overlap of the opener and the closer.
        var mgrs = shifts.Where(IsManagerShift).ToList();
        var opener = Js.Sorted(mgrs, (a, b) => (a.Start - b.Start).Or(a.End - b.End)).FirstOrDefault();
        var closer = Js.Sorted(mgrs, (a, b) => (b.End - a.End).Or(b.Start - a.Start)).FirstOrDefault();
        var hasPair = opener is not null && closer is not null && !ReferenceEquals(opener, closer);
        var hoFrom = hasPair ? Math.Max(opener!.Start, closer!.Start) : 0;
        var hoTo = hasPair ? Math.Min(Math.Min(opener!.End, closer!.End), hoFrom + p.HandoverMin) : 0;
        for (var i = 0; i < shifts.Count; i++)
        {
            var s = shifts[i];
            var seq = Js.Sorted(tiles[i].Entries(), (a, b) => a.Key - b.Key);
            var blank = seq.Where(kv => kv.Value == TaskGroups.Flex).Select(kv => kv.Key).ToList();
            if (blank.Count == 0) continue;
            bool Needed(double t)
            {
                var on = OnAt(t);
                if (on.Count <= cover.MinPeopleOnFloor) return true;
                if (IsManagerShift(s) && on.Count(IsManagerShift) <= 1) return true;
                var juniors = on.Count(o => o.PlanRole == RoleBadges.Jsa);
                return s.PlanRole != RoleBadges.Jsa && juniors > 0 && on.Count - juniors <= cover.MinAdultsWithJsa;
            }
            var lead = new List<double>();
            foreach (var (t, g) in seq)
            {
                if (g != TaskGroups.Flex) break;
                lead.Add(t);
            }
            var trail = new List<double>();
            for (var k = seq.Count - 1; k >= 0; k--)
            {
                if (seq[k].Value != TaskGroups.Flex) break;
                trail.Add(seq[k].Key);
            }
            var edge = new JsSet<double>(lead.Concat(trail));
            // How much blank time the shortest legal shift holds on to - six hours
            // for a manager (owner decision R7). Wherever it sits: a junior on the
            // 3-hour minimum for the evening's last hour or two carries the rest of
            // it as hours either way, at the edges or not (G015 22/11: two juniors'
            // quarter-hours with no work in the middle read as unexplained hours).
            var mgr = IsManagerShift(s);
            var min = mgr ? Math.Max(minShiftMin, p.ManagerMinShiftMin) : minShiftMin;
            var hold = Math.Max(0, Math.Ceiling((min - (s.End - s.Start - blank.Count * B)) / B));
            var isFixed = s.SlotId?.StartsWith("fixed-", StringComparison.Ordinal) == true;
            var rest = new List<double>();
            foreach (var t in blank)
            {
                if (Needed(t)) out_.Cover.Add(t);
                else if (isFixed) out_.Fixed.Add(t);
                else if (mgr && t >= hoFrom && t < hoTo) out_.Managers.Add(t);
                else rest.Add(t);
            }
            // The edges first: that is the time a shorter shift would shed.
            foreach (var t in rest.Where(x => edge.Has(x)).Concat(rest.Where(x => !edge.Has(x))))
            {
                if (hold > 0)
                {
                    (mgr ? out_.Managers : out_.Minimum).Add(t);
                    hold--;
                }
                else out_.Other[i].Add(t);
            }
            Js.Sort(out_.Other[i], (a, b) => a - b);
        }
        return out_;
    }

    /// <summary>The held-time counts the hours check allows for, hours.</summary>
    public sealed record HeldHours(double Cover, double Minimum, double? Managers = null, double? Fixed = null, double? Breaks = null);

    /// <summary>
    /// The day's hours against its benchmark - one calculation for the status, the
    /// Plan page and the Review (owner decision R2). Worked hours: shift time less
    /// every break (the paid part of a break is not in the benchmark). Overages the
    /// decisions allow - Admin past its benchmark (1), checkout keeping its hours
    /// (10), shift time held for cover or by the 3-hour minimum (3, 15) - are a
    /// warning naming each; any other gap over an hour, either way, is an error.
    /// </summary>
    public static HoursCheck HoursCheckOf(Stage1Computation s1, IReadOnlyList<CheckedShift> shifts, HeldHours held)
    {
        double worked = 0;
        foreach (var s in shifts)
        {
            var len = s.End - s.Start;
            worked += (len - Breaks.BreakForRole(EngineRole[s.PlanRole], len / 60).TotalMinutes) / 60;
        }
        var benchmark = BenchmarkSplit.DayBenchmark(BenchmarkParts.Of(s1));
        var gap = worked - benchmark;
        var issues = s1.HoursIssues ?? [];
        var allowed = new AllowedOverages(
            issues.Where(h => h.Group != "reset").Aggregate(0.0, (n, h) => n + h.Hours),
            issues.Where(h => h.Group == "reset").Aggregate(0.0, (n, h) => n + h.Hours),
            s1.CheckoutShortfall ?? 0,
            held.Cover,
            held.Minimum,
            held.Managers ?? 0,
            held.Fixed ?? 0,
            // Shift time the planner added so breaks don't cost work (master plan
            // decision 4.2), read from its note on the day.
            held.Breaks ?? 0);
        var of = $"({Js.ToFixed(worked, 2)} h planned, {Js.ToFixed(benchmark, 2)} h benchmark)";
        var status = "ok";
        string? message = null;
        if (gap < -ToleranceH - 1e-9)
        {
            status = "error";
            // Where the hours went: the groups Stage 1 planned below the file - tills
            // the registers capped or the demand didn't need, with the difference not
            // lent to anything else (review RC-28), work its windows couldn't hold.
            var file = s1.BenchmarkFile;
            var shortGroups = file is not null
                ? file.Select(kv => (G: kv.Key, H: kv.Value - (s1.Allocations.FirstOrDefault(a => a.Group == kv.Key)?.AllocatedHours ?? 0)))
                    .Where(x => x.H > 0.25 + 1e-9)
                    .Select(x => $"{GroupName.GetValueOrDefault(x.G, x.G)} {Js.ToFixed(x.H, 2)} h")
                    .ToList()
                : [];
            message = $"Planned hours are {Js.ToFixed(-gap, 2)} h under the benchmark {of}{(shortGroups.Count > 0 ? $" - planned below the benchmark file: {string.Join(", ", shortGroups)}" : "")}";
        }
        else if (gap > ToleranceH + 1e-9)
        {
            var explained = allowed.Admin + allowed.Reset + allowed.Checkout + allowed.Cover + allowed.Minimum + allowed.Managers + allowed.Fixed + allowed.Breaks;
            var reasons = new List<string?>
            {
                allowed.Admin > 1e-9 ? $"Admin tasks past the Admin benchmark {Js.ToFixed(allowed.Admin, 2)} h" : null,
                allowed.Reset > 1e-9 ? $"Reset tasks past the Reset benchmark {Js.ToFixed(allowed.Reset, 2)} h" : null,
                allowed.Checkout > 1e-9 ? $"checkout keeping its hours {Js.ToFixed(allowed.Checkout, 2)} h" : null,
                allowed.Cover > 1e-9 ? $"cover {Js.ToFixed(allowed.Cover, 2)} h" : null,
                allowed.Minimum > 1e-9 ? $"3-hour minimums {Js.ToFixed(allowed.Minimum, 2)} h" : null,
                allowed.Managers > 1e-9 ? $"the managers' handover and six-hour shifts {Js.ToFixed(allowed.Managers, 2)} h" : null,
                allowed.Fixed > 1e-9 ? $"fixed shifts {Js.ToFixed(allowed.Fixed, 2)} h" : null,
                allowed.Breaks > 1e-9 ? $"time added so breaks don't cost work {Js.ToFixed(allowed.Breaks, 2)} h" : null,
            }.Where(r => r is not null).ToList();
            if (gap - explained > ToleranceH + 1e-9)
            {
                status = "error";
                message = $"Planned hours are {Js.ToFixed(gap, 2)} h over the benchmark {of} - {Js.ToFixed(gap - explained, 2)} h of it is not explained by an hours issue, cover or the 3-hour minimum";
            }
            else
            {
                status = "warning";
                message = $"Hours issue: {Js.ToFixed(gap, 2)} h over the benchmark {of} - {string.Join(", ", reasons)}";
            }
        }
        return new HoursCheck { Worked = worked, Benchmark = benchmark, Gap = gap, Allowed = allowed, Status = status, Message = message };
    }
}
