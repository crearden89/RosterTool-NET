// Port of src/lib/stage2/production.invariants.test.ts.
// ─── The base roster, built by production, checked against every rule ───────
// Roster Engine Review 07/11/2026, fix order step 2. This runs the REAL
// production code end to end: Stage 1 -> the day's plan slots -> the Task view
// (plan shifts) and the fortnight roster. Only the trade curve and the POS
// morning customers are stood in for (HarnessScenario). See RosterInvariants
// for the checks.
//
// The corpus: the G999 31/08-13/09 fortnight, plus generated store-fortnights
// 1-20 (the TypeScript harness's own, dumped by scenario-dump.ts). Every check
// is counted as days that break it, per surface: "slots" (the shaper's shifts
// before painting, cover checks only), "plan" (the Task view) and "roster".
//
// THE RATCHET: production.invariants.baseline.json is the TypeScript app's
// recorded baseline (a004032), copied verbatim. The test fails when the .NET
// pipeline breaks a rule on more days than it, or has more blank or lost
// quarter-hours or fewer clean days - and when it plans or rosters a different
// set of days. Measures that come out BETTER than the TypeScript are printed:
// on this corpus the two pipelines are meant to agree exactly.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Services.Planning;
using Roster.Data.Services.Rostering;
using Roster.Data.Tests.Infrastructure;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Shaper;
using Xunit.Abstractions;

namespace Roster.Data.Tests.Services.Harness;

[Trait("Category", "SqlServer")]
[Trait("Category", "Slow")]
public sealed class ProductionInvariantsTests(SqlServerDatabaseFixture db, ITestOutputHelper log) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    private const int CorpusSeeds = 20;

    /// <summary>Only the cover checks mean anything before tiles are painted.</summary>
    private static readonly HashSet<string> SlotChecks = ["i_lone_worker", "i_no_manager_present", "j_jsa_fewer_than_3_adults"];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Tally
    {
        /// <summary>Days checked, by "corpus/surface" (plus Mondays and PH eves).</summary>
        public SortedDictionary<string, double> Days { get; } = new(StringComparer.Ordinal);
        /// <summary>Days breaking each check, by "corpus/surface/check".</summary>
        public SortedDictionary<string, double> Violations { get; } = new(StringComparer.Ordinal);
        /// <summary>The first example of each, for the report.</summary>
        public Dictionary<string, string> Examples { get; } = new();
        /// <summary>Quarter-hours, by "corpus/surface/what": rostered, blank, lost; and clean days.</summary>
        public SortedDictionary<string, double> Measures { get; } = new(StringComparer.Ordinal);
    }

    private static void CountDay(Tally t, string key) => t.Days[key] = t.Days.GetValueOrDefault(key) + 1;

    private static void Measure(Tally t, string prefix, IReadOnlyList<CheckShift> shifts, Stage1Computation s1, IReadOnlyList<Violation> vs)
    {
        double rostered = 0;
        double blank = 0;
        foreach (var s in shifts)
        {
            for (var m = s.Start; m < s.End; m += 15)
            {
                rostered++;
                var g = s.Blocks.FirstOrDefault(b => b.Time == m)?.Group;
                if (g is null || g == "flex") blank++;
            }
        }
        void Add(string what, double n) => t.Measures[$"{prefix}/{what}"] = t.Measures.GetValueOrDefault($"{prefix}/{what}") + n;
        Add("rostered_quarter_hours", rostered);
        Add("blank_quarter_hours", blank);
        // Work Stage 1 planned that no tile carries - lost, not moved.
        Add("lost_quarter_hours", RosterInvariants.LostQuarterHours(s1, shifts));
        // A day that breaks no rule at all, the first planning step's errors
        // included (preferences and notes aside).
        Add("clean_days", vs.Any(v => RosterInvariants.DescribeInvariant(v.Inv).Kind == "rule") ? 0 : 1);
    }

    private static void Record(Tally t, string prefix, string where, IEnumerable<Violation> vs)
    {
        var seen = new HashSet<string>();
        foreach (var v in vs)
        {
            var key = $"{prefix}/{v.Inv}";
            if (!seen.Add(key)) continue;
            t.Violations[key] = t.Violations.GetValueOrDefault(key) + 1;
            t.Examples.TryAdd(key, $"{where}: {v.Detail}");
        }
    }

    private async Task RunFortnightAsync(Tally t, string corpus, string name)
    {
        await db.ResetAsync();
        var (s, sp) = await PipelineKit.ScenarioAsync(db, name);
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        var services = scope.ServiceProvider;
        var p = s.Params;
        var dates = Period.PeriodDatesFrom(s.PeriodStart);
        var isoDates = dates.Select(d => d.ToIso()).ToList();
        var fortnight = await services.GetRequiredService<IRosterLoader>().ComputeRosterFortnightAsync(s.StoreId, s.PeriodStart);
        var stage1 = services.GetRequiredService<IStage1Loader>();
        var daySlots = services.GetRequiredService<IDaySlotsLoader>();
        var planShiftsLoader = services.GetRequiredService<IPlanShiftsLoader>();
        var employees = s.Table("employees");
        var juniorWeek = new JsMap<string, double>(); // employee:week -> paid hours
        string Where(string date) => p.Seed is { } seed ? $"seed {seed} {date}" : date;

        foreach (var date in dates)
        {
            var iso = date.ToIso();
            var s1 = await stage1.ComputeStage1Async(s.StoreId, date);
            if (!s1.HasData) continue;
            var dow = Period.DowMon0(date);
            var eveningLR = p.Ph is { } ph && ph.Realloc && isoDates.IndexOf(ph.Date) == isoDates.IndexOf(iso) + 1;
            CheckDayContext Ctx(string surface) => PipelineKit.DayContext(s, s1, date, surface, eveningLR);
            if (dow == 0) CountDay(t, $"{corpus}/monday");
            if (eveningLR) CountDay(t, $"{corpus}/ph-eve");

            // The shaper's shifts, before painting: cover only.
            var shaped = (await daySlots.ComputeDaySlotsAsync(s.StoreId, date)).Shaped;
            var slots = (shaped?.Slots ?? []).Select(x => new CheckShift($"{x.Role}:{x.Id}", PipelineKit.PlanRole(x.Role), x.Start, x.End, [])).ToList();
            CountDay(t, $"{corpus}/slots");
            Record(t, $"{corpus}/slots", Where(iso), RosterInvariants.CheckDay(slots, Ctx("slots")).Where(v => SlotChecks.Contains(v.Inv)));

            // The Task view.
            var plan = await planShiftsLoader.ComputePlanShiftsAsync(s.StoreId, date);
            var planShifts = PipelineKit.PlanCheckShifts(plan, x => $"{x.PlanRole}:{x.SlotId}");
            var pv = RosterInvariants.CheckDay(planShifts, Ctx("plan"));
            foreach (var sl in shaped?.Slots ?? [])
            {
                var ps = plan.Shifts.Find(x => x.SlotId == sl.Id);
                if (ps is not null && sl.Id.StartsWith("fixed-", StringComparison.Ordinal) && (ps.Start != sl.Start || ps.End != sl.End))
                    pv.Add(new Violation("l_fixed_slot_reshaped", $"{sl.Id} {Js.NumberToString(sl.Start)}-{Js.NumberToString(sl.End)} -> {Js.NumberToString(ps.Start)}-{Js.NumberToString(ps.End)}"));
            }
            PlanSlot? SlotOf(string id) => shaped?.Slots.Find(x => x.Id == id);
            PlanShift? ShiftOf(string id) => plan.Shifts.Find(x => x.SlotId == id);
            if (SlotOf("mgr-open") is { } so && SlotOf("mgr-close") is { } sc && ShiftOf("mgr-open") is { } po && ShiftOf("mgr-close") is { } pc)
            {
                var before = Math.Min(so.End, sc.End) - Math.Max(so.Start, sc.Start);
                var after = Math.Min(po.End, pc.End) - Math.Max(po.Start, pc.Start);
                if (before >= 60 && after < 60) pv.Add(new Violation("l_mgr_handover_destroyed", $"overlap {Js.NumberToString(before)}min -> {Js.NumberToString(after)}min"));
            }
            CountDay(t, $"{corpus}/plan");
            Record(t, $"{corpus}/plan", Where(iso), pv);
            Measure(t, $"{corpus}/plan", planShifts, s1, pv);

            // The named roster.
            if (!fortnight.ByDate.TryGet(date, out var r) || !r.HasData) continue;
            var unfilled = r.Shifts.Count(x => x.Unfilled);
            var rosterShifts = r.Shifts.Where(x => !x.Unfilled).Select(x => PipelineKit.RosterCheckShift(x, $"{x.Role}:{x.Name}")).ToList();
            var rv = RosterInvariants.CheckDay(rosterShifts, Ctx("roster"));
            var planMin = planShifts.Sum(x => x.End - x.Start);
            var rosterMin = rosterShifts.Sum(x => x.End - x.Start);
            // Time the roster added so breaks don't cost work, or cut so a shorter
            // break leaves no time empty, is explained by its own note on the day.
            var explained = Js.Round((BreakTime.RosterBreakTimeAdded(r.Flags) - BreakTime.RosterBreakCutHours(r.Flags)) * 60);
            if (planMin + explained != rosterMin)
                rv.Add(new Violation("x_plan_vs_roster_hours_differ", $"plan {Js.ToFixed(planMin / 60, 2)}h vs roster {Js.ToFixed(rosterMin / 60, 2)}h of shifts"));
            if (unfilled > 0) rv.Add(new Violation("k_unfilled_shift", $"{unfilled} open shift(s)"));
            // A fixed roster is worked exactly as set.
            foreach (var f in p.Fixed.Where(x => x.Dow == dow))
            {
                var emp = employees.FirstOrDefault(e => e.GetProperty("name").GetString() == f.EmpName);
                if (emp.ValueKind != JsonValueKind.Object || !(emp.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True)) continue;
                var id = HarnessScenario.IdFor(emp.GetProperty("id").GetString()!);
                var worked = r.Shifts.FirstOrDefault(x => !x.Unfilled && Guid.TryParse(x.EmployeeId, out var g) && g == id);
                if (worked is null) rv.Add(new Violation("l_fixed_roster_not_worked", $"{f.EmpName} {Js.NumberToString(f.Start / 60)}-{Js.NumberToString(f.End / 60)}"));
                else if (worked.Start != f.Start || worked.End != f.End)
                    rv.Add(new Violation("l_fixed_roster_moved", $"{f.EmpName} fixed {Js.NumberToString(f.Start / 60)}-{Js.NumberToString(f.End / 60)}, rostered {Js.NumberToString(worked.Start / 60)}-{Js.NumberToString(worked.End / 60)}"));
            }
            CountDay(t, $"{corpus}/roster");
            Record(t, $"{corpus}/roster", Where(iso), rv);
            Measure(t, $"{corpus}/roster", rosterShifts, s1, rv);

            var week = isoDates.IndexOf(iso) < 7 ? 0 : 1;
            foreach (var x in r.Shifts)
            {
                if (x.Unfilled || x.Role != "JSA") continue;
                var paid = (x.End - x.Start - Breaks.BreakForRole("JSA", (x.End - x.Start) / 60).UnpaidMinutes) / 60;
                var k = $"{x.EmployeeId}:{week}";
                juniorWeek.Set(k, juniorWeek.GetOr(k, 0) + paid);
            }
        }

        // Junior weekly caps: 15h in a school-term week, 30h in a holiday week.
        foreach (var (k, hours) in juniorWeek)
        {
            var parts = k.Split(':');
            var w = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var term = p.IsTermWeek(w);
            var cap = term ? RosteringRules.Default.JsaTermWeekCapH : RosteringRules.Default.JsaHolidayWeekCapH;
            if (hours > cap + 1e-9)
            {
                var who = employees.FirstOrDefault(e => HarnessScenario.IdFor(e.GetProperty("id").GetString()!).ToString("D") == parts[0]);
                var whoName = who.ValueKind == JsonValueKind.Object ? who.GetProperty("name").GetString() : parts[0];
                Record(t, $"{corpus}/roster", $"{(p.Seed is { } seed ? $"seed {seed} " : "")}{s.PeriodStart.ToIso()} week {w + 1}",
                    [new Violation(term ? "j_jsa_term_week_over_cap" : "j_jsa_holiday_week_over_cap", $"{whoName} {Js.ToFixed(hours, 2)}h vs {Js.NumberToString(cap)}h")]);
            }
        }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Tally? cached;

    private async Task<Tally> CorpusAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (cached is not null) return cached;
            var t = new Tally();
            await RunFortnightAsync(t, "g999", "g999");
            for (var seed = 1; seed <= CorpusSeeds; seed++) await RunFortnightAsync(t, "generated", $"gen{seed}");
            return cached = t;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static JsonElement Baseline()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "Services", "Harness", "production.invariants.baseline.json");
            if (File.Exists(file)) return JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
        }
        throw new FileNotFoundException("production.invariants.baseline.json (tests/Roster.Data.Tests/Services/Harness)");
    }

    private static Dictionary<string, double> Section(JsonElement baseline, string name) =>
        baseline.GetProperty(name).EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble());

    [SqlServerFact]
    public async Task Plans_and_rosters_every_day_of_the_corpus()
    {
        var t = await CorpusAsync();
        // A day the harness silently skipped would read as a clean day.
        Assert.Equal(14, t.Days.GetValueOrDefault("g999/plan"));
        Assert.Equal(14, t.Days.GetValueOrDefault("g999/roster"));
        Assert.Equal(CorpusSeeds * 14, t.Days.GetValueOrDefault("generated/plan"));
        Assert.Equal(CorpusSeeds * 14, t.Days.GetValueOrDefault("generated/roster"));
    }

    // Fails only when the plan gets WORSE than the TypeScript's recorded
    // baseline: a rule broken on more days, or more blank or lost quarter-hours,
    // or fewer clean days.
    [SqlServerFact]
    public async Task Breaks_no_rule_more_often_than_the_recorded_baseline()
    {
        var t = await CorpusAsync();
        var was = Baseline();
        var wasViolations = Section(was, "violations");
        var wasMeasures = Section(was, "measures");
        var worse = new List<string>();
        var better = new List<string>();
        foreach (var key in wasViolations.Keys.Concat(t.Violations.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var before = wasViolations.GetValueOrDefault(key);
            var after = t.Violations.GetValueOrDefault(key);
            if (after > before) worse.Add($"WORSE  {key}: {before} -> {after} days  e.g. {t.Examples.GetValueOrDefault(key)}");
            else if (after < before) better.Add($"better {key}: {before} -> {after} days");
        }
        // Quarter-hour measures: fewer blank or lost is better; more clean days is
        // better. Rostered quarter-hours have no direction of their own.
        foreach (var (key, after) in t.Measures)
        {
            if (!wasMeasures.TryGetValue(key, out var before)) continue;
            var lowerIsBetter = key.EndsWith("blank_quarter_hours", StringComparison.Ordinal) || key.EndsWith("lost_quarter_hours", StringComparison.Ordinal);
            var higherIsBetter = key.EndsWith("clean_days", StringComparison.Ordinal);
            if (after == before) continue;
            if (!(lowerIsBetter || higherIsBetter))
            {
                log.WriteLine($"differs {key}: {before} -> {after}");
                continue;
            }
            var improved = lowerIsBetter ? after < before : after > before;
            (improved ? better : worse).Add($"{(improved ? "better" : "WORSE ")} {key}: {before} -> {after}");
        }
        if (better.Count > 0) log.WriteLine($"Better than the TypeScript's baseline:\n{string.Join("\n", better)}");
        // ROSTER_REPORT=<file>: the whole comparison, worse, better and equal, to that file.
        if (Environment.GetEnvironmentVariable("ROSTER_REPORT") is { Length: > 0 } report)
            File.WriteAllLines(report, [
                .. worse, .. better,
                .. t.Measures.Select(m => $"measure {m.Key}: TypeScript {wasMeasures.GetValueOrDefault(m.Key)} .NET {m.Value}"),
                .. t.Violations.Select(v => $"violation {v.Key}: TypeScript {wasViolations.GetValueOrDefault(v.Key)} .NET {v.Value}  e.g. {t.Examples.GetValueOrDefault(v.Key)}"),
                .. wasViolations.Keys.Where(k => !t.Violations.ContainsKey(k)).Select(k => $"violation {k}: TypeScript {wasViolations[k]} .NET 0"),
            ]);
        Assert.True(worse.Count == 0, $"The plan got worse than the recorded baseline.\n{string.Join("\n", worse)}");
        Assert.Equal(Section(was, "days").OrderBy(x => x.Key, StringComparer.Ordinal).ToList(), t.Days.ToList());
    }
}
