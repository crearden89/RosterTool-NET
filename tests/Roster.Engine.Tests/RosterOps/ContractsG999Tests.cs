// Port of src/lib/roster/contracts.test.ts (its two G999 cases, which run the
// fortnight builder through the G999 audit harness; the other cases are in
// ContractsTests.cs).

using System.Text.RegularExpressions;
using Roster.Engine.Pay;
using Roster.Engine.RosterOps;
using Roster.Engine.Tests.Rostering.Fortnight;

namespace Roster.Engine.Tests.RosterOps;

public class ContractsG999Tests
{
    // The engine's contract lines: per person under or above, and the team's.
    private static readonly Regex ContractLine = new("h (under|above) contract this fortnight$|^The team's contracts hold|^The plans need more hours");

    public static TheoryData<string, bool> Scenarios => new()
    {
        { "the G999 fortnight", false },
        { "G999 with every weekend-off switch cleared", true },
    };

    // The contract figures are the paid hours the roster's shifts hold (and the
    // credit the engine gives), and every contract line it wrote is one the
    // figures replace.
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Are_the_hours_the_roster_holds(string label, bool markersCleared)
    {
        var scenario = markersCleared ? new AuditScenario { Markers = [] } : new AuditScenario();
        var run = G999Audit.RunAudit(scenario);
        var (_, _, breakRules) = G999Audit.BuildDays(scenario);
        var paid = new Dictionary<string, double>();
        foreach (var day in run.Days)
        {
            foreach (var s in run.Result.ByDate.Get(day.Date)?.Shifts ?? [])
            {
                if (s.Unfilled) continue;
                var unpaid = Breaks.BreakForShift(breakRules[s.Role].BreakType, (s.End - s.Start) / 60).UnpaidMinutes;
                paid[s.EmployeeId] = paid.GetValueOrDefault(s.EmployeeId) + (s.End - s.Start - unpaid) / 60;
            }
        }
        foreach (var o in run.Result.Outcomes) Assert.Equal(paid.GetValueOrDefault(o.EmployeeId), o.PaidHours, 9);
        var f = Contracts.ContractFigures(run.Result.Outcomes);
        Assert.True(f.People.Count > 0, label);
        Assert.DoesNotContain(Contracts.OtherFortnightFlags(run.Result.Flags), x => ContractLine.IsMatch(x));
    }
}
