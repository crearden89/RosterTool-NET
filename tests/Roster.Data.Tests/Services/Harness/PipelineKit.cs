// New: what the whole-pipeline ports of the stage 2 tests share - a scenario
// seeded and served, the held plan as the TypeScript tests build it, and the
// shifts in the form the day checker (RosterInvariants) reads.

using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Services.Planning;
using Roster.Data.Tests.Infrastructure;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Fortnight;

namespace Roster.Data.Tests.Services.Harness;

internal static class PipelineKit
{
    public static readonly Guid User1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    /// <summary>The slot type's role for the checks (PLAN_ROLE).</summary>
    public static string PlanRole(string slotRole) => slotRole switch { "MGR" => "ASM", "JSA" => "JSA", _ => "SA" };

    public static async Task<(HarnessScenario Scenario, ServiceProvider Services)> ScenarioAsync(SqlServerDatabaseFixture db, string name)
    {
        var scenario = HarnessScenario.Load(name);
        await using (var sys = db.System("seed the harness scenario")) await scenario.SeedAsync(sys, User1);
        return (scenario, scenario.Services(db.ConnectionString));
    }

    /// <summary>The plan for every date of the fortnight, as the held plan holds it (Stage 1, then the Task view where there is data).</summary>
    public static async Task<List<HeldPlanDay>> HeldPlanAsync(IServiceProvider sp, HarnessScenario s)
    {
        var stage1 = sp.GetRequiredService<IStage1Loader>();
        var planShifts = sp.GetRequiredService<IPlanShiftsLoader>();
        var days = new List<HeldPlanDay>();
        foreach (var date in Period.PeriodDatesFrom(s.PeriodStart))
        {
            var result = await stage1.ComputeStage1Async(s.StoreId, date);
            var plan = result.HasData ? await planShifts.ComputePlanShiftsAsync(s.StoreId, date) : null;
            days.Add(new HeldPlanDay(date, result, plan));
        }
        return days;
    }

    /// <summary>Paid maximum by role, from the scenario's break_rules (the store assistant's is the scenario's saMax).</summary>
    public static Dictionary<string, double> MaxPaid(HarnessScenario s) =>
        s.Table("break_rules").ToDictionary(r => r.GetProperty("role").GetString()!, r => r.GetProperty("max_shift_hours").GetDouble());

    /// <summary>Break type by role, from the scenario's break_rules.</summary>
    public static Dictionary<string, string> BreakTypes(HarnessScenario s) =>
        s.Table("break_rules").ToDictionary(r => r.GetProperty("role").GetString()!, r => r.GetProperty("break_type").GetString()!);

    public static List<CheckShift> PlanCheckShifts(PlanShiftComputation plan, Func<PlanShift, string> label) =>
        plan.Shifts.Select(s => new CheckShift(label(s), PlanRole(s.PlanRole), s.Start, s.End, s.Blocks)).ToList();

    public static CheckShift RosterCheckShift(Shift s, string label) => new(label, s.Role, s.Start, s.End, s.Blocks);

    public static CheckDayContext DayContext(HarnessScenario s, Stage1Computation s1, DateOnly date, string surface, bool eveningLR)
    {
        var p = s.Params;
        var iso = date.ToIso();
        var school = p.IsSchoolDay(iso);
        return new CheckDayContext
        {
            Date = iso,
            Surface = surface,
            S1 = s1,
            Bench = p.BenchOn(iso),
            NonOpMin = p.NonOpMinOn(iso),
            IsSchoolDay = school,
            BreakRulesMaxPaid = MaxPaid(s),
            EveningLR = eveningLR,
            ShaperMax = RosterInvariants.PlanShaperMax(p.SaMax, school),
        };
    }
}
