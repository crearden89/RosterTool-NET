// Port of the look and behaviour of src/components/ui/primitives.tsx, checked on the rendered markup.

using Bunit;
using Roster.Web.Components.Ui;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Ui;

public class PrimitivesTests : UiTestContext
{
    [Fact]
    public void Card_is_padded_unless_told_otherwise()
    {
        var padded = Render<Card>(p => p.AddChildContent("x"));
        Assert.Equal("rounded-2xl border border-[var(--ui-line)] bg-white p-5 sm:p-6", padded.Find("div").ClassName);

        var flush = Render<Card>(p => p.Add(c => c.Padded, false).Add(c => c.Class, "mt-4").AddChildContent("x"));
        Assert.Equal("rounded-2xl border border-[var(--ui-line)] bg-white mt-4", flush.Find("div").ClassName);
    }

    [Fact]
    public void PageHeader_has_the_title_its_facts_and_its_actions()
    {
        var cut = Render<PageHeader>(p => p
            .Add(h => h.Title, "Team Members")
            .Add(h => h.Subtitle, "19 team members · contracts, availability and leave")
            .Add(h => h.Actions, "<button>Add</button>"));

        Assert.Equal("Team Members", cut.Find("h1").TextContent);
        Assert.Contains("text-[28px] font-bold", cut.Find("h1").ClassName);
        Assert.Equal("19 team members · contracts, availability and leave", cut.Find("p").TextContent);
        Assert.Equal("Add", cut.Find("button").TextContent);
    }

    [Theory]
    [InlineData(PillTone.Navy, "bg-[var(--ui-ink)] text-white")]
    [InlineData(PillTone.Soft, "bg-[var(--ui-lavender)] text-[var(--ui-ink)]")]
    [InlineData(PillTone.Amber, "bg-[var(--ui-amber-bg)] text-[var(--ui-amber-ink)]")]
    [InlineData(PillTone.Red, "bg-[var(--brand-red-tint)] text-[var(--ui-bad)]")]
    public void Pill_tones(PillTone tone, string classes)
    {
        var cut = Render<Pill>(p => p.Add(x => x.Tone, tone).AddChildContent("Leave"));
        Assert.EndsWith(classes, cut.Find("span").ClassName);
        Assert.Equal("Leave", cut.Find("span").TextContent);
    }

    [Theory]
    [InlineData("Hannah Lee", "HL")]
    [InlineData("4-Nye, Harper", "HN")]
    [InlineData("5L-Smith, Jo Ann", "JS")]
    [InlineData("Cher", "C")]
    [InlineData("", "?")]
    public void Avatar_initials_skip_the_rank_and_read_first_name_first(string name, string initials) =>
        Assert.Equal(initials, AvatarStyle.InitialsOf(name));

    [Fact]
    public void Avatar_colour_is_a_stable_hash_of_the_seed()
    {
        Assert.Equal(AvatarStyle.ColourFor("emp-1"), AvatarStyle.ColourFor("emp-1"));
        // h = h * 31 + c over "a" is 97; 97 % 9 = 7: the eighth colour.
        Assert.Equal("#be123c", AvatarStyle.ColourFor("a"));
        var cut = Render<Avatar>(p => p.Add(a => a.Name, "Hannah Lee").Add(a => a.Seed, "a"));
        var span = cut.Find("span");
        Assert.Equal("true", span.GetAttribute("aria-hidden"));
        Assert.Contains("background: #be123c", span.GetAttribute("style"));
        Assert.Contains("font-size: 13px", span.GetAttribute("style"));
        Assert.Equal("HL", span.TextContent);
    }

    [Fact]
    public void Switch_flips_and_says_its_state()
    {
        bool? got = null;
        var cut = Render<Switch>(p => p.Add(s => s.Label, "Leave booked").Add(s => s.Checked, true).Add(s => s.OnChange, v => got = v));
        var button = cut.Find("button");
        Assert.Equal("switch", button.GetAttribute("role"));
        Assert.Equal("true", button.GetAttribute("aria-checked"));
        Assert.False(button.HasAttribute("disabled"));
        button.Click();
        Assert.False(got);
    }

    [Fact]
    public void Switch_without_a_handler_only_shows_the_state()
    {
        var cut = Render<Switch>(p => p.Add(s => s.Label, "Store closed").Add(s => s.ShowLabel, false));
        var button = cut.Find("button");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("false", button.GetAttribute("aria-checked"));
        Assert.Equal("Store closed", button.GetAttribute("aria-label"));
    }

    [Fact]
    public void ButtonClass_matches_the_typescript_strings()
    {
        Assert.Equal(
            "inline-flex items-center justify-center gap-1.5 rounded-xl font-semibold whitespace-nowrap transition-colors disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[color:var(--ui-violet)] px-3 py-1.5 text-xs bg-[var(--ui-ink)] text-white hover:bg-[#14147a]",
            Ui.ButtonClass(ButtonKind.Primary, ButtonSize.Sm));
        Assert.EndsWith("px-4 py-2 text-sm bg-[var(--brand-orange)] text-white hover:brightness-95", Ui.ButtonClass(ButtonKind.Accent));
    }

    [Fact]
    public void StatCard_puts_the_label_over_the_figure()
    {
        var cut = Render<StatCard>(p => p.Add(s => s.Label, "Leave").Add(s => s.Value, "3 people").Add(s => s.Sub, "this fortnight"));
        Assert.Equal("Leave", cut.Find("p").TextContent);
        Assert.Contains("uppercase", cut.Find("p").ClassName);
        Assert.Equal("3 people", cut.Find("div.text-2xl").TextContent);
    }
}
