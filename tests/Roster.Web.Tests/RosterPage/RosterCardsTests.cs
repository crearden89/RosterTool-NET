// New: the Roster page's floor bar (FloorBar.tsx) and contracts card (ContractsCard.tsx).

using Bunit;
using Roster.Web.Features.RosterPage;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.RosterPage;

public class RosterCardsTests : UiTestContext
{
    private IRenderedComponent<SubtaskViewProvider> Floor(bool published) =>
        Render<SubtaskViewProvider>(p => p.AddChildContent<FloorBar>(f => f
            .Add(x => x.Published, published)
            .Add(x => x.Title, "Not on the floor yet")
            .Add(x => x.Line, "Approve the roster to put it on the Floor Display and the printed roster.")
            .Add(x => x.PrintHref, "/api/print/ukg-roster?store=s&start=2026-11-23")
            .Add(x => x.ExportHref, "/api/export/ukg?store=s&start=2026-11-23")));

    [Fact]
    public void FloorBar_PrintWaitsForTheRosterToBeApproved()
    {
        var cut = Floor(published: false);
        Assert.Equal("On the floor", cut.Find("section").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("a[target=_blank]"));
        Assert.Equal("Approve the roster for this fortnight first", cut.Find("span[aria-disabled]").GetAttribute("title"));
        Assert.Equal("/api/export/ukg?store=s&start=2026-11-23", cut.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void FloorBar_Published_PrintsTheRoster_WithTheSubtasksChoice()
    {
        var cut = Floor(published: true);
        Assert.Equal("/api/print/ukg-roster?store=s&start=2026-11-23", cut.Find("a[target=_blank]").GetAttribute("href"));
        cut.Find("button[role=switch]").Click();
        Assert.EndsWith("&subtasks=1", cut.Find("a[target=_blank]").GetAttribute("href"));
    }

    private static RosterContracts Figures(int people) => new()
    {
        People = Enumerable.Range(0, people)
            .Select(i => new RosterContractGap(Guid.NewGuid(), $"Person {i}", "SA", i % 2 == 0, 10 - i * 0.5)).ToList(),
        Under = people / 2,
        Over = people - people / 2,
        UnderHours = 3.5,
        OverHours = 1.25,
    };

    [Fact]
    public void ContractsCard_ShowsBarsLargestFirst_AndFoldsAwayPastNine()
    {
        var cut = Render<ContractsCard>(p => p.Add(x => x.Figures, Figures(11)).Add(x => x.Flags, ["Run of 6 days"]));
        Assert.Contains("3.50 h short", cut.Markup);
        Assert.Contains("1.25 h over", cut.Markup);
        Assert.Equal(9, cut.FindAll("ul.space-y-2\\.5 > li").Count - 2);
        Assert.Equal("and 2 more", cut.Find("summary").TextContent);
        var first = cut.FindAll("li")[0];
        Assert.Equal("above contract by +10.00 h", first.Children[2].TextContent.Trim());
        Assert.Contains("width:100%;background:var(--ui-bad)", first.InnerHtml);
        Assert.Equal("Run of 6 days", cut.Find("ul.text-xs li").TextContent);
    }

    [Fact]
    public void ContractsCard_WithoutFigures_ListsTheFlagsAsTheyAre()
    {
        var none = Render<ContractsCard>(p => p.Add(x => x.Figures, null).Add(x => x.Flags, []));
        Assert.Contains("The figures show once the roster is next built", none.Markup);
        Assert.Contains("No fortnight checks.", none.Markup);
        var even = Render<ContractsCard>(p => p.Add(x => x.Figures, Figures(0)).Add(x => x.Flags, []));
        Assert.Contains("Everyone is within half an hour of their contract.", even.Markup);
    }
}
