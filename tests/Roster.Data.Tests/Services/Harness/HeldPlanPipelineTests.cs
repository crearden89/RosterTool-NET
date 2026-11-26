// Port of src/lib/stage2/heldPlan.test.ts (its "roster from the held plan"
// cases; the unit cases are in the engine tests, Rostering/Fortnight/HeldPlanTests).
// The roster built from the HELD plan (Plan -> Review -> Roster): the plan the
// store and its EMSO approved is the plan that gets rostered - its shifts, its
// work, its edits. Real engines against the TypeScript harness's own
// store-fortnights, through the data services on SQL Server.

using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Services.Rostering;
using Roster.Data.Tests.Infrastructure;
using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;

namespace Roster.Data.Tests.Services.Harness;

[Trait("Category", "SqlServer")]
[Trait("Category", "Slow")]
public sealed class HeldPlanPipelineTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static List<string> Tills(HarnessScenario s, HeldPlanDay day, IReadOnlyList<CheckShift> shifts, string surface) =>
        RosterInvariants.CheckDay(shifts, PipelineKit.DayContext(s, day.Result, day.Date, surface, eveningLR: false))
            .Where(v => v.Inv == "a_tills")
            .Select(v => v.Detail)
            .ToList();

    [SqlServerTheory]
    [InlineData("g999")]
    [InlineData("gen1")]
    [InlineData("gen2")]
    [InlineData("gen3")]
    [InlineData("gen4")]
    [InlineData("gen5")]
    public async Task Rosters_every_planned_shift_and_fills_them_all_adding_no_tills_gap_the_plan_didnt_have(string name)
    {
        var (s, sp) = await PipelineKit.ScenarioAsync(db, name);
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        var held = await PipelineKit.HeldPlanAsync(scope.ServiceProvider, s);
        var roster = await scope.ServiceProvider.GetRequiredService<IRosterLoader>().ComputeRosterFortnightAsync(s.StoreId, s.PeriodStart, held);
        foreach (var day in held)
        {
            if (day.Plan is null) continue;
            var r = roster.ByDate[day.Date];
            var where = $"{name} {day.Date:yyyy-MM-dd}";
            Assert.True(day.Plan.Shifts.Count == r.Shifts.Count, where);
            Assert.True(!r.Shifts.Any(x => x.Unfilled), where);
            var planCs = PipelineKit.PlanCheckShifts(day.Plan, x => x.SlotId);
            var rosterCs = r.Shifts.Select(x => PipelineKit.RosterCheckShift(x, x.Name)).ToList();
            var planGaps = new HashSet<string>(Tills(s, day, planCs, "plan"));
            foreach (var gap in Tills(s, day, rosterCs, "roster")) Assert.True(planGaps.Contains(gap), $"{where} {gap}");
        }
    }

    [SqlServerFact]
    public async Task Carries_the_managers_edits_a_removed_shift_isnt_rostered_repainted_work_is()
    {
        var (s, sp) = await PipelineKit.ScenarioAsync(db, "g999");
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        var loader = scope.ServiceProvider.GetRequiredService<IRosterLoader>();
        var held = await PipelineKit.HeldPlanAsync(scope.ServiceProvider, s);
        var before = await loader.ComputeRosterFortnightAsync(s.StoreId, s.PeriodStart, held);
        // A day with store assistants and some cleaning on the plan.
        var day = held.First(d =>
            d.Plan is not null &&
            d.Plan.Shifts.Count(x => x.PlanRole == "SA") > 2 &&
            d.Plan.Shifts.Any(x => x.Blocks.Any(b => b.Group == "cleaning")));
        static int Cleaning(IEnumerable<Shift> shifts) => shifts.Sum(x => x.Blocks.Count(b => b.Group == "cleaning"));
        var beforeDay = before.ByDate[day.Date];
        Assert.True(Cleaning(beforeDay.Shifts) > 0);
        // Remove one store assistant's shift, and turn all the day's cleaning into Non-Op.
        var removed = day.Plan!.Shifts.First(x => x.PlanRole == "SA");
        var edited = day with
        {
            Plan = day.Plan with
            {
                Shifts = day.Plan.Shifts
                    .Where(x => !ReferenceEquals(x, removed))
                    .Select(x => x with { Blocks = x.Blocks.Select(b => b.Group == "cleaning" ? b with { Group = "non_op" } : b).ToList() })
                    .ToList(),
            },
        };
        var after = await loader.ComputeRosterFortnightAsync(s.StoreId, s.PeriodStart, held.Select(d => d.Date == day.Date ? edited : d).ToList());
        var afterDay = after.ByDate[day.Date];
        Assert.Equal(beforeDay.Shifts.Count - 1, afterDay.Shifts.Count);
        Assert.True(Cleaning(afterDay.Shifts) < Cleaning(beforeDay.Shifts));
    }
}
