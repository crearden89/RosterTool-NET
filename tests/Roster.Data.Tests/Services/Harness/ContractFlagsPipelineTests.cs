// Port of src/lib/stage2/contractFlags.test.ts.
// The fortnight's contract flags are written from the final hours. The check
// writes them partway through; afterwards the painter trims shifts, the break
// rescue swaps who holds two shifts or adds time, and a break that leaves time
// empty cuts another shift. A swap alone used to leave both people's lines
// with their hours from before it. Real engines against the TypeScript
// harness's store-fortnights, as production runs them (the held plan, then the
// roster built from it), through the data services on SQL Server.

using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Services.Held;
using Roster.Data.Tests.Infrastructure;
using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Data.Tests.Services.Harness;

[Trait("Category", "SqlServer")]
[Trait("Category", "Slow")]
public sealed partial class ContractFlagsPipelineTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(HarnessScenario Scenario, RosterSnapshot Roster)> RosterAsync(string name)
    {
        var (s, sp) = await PipelineKit.ScenarioAsync(db, name);
        await using var _ = sp;
        using var scope = HarnessScenario.SystemScope(sp);
        await scope.ServiceProvider.GetRequiredService<IHeldPlans>().ComputeAndStorePlanSnapshotAsync(s.StoreId, s.PeriodStart, PipelineKit.User1);
        var roster = await scope.ServiceProvider.GetRequiredService<IHeldRosters>().ComputeAndStoreRosterSnapshotAsync(s.StoreId, s.PeriodStart, PipelineKit.User1);
        return (s, roster!);
    }

    [GeneratedRegex("^Shifts swapped so breaks don't cost work: ")]
    private static partial Regex Swapped();

    private static List<string> SwapNotes(RosterSnapshot r) => r.ByDate.SelectMany(d => d.Value.Flags.Where(f => Swapped().IsMatch(f))).ToList();

    [SqlServerFact]
    public async Task Give_both_peoples_final_hours_when_the_break_rescue_swaps_their_shifts_after_the_check()
    {
        // Generated fortnight 10: one swap after the contract check and no shift
        // trimmed - the case whose two lines kept the hours from before the swap
        // (18.25 h and 7.00 h short, where the shifts leave 19.75 h and 5.50 h).
        var (s, r) = await RosterAsync("gen10");
        var swaps = SwapNotes(r);
        Assert.True(swaps.Count == 1, "the fortnight no longer has a swap - pick another that does");

        // What each person's final shifts pay, read from the shifts themselves.
        var breakTypes = PipelineKit.BreakTypes(s);
        var paid = new Dictionary<string, double>();
        foreach (var (_, day) in r.ByDate)
            foreach (var x in day.Shifts)
            {
                if (x.Unfilled) continue;
                var unpaid = Breaks.BreakForShift(breakTypes[x.Role], (x.End - x.Start) / 60).UnpaidMinutes;
                paid[x.EmployeeId] = paid.GetValueOrDefault(x.EmployeeId) + (x.End - x.Start - unpaid) / 60;
            }
        var people = r.Outcomes!.Where(o => o.Role != "JSA").ToList();
        Assert.Equal(2, people.Count(o => swaps[0].Contains(o.Name)));
        foreach (var o in people)
        {
            var gap = paid.GetValueOrDefault(o.EmployeeId) + o.CreditedHours - o.ContractPeriodHours;
            var lines = r.FortnightFlags.Where(f => f.StartsWith($"{o.Name} is ", StringComparison.Ordinal) && f.Contains(" contract this fortnight")).ToList();
            if (gap < -0.5) Assert.Equal([$"{o.Name} is {Js.ToFixed(-gap, 2)}h under contract this fortnight"], lines);
            else if (gap > 0.5) Assert.Equal([$"{o.Name} is {Js.ToFixed(gap, 2)}h above contract this fortnight"], lines);
            else Assert.Empty(lines);
        }
    }

    // Pinned from the engine before the lines were read again at the end
    // (main 66da263): a fortnight with no swap reads exactly as it did.
    [SqlServerFact]
    public async Task Read_exactly_as_before_in_a_fortnight_with_no_swap()
    {
        var (_, one) = await RosterAsync("gen1");
        Assert.Empty(SwapNotes(one));
        Assert.Equal(
            [
                "Noah Reyes is 16.75h under contract this fortnight",
                "Harvey Chen is 16.00h under contract this fortnight",
                "Rory Nolan is 9.75h under contract this fortnight",
                "Simon Dixon is 11.25h under contract this fortnight",
                "Marcus Turner is 11.00h under contract this fortnight",
                "Adam Mahoney is 13.50h under contract this fortnight",
                "Angus Collins is 15.75h under contract this fortnight",
                "Ella Bishop is 19.33h under contract this fortnight",
                "Rhys Brooks is 19.75h under contract this fortnight",
                "Blake Cooper is 37.00h under contract this fortnight",
                "Stuart Ramsay is 37.00h under contract this fortnight",
                "Cara Mackay is 27.00h under contract this fortnight",
                "Grace Shaw-Fletcher is 27.00h under contract this fortnight",
                "The team's contracts hold 261.08h more than the plans need this fortnight: 13 people are short of contract and paid the difference",
            ],
            one.FortnightFlags);
        await db.ResetAsync();
        var (_, four) = await RosterAsync("gen4");
        Assert.Empty(SwapNotes(four));
        Assert.Equal(
            [
                "Noah Reyes is 7.50h under contract this fortnight",
                "Harvey Chen is 14.50h under contract this fortnight",
                "Simon Dixon is 6.00h under contract this fortnight",
                "Adam Mahoney is 7.00h under contract this fortnight",
                "Angus Collins is 12.00h under contract this fortnight",
                "Ella Bishop is 13.92h under contract this fortnight",
                "Rhys Brooks is 9.00h under contract this fortnight",
                "Blake Cooper is 1.00h under contract this fortnight",
                "Stuart Ramsay is 6.17h under contract this fortnight",
                "Cara Mackay is 24.00h under contract this fortnight",
                "Grace Shaw-Fletcher is 24.00h under contract this fortnight",
                "Sarah Bailey is 20.75h under contract this fortnight",
                "Annie Morgan is 15.90h under contract this fortnight",
                "Megan Carter is 15.90h under contract this fortnight",
                "The team's contracts hold 167.88h more than the plans need this fortnight: 14 people are short of contract and paid the difference",
                "Rory Nolan is 5.75h above contract this fortnight",
                "Marcus Turner is 4.00h above contract this fortnight",
            ],
            four.FortnightFlags);
    }
}
