// New: the Team Members editor's contract help text (EmployeeEditor in StaffClient.tsx).
// The owner corrected "contract ÷ 5" to "contract ÷ 10", the figure the editor has always
// shown beside it (a fortnight's contract over ten working days).

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Roster.Web.Features.Team;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Team;

public sealed class EmployeeEditorTests : UiTestContext
{
    public EmployeeEditorTests()
    {
        // Made by the container, so it is disposed with the test context.
        Services.AddSingleton(_ => new TeamTestDb());
        Services.AddSingleton(sp => sp.GetRequiredService<TeamTestDb>().Team);
    }

    private IRenderedComponent<EmployeeEditor> Editor(double contract, double? notional = null) =>
        Render<EmployeeEditor>(p => p
            .Add(x => x.Employee, new TeamMember
            {
                Id = Guid.NewGuid(), Name = "4-Nye, Harper", Role = "SA", PositionCode = "SA-FF",
                ContractHours = contract, NotionalHours = notional,
            })
            .Add(x => x.Positions, []));

    [Fact]
    public void Contract_help_says_the_default_notional_is_contract_over_ten()
    {
        var help = Editor(contract: 60).Find("p.mt-1\\.5").TextContent;
        Assert.Contains("Leave empty for the default of contract ÷ 10 (6.0h).", help);
        Assert.DoesNotContain("÷ 5", help);
    }

    [Fact]
    public void Default_notional_follows_the_contract_box()
    {
        var cut = Editor(contract: 76, notional: 8);
        Assert.Contains("contract ÷ 10 (7.6h)", cut.Find("p.mt-1\\.5").TextContent);
        Assert.Equal("7.6", cut.Find("input[step='0.25']").GetAttribute("placeholder"));
        cut.Find("input[type=number]").Input("45");
        Assert.Contains("contract ÷ 10 (4.5h)", cut.Find("p.mt-1\\.5").TextContent);
    }
}
