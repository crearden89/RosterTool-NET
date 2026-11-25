// New: the "Show sub-tasks" switch (src/app/(app)/plan/roster/SubtaskView.tsx), which the
// Roster page and the Plan page share.

using Bunit;
using Roster.Web.Features.RosterPage;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.RosterPage;

public class SubtaskViewTests : UiTestContext
{
    private const string Print = "/api/print/ukg-roster?store=st1&start=2026-11-23";

    [Fact]
    public void Switch_IsOffWhenThePageOpens_AndTheLinkPrintsWithoutSubtasks()
    {
        var cut = Render<SubtaskViewProvider>(p => p
            .AddChildContent<ShowSubtasksToggle>()
            .AddChildContent<PrintRosterLink>(l => l.Add(x => x.Href, Print)));
        Assert.Equal("false", cut.Find("button[role=switch]").GetAttribute("aria-checked"));
        Assert.Equal(Print, cut.Find("a").GetAttribute("href"));
        Assert.Equal("_blank", cut.Find("a").GetAttribute("target"));
    }

    [Fact]
    public void Switch_On_TurnsGoldOnNavy_AndThePrintLinkCarriesTheChoice()
    {
        var cut = Render<SubtaskViewProvider>(p => p
            .AddChildContent<ShowSubtasksToggle>()
            .AddChildContent<PrintRosterLink>(l => l.Add(x => x.Href, Print)));
        cut.Find("button[role=switch]").Click();
        Assert.Equal("true", cut.Find("button[role=switch]").GetAttribute("aria-checked"));
        Assert.Contains("bg-[var(--ui-gold)]", cut.Find("button[role=switch] span").ClassName);
        Assert.Equal(Print + "&subtasks=1", cut.Find("a").GetAttribute("href"));
        Assert.True(cut.Instance.On);
        cut.Find("button[role=switch]").Click();
        Assert.False(cut.Instance.On);
    }

    [Fact]
    public void Switch_Light_UsesTheLightControlsColours()
    {
        var cut = Render<SubtaskViewProvider>(p => p.AddChildContent<ShowSubtasksToggle>(t => t.Add(x => x.Tone, SubtaskToggleTone.Light).Add(x => x.Hint, "Plan hint")));
        var button = cut.Find("button[role=switch]");
        Assert.Contains("bg-[var(--ui-lavender)]", button.ClassName);
        Assert.Equal("Plan hint", button.GetAttribute("title"));
        button.Click();
        Assert.Contains("bg-[var(--ui-ink)]", cut.Find("button[role=switch] span").ClassName);
    }

    [Fact]
    public void Legend_ListsEachGroupsCodes()
    {
        var text = Render<SubtaskLegend>().Find("div").TextContent;
        Assert.StartsWith("Sub-tasks:", text);
        Assert.Contains("Reset RE Re-runs, DC Decarding", text);
        Assert.Contains("Load Running PR Produce, LL Long Life, FZ Freezer, AM Ambient, BD Bread, SL Short Life, FT Freeze to Thaw", text);
        Assert.Contains("Cleaning FE Front end, SF Shop floor, BH BOH", text);
        Assert.Contains("Specials BU Building, TD Tidy, RN Running", text);
        Assert.Equal(4, ShowSubtasks.AllSubtaskGroups.Count);
    }
}
