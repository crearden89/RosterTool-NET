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
