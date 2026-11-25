// Port of src/lib/roster/contracts.test.ts (the flag cases; the figures' cases
// follow once the fortnight builder's ContractOutcome is on main).

using Roster.Engine.RosterOps;

namespace Roster.Engine.Tests.RosterOps;

public class ContractsTests
{
    [Fact]
    public void Replaces_every_contract_line_keeping_the_other_fortnight_flags_and_why_a_contract_cant_be_met()
    {
        const string runs = "Hannah Lee works 9 days in a row (23/11 - 01/12) - above the 7-day run warning";
        Assert.Equal(
            [runs, "Marcus Tan's contract can't be met: 120.00h/fortnight needs more than 5 shifts a week at the ASM maximum (120.00h/fortnight ceiling)"],
            Contracts.OtherFortnightFlags(
            [
                "Hannah Lee is 11.00h under contract this fortnight",
                "Isla Scott is 24.00h above contract this fortnight",
                "The team's contracts hold 102.58h more than the plans need this fortnight: 14 people are short of contract and paid the difference",
                "The plans need more hours than the team's contracts hold: 2 people are rostered 8.00h above contract this fortnight",
                runs,
                "Marcus Tan is 21.75h under contract this fortnight - unreachable: 120.00h/fortnight needs more than 5 shifts a week at the ASM maximum (120.00h/fortnight ceiling)",
            ]));
    }

    [Fact]
    public void Drops_stale_held_lines_from_before_a_swap_of_shifts()
    {
        Assert.Empty(Contracts.OtherFortnightFlags(["Chloe Park is 1.50h under contract this fortnight", "Zara Ahmed is 9.75h under contract this fortnight"]));
    }
}

public class ContractFiguresTests
{
    private static Roster.Engine.Rostering.Fortnight.ContractOutcome Outcome(string name, string role, double contract, double paid, double credited = 0) => new()
    {
        EmployeeId = $"id-{name}",
        Name = name,
        Role = role,
        ContractPeriodHours = contract,
        PaidHours = paid,
        CreditedHours = credited,
        ShortfallHours = Math.Max(0, contract - paid - credited),
    };

    [Fact]
    public void Names_who_is_under_and_above_contract_as_numbers_the_largest_gap_first()
    {
        var f = Contracts.ContractFigures([Outcome("Hannah Lee", "SM", 90, 79), Outcome("Isla Scott", "SA", 30, 54), Outcome("Noah Kim", "SA", 40, 30.5, 4)]);
        Assert.Equal([("Isla Scott", "over", 24.0), ("Hannah Lee", "under", 11.0), ("Noah Kim", "under", 5.5)], f.People.Select(p => (p.Name, p.Kind, p.Hours)));
        Assert.Equal((2.0, 1.0, 16.5, 24.0), (f.Under, f.Over, f.UnderHours, f.OverHours));
    }

    [Fact]
    public void Leaves_juniors_and_anyone_within_half_an_hour_of_contract_out()
    {
        var f = Contracts.ContractFigures([Outcome("Ella Nguyen", "JSA", 20, 6), Outcome("Liam Walsh", "SA", 40, 39.5), Outcome("Mia Chen", "SA", 40, 40.5)]);
        Assert.Empty(f.People);
        Assert.Null(f.Team);
        Assert.Equal(0, f.Lines.Size);
    }

    [Fact]
    public void Words_the_teams_line_as_the_engine_does_whichever_way_the_team_is_out()
    {
        Assert.Equal(
            "The team's contracts hold 9.00h more than the plans need this fortnight: 1 person is short of contract and paid the difference",
            Contracts.ContractFigures([Outcome("A", "SA", 40, 30), Outcome("B", "SA", 40, 41)]).Team);
        Assert.Equal(
            "The plans need more hours than the team's contracts hold: 2 people are rostered 8.00h above contract this fortnight",
            Contracts.ContractFigures([Outcome("A", "SA", 40, 39), Outcome("B", "SA", 40, 46), Outcome("C", "ASM", 80, 82)]).Team);
    }

    [Fact]
    public void Gives_the_hours_the_roster_holds_when_a_held_flag_still_gives_them_from_before_a_swap()
    {
        var f = Contracts.ContractFigures([Outcome("Chloe Park", "DSM", 70, 63.5), Outcome("Zara Ahmed", "RSM", 60, 55.25)]);
        Assert.Equal([("Chloe Park", 6.5), ("Zara Ahmed", 4.75)], f.People.Select(p => (p.Name, p.Hours)));
    }
}
