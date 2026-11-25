// Port of src/lib/roster/contracts.held.test.ts and src/lib/subtasks/carry.held.test.ts:
// the whole pipeline - the plan generated and held, the roster built from the held
// plan and held, both read back - through the data services on SQL Server, on the
// TypeScript harness's own store-fortnights.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Services.Held;
using Roster.Data.Services.SubTasks;
using Roster.Data.Tests.Infrastructure;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.SubTasks;

namespace Roster.Data.Tests.Services.Harness;

[Trait("Category", "SqlServer")]
public sealed partial class HeldPipelineTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid User1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(HarnessScenario Scenario, ServiceProvider Services)> ScenarioAsync(string name)
    {
        var scenario = HarnessScenario.Load(name);
        await using (var sys = db.System("seed the harness scenario")) await scenario.SeedAsync(sys, User1);
        return (scenario, scenario.Services(db.ConnectionString));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, RosterJson.Options);

    // ─── contracts.held.test.ts ──────────────────────────────────────────────
    // The contract figures ride with the held roster: a roster built through the
    // production code holds the engine's contract outcomes beside its flags, the
    // read gives them back exactly, and they are the paid hours its shifts hold.

    [GeneratedRegex("h (under|above) contract this fortnight$|^The team's contracts hold|^The plans need more hours")]
    private static partial Regex ContractLine();

    [SqlServerTheory]
    [InlineData("g999")] // the G999 fortnight
    [InlineData("gen3")] // generated store-fortnight 3
    [InlineData("gen11")] // generated store-fortnight 11
    public async Task Holds_the_engines_outcomes_the_paid_hours_its_shifts_hold(string name)
    {
        var (scenario, sp) = await ScenarioAsync(name);
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        var plans = scope.ServiceProvider.GetRequiredService<IHeldPlans>();
        var rosters = scope.ServiceProvider.GetRequiredService<IHeldRosters>();
        await plans.ComputeAndStorePlanSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var built = await rosters.ComputeAndStoreRosterSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var read = await rosters.ReadSnapshotsAsync(scenario.StoreId, scenario.PeriodStart);

        Assert.NotEmpty(built!.Outcomes!);
        Assert.Equal(Json(built.Outcomes), Json(read.Roster!.Outcomes));
        Assert.Equal(User1, read.Plan!.GeneratedBy);
        var rules = await scope.ServiceProvider.GetRequiredService<Roster.Data.Services.RefData.IRefData>().GetBreakRulesAsync();
        var paid = new Dictionary<string, double>();
        foreach (var (_, day) in read.Roster.ByDate)
            foreach (var s in day.Shifts)
            {
                if (s.Unfilled) continue;
                var unpaid = Breaks.BreakForShift(rules[s.Role].BreakType, (s.End - s.Start) / 60).UnpaidMinutes;
                paid[s.EmployeeId] = paid.GetValueOrDefault(s.EmployeeId) + (s.End - s.Start - unpaid) / 60;
            }
        foreach (var o in read.Roster.Outcomes!) Assert.Equal(paid.GetValueOrDefault(o.EmployeeId), o.PaidHours, 9);
        Assert.NotEmpty(Contracts.ContractFigures(read.Roster.Outcomes!).People);
        Assert.DoesNotContain(Contracts.OtherFortnightFlags(read.Roster.FortnightFlags), x => ContractLine().IsMatch(x));
    }

    // ─── carry.held.test.ts ──────────────────────────────────────────────────
    // The plan's sub-tasks on the roster (G999 fortnight).

    private static List<object> TilesOf(JsMap<DateOnly, RosterComputation> byDate) =>
        byDate.Select(kv => (object)new { d = kv.Key, shifts = kv.Value.Shifts.Select(s => new { s.EmployeeId, s.Start, s.End, tiles = string.Join(",", s.Blocks.Select(b => $"{Js.NumberToString(b.Time)}:{b.Group}")) }).ToList() }).ToList();

    private sealed record Unchanged(DisplayBlock R, DisplayBlock P, int Ri, int Pi);

    /// <summary>Every roster tile that comes unchanged from the plan, with the plan's tile.</summary>
    private static List<Unchanged> UnchangedTiles(IReadOnlyList<CarryShift> plan, IReadOnlyList<CarryShift> roster)
    {
        var out_ = new List<Unchanged>();
        foreach (var (ri, pi) in Carry.ShiftPairs(plan, roster))
        {
            var at = new Dictionary<double, DisplayBlock>();
            foreach (var b in plan[pi].Blocks) at[b.Time] = b;
            foreach (var r in roster[ri].Blocks)
                if (at.TryGetValue(r.Time, out var p) && p.Group == r.Group && Catalogue.IsSubtaskGroup(r.Group)) out_.Add(new Unchanged(r, p, ri, pi));
        }
        return out_;
    }

    private static string Other(DisplayBlock b) =>
        Catalogue.Subtasks.First(g => g.Group == b.Group).Subtasks.First(s => s.Key != b.Sub).Key;

    [SqlServerFact]
    public async Task Labels_a_roster_from_a_plan_the_rules_labelled_exactly_as_the_rules_label_it()
    {
        var (scenario, sp) = await ScenarioAsync("g999");
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        var plans = scope.ServiceProvider.GetRequiredService<IHeldPlans>();
        var rosters = scope.ServiceProvider.GetRequiredService<IHeldRosters>();
        var labels = scope.ServiceProvider.GetRequiredService<ISubtaskLabels>();
        await plans.ComputeAndStorePlanSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var built = await rosters.ComputeAndStoreRosterSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var plan = (await rosters.ReadSnapshotsAsync(scenario.StoreId, scenario.PeriodStart)).Plan!;
        foreach (var (date, day) in built!.ByDate)
        {
            if (day.Shifts.Count == 0) continue;
            var s1 = plan.Days.First(d => d.Date == date).Result;
            var ctx = await labels.LabelContextAsync(scenario.StoreId, date, ResetSource.From(s1));
            Assert.Equal(Json(Label.LabelShifts(Carry.WithoutLabels(day.Shifts), ctx)), Json(day.Shifts));
        }
    }

    [GeneratedRegex(@"(\d\d:\d\d)–(\d\d:\d\d)")]
    private static partial Regex Span();

    [SqlServerFact]
    public async Task Carries_a_managers_relabels_to_the_tiles_the_roster_keeps_the_break_rescues_swapped_shifts_included()
    {
        var (scenario, sp) = await ScenarioAsync("g999");
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        var plans = scope.ServiceProvider.GetRequiredService<IHeldPlans>();
        var rosters = scope.ServiceProvider.GetRequiredService<IHeldRosters>();
        await plans.ComputeAndStorePlanSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var before = await rosters.ComputeAndStoreRosterSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var plan = (await rosters.ReadSnapshotsAsync(scenario.StoreId, scenario.PeriodStart)).Plan!;
        var rosterOf = before!.ByDate;

        // The relabels, day by day: a Reset tile from Decarding to Re-runs and two
        // Load Running tiles to Bread (as a Plan page edit saves them). On a day the
        // roster swapped two shifts' holders so breaks don't cost work, a tile of
        // each swapped shift too.
        var marks = new List<(DateOnly Date, int Pi, double Time, string Sub, bool Swapped)>();
        var swapDays = 0;
        foreach (var d in plan.Days)
        {
            var shifts = d.Plan?.Shifts ?? [];
            void Mark(int pi, double time, string sub, bool swapped = false)
            {
                var blocks = shifts[pi].Blocks;
                var bi = blocks.FindIndex(b => b.Time == time);
                blocks[bi] = blocks[bi] with { Sub = sub };
                marks.Add((d.Date, pi, time, sub, swapped));
            }
            var reset = shifts.SelectMany((s, pi) => s.Blocks.Where(b => b.Group == "reset" && b.Sub == "decarding").Select(b => (pi, b))).FirstOrDefault();
            if (reset.b is not null) Mark(reset.pi, reset.b.Time, "re_runs");
            var lr = shifts.FindIndex(s => s.Blocks.Count(b => b.Group == "load_running" && b.Sub != "bread") >= 2);
            if (lr >= 0)
                foreach (var b in shifts[lr].Blocks.Where(x => x.Group == "load_running" && x.Sub != "bread").Take(2).ToList()) Mark(lr, b.Time, "bread");
            if (!rosterOf.TryGet(d.Date, out var rday)) continue;
            var swap = rday.Flags.FirstOrDefault(f => f.StartsWith("Shifts swapped so breaks don't cost work", StringComparison.Ordinal));
            if (swap is null) continue;
            swapDays++;
            // A tile the roster kept unchanged on each swapped shift, now worked by the other person.
            var kept = UnchangedTiles(shifts.Select(CarryShift.Of).ToList(), rday.Shifts.Select(CarryShift.Of).ToList());
            foreach (Match m in Span().Matches(swap))
            {
                var (from, to) = (m.Groups[1].Value, m.Groups[2].Value);
                var pi = shifts.FindIndex(s => Grid.MinToTime(s.Start) == from && Grid.MinToTime(s.End) == to);
                var hit = kept.FirstOrDefault(x => x.Pi == pi && !marks.Any(k => k.Date == d.Date && k.Pi == pi && k.Time == x.P.Time));
                if (hit is not null) Mark(pi, hit.P.Time, Other(shifts[pi].Blocks.First(b => b.Time == hit.P.Time)), true);
            }
        }
        Assert.True(swapDays > 0);
        Assert.True((await plans.WritePlanSnapshotAsync(scenario.StoreId, scenario.PeriodStart, plan.Days, plan.Version)).Ok);

        var after = await rosters.ComputeAndStoreRosterSnapshotAsync(scenario.StoreId, scenario.PeriodStart, User1);
        var held = await rosters.ReadSnapshotsAsync(scenario.StoreId, scenario.PeriodStart);
        // Labels only: the roster's people, times and tiles are the ones it had.
        Assert.Equal(Json(TilesOf(before.ByDate)), Json(TilesOf(after!.ByDate)));
        Assert.Equal(Json(after.ByDate), Json(held.Roster!.ByDate));

        var keptCount = 0;
        var swappedKept = 0;
        foreach (var (date, day) in after.ByDate)
        {
            var planShifts = (held.Plan!.Days.FirstOrDefault(x => x.Date == date)?.Plan?.Shifts ?? []).Select(CarryShift.Of).ToList();
            var pairs = UnchangedTiles(planShifts, day.Shifts.Select(CarryShift.Of).ToList());
            // Every roster tile that comes unchanged from the relabelled plan has the plan's label.
            foreach (var x in pairs) Assert.True(x.R.Sub == x.P.Sub, $"{date} {Grid.MinToTime(x.R.Time)} {x.R.Group}");
            foreach (var m in marks.Where(x => x.Date == date))
            {
                var hit = pairs.FirstOrDefault(x => x.Pi == m.Pi && x.R.Time == m.Time);
                if (hit is null) continue;
                keptCount++;
                if (m.Swapped) swappedKept++;
                Assert.Equal(m.Sub, hit.R.Sub);
            }
        }
        Assert.True(keptCount > 20, $"kept {keptCount}");
        Assert.True(swappedKept > 0);
    }
}
