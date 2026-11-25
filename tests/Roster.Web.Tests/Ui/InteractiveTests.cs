// Port of the behaviour of src/components/ui/DayChips.tsx, SideSheet.tsx, SaveBar.tsx,
// Bars.tsx, DayBarsChart.tsx and NameCombobox.tsx.

using Bunit;
using Microsoft.AspNetCore.Components;
using Roster.Web.Components.Ui;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Ui;

public class InteractiveTests : UiTestContext
{
    [Fact]
    public void DayChips_lays_a_fortnight_out_week_by_week()
    {
        var start = new DateOnly(2026, 11, 23); // a Monday
        var days = Enumerable.Range(0, 14).Select(i => new DayChip
        {
            Date = start.AddDays(i),
            Href = $"/plan/generate?date={start.AddDays(i):yyyy-MM-dd}",
            Tone = i == 2 ? DayTone.Error : DayTone.Ok,
            Note = "+0.09",
            Status = i == 2 ? "Short of cover" : null,
        }).ToList();
        var cut = Render<DayChips>(p => p.Add(c => c.Days, days).Add(c => c.Selected, start.AddDays(1)));

        var weeks = cut.FindAll("nav > div");
        Assert.Equal(2, weeks.Count);
        Assert.Equal("Week 1 · 23–29 Nov", weeks[0].QuerySelector("p")!.TextContent);
        Assert.Equal("Week 2 · 30 Nov – 6 Dec", weeks[1].QuerySelector("p")!.TextContent);
        var links = cut.FindAll("a");
        Assert.Equal(14, links.Count);
        Assert.Equal("date", links[1].GetAttribute("aria-current"));
        Assert.Null(links[0].GetAttribute("aria-current"));
        Assert.Equal("Wed 25 Nov: Short of cover", links[2].GetAttribute("aria-label"));
        // App-relative, so the app also runs under a virtual directory.
        Assert.Equal("plan/generate?date=2026-11-24", links[1].GetAttribute("href"));
        Assert.Contains("grid-column-start: 1", cut.Find("li").GetAttribute("style"));
    }

    [Fact]
    public void DayChips_starts_a_short_first_week_on_its_weekday()
    {
        var wed = new DateOnly(2026, 11, 25);
        var cut = Render<DayChips>(p => p.Add(c => c.Days, [new DayChip { Date = wed, Href = "x" }]));
        Assert.Contains("grid-column-start: 3", cut.Find("li").GetAttribute("style"));
    }

    [Fact]
    public void SideSheet_is_a_labelled_modal_dialog_that_closes_on_the_dimmed_page()
    {
        var closed = 0;
        var cut = Render<SideSheet>(p => p
            .Add(s => s.TitleId, "sheet-title")
            .Add(s => s.Eyebrow, "Fixed roster")
            .Add(s => s.Title, "Harper Nye")
            .Add(s => s.OnClose, () => closed++)
            .AddChildContent("<p>Body</p>"));

        var dialog = cut.Find("aside");
        Assert.Equal("dialog", dialog.GetAttribute("role"));
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("sheet-title", dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("Harper Nye", cut.Find("#sheet-title").TextContent);
        cut.Find("div[aria-hidden=true]").Click();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void SaveScope_counts_the_forms_changes_and_saves_each_changed_form()
    {
        var saved = new List<string>();
        RenderFragment forms = b =>
        {
            b.OpenComponent<SaveSlot>(0);
            b.AddAttribute(1, nameof(SaveSlot.Id), "hours");
            b.AddAttribute(2, nameof(SaveSlot.Changes), 2);
            b.AddAttribute(3, nameof(SaveSlot.Save), (Func<Task<bool>>)(() => { saved.Add("hours"); return Task.FromResult(true); }));
            b.CloseComponent();
            b.OpenComponent<SaveSlot>(4);
            b.AddAttribute(5, nameof(SaveSlot.Id), "breaks");
            b.AddAttribute(6, nameof(SaveSlot.Changes), 0);
            b.AddAttribute(7, nameof(SaveSlot.Save), (Func<Task<bool>>)(() => { saved.Add("breaks"); return Task.FromResult(true); }));
            b.CloseComponent();
        };
        var cut = Render<SaveScope>(p => p.AddChildContent(forms));

        cut.WaitForAssertion(() => Assert.Equal("2 unsaved changes", cut.Find("[role=region] b").TextContent));
        Assert.Equal("Save changes", cut.FindAll("[role=region] button")[1].TextContent);
        cut.FindAll("[role=region] button")[1].Click();
        cut.WaitForAssertion(() => Assert.Equal(["hours"], saved));
    }

    [Fact]
    public void SplitBar_writes_a_share_only_where_it_fits()
    {
        var cut = Render<SplitBar>(p => p.Add(b => b.Label, "Hours by group").Add(b => b.Parts,
        [
            new SplitPart("a", "Registers", 0.7, "#00005f", SplitInk.White, "Registers 70%"),
            new SplitPart("b", "Fresh", 0.1, "#e8e8f4", SplitInk.Ink, "Fresh 10%"),
            new SplitPart("c", "None", 0, "#fff", SplitInk.Ink, "None"),
        ]));
        var parts = cut.FindAll("div[role=img] > span");
        Assert.Equal(2, parts.Count);
        Assert.Equal("70%", parts[0].TextContent.Trim());
        Assert.Equal("", parts[1].TextContent.Trim());
        Assert.Contains("width: calc((100% - 2px) * 0.7)", parts[0].GetAttribute("style"));
    }

    [Fact]
    public void MeterBar_and_CentredBar_carry_their_figure_in_words()
    {
        var meter = Render<MeterBar>(p => p.Add(b => b.Value, 1.5).Add(b => b.Label, "Hours used: 120 of 100"));
        Assert.Equal("Hours used: 120 of 100", meter.Find("[role=img]").GetAttribute("aria-label"));
        Assert.Contains("width: 100%", meter.Find("span span").GetAttribute("style"));

        var centred = Render<CentredBar>(p => p.Add(b => b.Value, 0.5).Add(b => b.Side, BarSide.Below).Add(b => b.Label, "4 h under"));
        Assert.Contains("right: 50%; width: 25%", centred.FindAll("span span")[1].GetAttribute("style"));
    }

    [Fact]
    public void DayBarsChart_is_one_tab_stop_on_today()
    {
        var days = Enumerable.Range(1, 5).Select(d => new DayBarDatum
        {
            Key = $"d{d}", Day = d, Actual = 0.5, Forecast = 0.6, Today = d == 3, Label = $"Day {d}", Tip = [$"Day {d}", "Sales $1,000"],
        }).ToList();
        var cut = Render<DayBarsChart>(p => p.Add(c => c.Days, days).Add(c => c.Label, "Sales this month"));
        var bars = cut.FindAll("[role=img]");
        Assert.Equal(["-1", "-1", "0", "-1", "-1"], bars.Select(b => b.GetAttribute("tabindex")).ToList());
        bars[2].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() => Assert.Equal("0", cut.FindAll("[role=img]")[3].GetAttribute("tabindex")));
    }

    [Fact]
    public void NameCombobox_narrows_the_list_as_you_type_and_picks()
    {
        var picked = "";
        var cut = Render<NameCombobox>(p => p
            .Add(c => c.Options, [new ComboOption("1", "Hannah Lee", "SA"), new ComboOption("2", "Harper Nye"), new ComboOption("3", "Jo Smith", Disabled: true)])
            .Add(c => c.Value, "2")
            .Add(c => c.OnChange, v => picked = v));

        var input = cut.Find("input");
        Assert.Equal("combobox", input.GetAttribute("role"));
        Assert.Equal("Harper Nye", input.GetAttribute("value"));
        input.Input("han");
        var options = cut.FindAll("[role=option]");
        Assert.Single(options);
        Assert.Contains("Hannah Lee", options[0].TextContent);
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.Equal("1", picked);
        Assert.Empty(cut.FindAll("[role=listbox]"));
    }
}
