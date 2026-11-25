// Port of the behaviour of src/components/ui/FilterChips.tsx, SegmentedControl.tsx,
// PageChips.tsx, CheckList.tsx and StepTrack.tsx.

using Bunit;
using Roster.Web.Components.Ui;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Ui;

public class ChipsTests : UiTestContext
{
    [Fact]
    public void FilterChips_one_on_at_a_time_with_counts()
    {
        string? picked = null;
        var cut = Render<FilterChips<string>>(p => p
            .Add(c => c.Label, "Show")
            .Add(c => c.Options, [new FilterChip<string>("all", "All", 23), new FilterChip<string>("errors", "Errors", 2)])
            .Add(c => c.Value, "all")
            .Add(c => c.OnChange, v => picked = v));

        Assert.Equal("Show", cut.Find("[role=group]").GetAttribute("aria-label"));
        var buttons = cut.FindAll("button");
        Assert.Equal("true", buttons[0].GetAttribute("aria-pressed"));
        Assert.Equal("false", buttons[1].GetAttribute("aria-pressed"));
        Assert.Equal("Errors2", buttons[1].TextContent.Replace(" ", "").Trim());
        buttons[1].Click();
        Assert.Equal("errors", picked);
    }

    [Fact]
    public void SegmentedControl_is_radio_buttons_in_a_named_group()
    {
        var cut = Render<SegmentedControl<string>>(p => p
            .Add(c => c.Label, "Cost basis")
            .Add(c => c.Name, "basis")
            .Add(c => c.DefaultValue, "ideal")
            .Add(c => c.Options, [new SegmentOption<string>("actual", "Actual"), new SegmentOption<string>("ideal", "Ideal")]));

        Assert.Equal("radiogroup", cut.Find("div").GetAttribute("role"));
        var radios = cut.FindAll("input[type=radio]");
        Assert.All(radios, r => Assert.Equal("basis", r.GetAttribute("name")));
        Assert.False(radios[0].HasAttribute("checked"));
        Assert.True(radios[1].HasAttribute("checked"));
    }

    [Fact]
    public void SegmentedControl_controlled_reports_the_choice()
    {
        var chosen = "";
        var cut = Render<SegmentedControl<string>>(p => p
            .Add(c => c.Label, "View")
            .Add(c => c.Value, "plan")
            .Add(c => c.OnChange, v => chosen = v)
            .Add(c => c.Options, [new SegmentOption<string>("plan", "Plan"), new SegmentOption<string>("roster", "Roster")]));
        cut.FindAll("input")[1].Change(true);
        Assert.Equal("roster", chosen);
        // Without a name the radios still share one, so they are one group.
        var names = cut.FindAll("input").Select(i => i.GetAttribute("name")).Distinct().ToList();
        Assert.Single(names);
    }

    [Fact]
    public void StatusChip_shows_its_icon()
    {
        var cut = Render<StatusChip>(p => p.Add(c => c.Tone, StatusTone.Navy).Add(c => c.Icon, StatusIcon.Lock).AddChildContent("Approved · locked"));
        Assert.NotNull(cut.Find("svg.lucide-lock"));
        Assert.Contains("bg-[var(--ui-ink)] text-white", cut.Find("span").ClassName);
        Assert.Contains("Approved · locked", cut.Find("span").TextContent);
    }

    [Fact]
    public void CheckList_draws_each_check_with_its_severity()
    {
        var cut = Render<CheckList>(p => p.Add(c => c.Rows,
        [
            CheckRow.Of("a", CheckTone.Error, "Not enough cover at 9:00", "Two people short."),
            CheckRow.Of("b", CheckTone.Ok, "Breaks are in place"),
        ]));
        var items = cut.FindAll("li");
        Assert.Equal(2, items.Count);
        Assert.Contains("Error", items[0].TextContent);
        Assert.Contains("Two people short.", items[0].TextContent);
        Assert.NotNull(items[0].QuerySelector("svg.lucide-circle-alert"));
        Assert.Contains("OK", items[1].TextContent);
    }

    [Fact]
    public void StepTrack_says_each_state_in_words()
    {
        var cut = Render<StepTrack>(p => p.Add(c => c.Steps,
        [
            new Step("plan", "Plan", "Sun 15 Nov, 7:48 am", StepState.Done),
            new Step("review", "Review", "Waiting for the store", StepState.Current),
            new Step("roster", "Roster", "After approval", StepState.Todo),
        ]));
        var srOnly = cut.FindAll(".sr-only").Select(e => e.TextContent).ToList();
        Assert.Equal([", done", ", next", ", still to come"], srOnly);
        Assert.Equal("3", cut.FindAll("li")[2].QuerySelector("span")!.TextContent.Trim());
    }
}
