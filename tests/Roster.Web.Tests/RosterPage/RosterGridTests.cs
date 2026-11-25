// Port of src/components/roster/RosterGrid.test.ts, plus checks of the grid's other parts.

using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Web.Features.RosterPage.Grid;
using Roster.Web.Tests.Infrastructure;
using CoreGrid = Roster.Engine.Core.Grid;

namespace Roster.Web.Tests.RosterPage;

public class RosterGridTests : UiTestContext
{
    private static readonly RosterGridRow Shift = new()
    {
        Key = "s1",
        Badge = "SA",
        Start = 6 * 60,
        End = 14 * 60,
        BreakMinutes = 30,
        UnpaidBreakMinutes = 30,
        Blocks = [new DisplayBlock(6 * 60, "load_running")],
    };

    private static readonly IReadOnlyList<LoadRunning.Period> Lr = [new(6 * 60, 10 * 60)];

    private string Render(Action<ComponentParameterCollectionBuilder<RosterGrid>>? props = null) =>
        Render<RosterGrid>(p =>
        {
            p.Add(x => x.Rows, [Shift]);
            props?.Invoke(p);
        }).Markup;

    // ── RosterGrid load running line ─────────────────────────────────────────

    [Fact]
    public void LoadRunningLine_IsDashed_AtTheStartAndFinishOfEachPeriod()
    {
        var html = Render(p => p.Add(x => x.LoadRunningPeriods, Lr));
        var dashed = new Regex("<t[hd][^>]*data-c=\"(\\d+)\"[^>]*style=\"[^\"]*repeating-linear-gradient\\(to bottom");
        var cols = dashed.Matches(html).Select(m => double.Parse(m.Groups[1].Value)).Distinct().Order().ToList();
        Assert.Equal([6 * 60, 10 * 60], cols);
        Assert.DoesNotMatch(new Regex("border-left:\\s*2px solid"), html);
    }

    [Fact]
    public void LoadRunningLine_ClosesAPeriodThatRunsToTheEndOfTheGrid_OnTheLastColumnsRightEdge()
    {
        var last = CoreGrid.DisplayBlocks()[^1];
        var html = Render(p => p.Add(x => x.LoadRunningPeriods, [new LoadRunning.Period(20 * 60, last + 15)]));
        Assert.Matches(new Regex($"data-c=\"{last}\"[^>]*style=\"[^\"]*background-position:\\s*right top"), html);
    }

    // ── RosterGrid focus cell (edit mode) ────────────────────────────────────

    [Fact]
    public void FocusCell_WashesTheHoveredRowAndColumnPaleYellowWhileEditing()
    {
        var html = Render(p => p.Add(x => x.OnCellClick, (GridCell _) => { }));
        Assert.Contains("rg-xhair", html);
        // Written raw - a <style> body is not HTML-escaped, so the selectors arrive intact.
        Assert.Contains(".rg-xhair tbody tr[data-row]:hover > td:not([data-c]:hover)", html);
        // One rule per time column, keyed to the cells that carry it.
        foreach (var m in CoreGrid.DisplayBlocks()) Assert.Contains($".rg-xhair:has([data-c=\"{m}\"]:hover)", html);
        Assert.Contains("background-color:var(--brand-yellow-wash)", html);
        // The cell under the mouse is outlined where the washes cross.
        Assert.Contains(".rg-xhair tbody td[data-c]:hover{box-shadow:inset 0 0 0 2px var(--brand-navy)}", html);
        Assert.Matches(new Regex("<tr data-row"), html);
    }

    [Fact]
    public void FocusCell_AddsNothingOutsideEditMode()
    {
        var html = Render();
        Assert.DoesNotContain("rg-xhair", html);
        Assert.DoesNotContain("<style>", html);
    }

    // ── The rest of the grid ─────────────────────────────────────────────────

    [Fact]
    public void Row_ShowsTimesPaidHoursAndBreak_AsTheTypeScriptWritesThem()
    {
        var cut = Render<RosterGrid>(p => p.Add(x => x.Rows, [Shift]).Add(x => x.BreakAsHours, true));
        var cells = cut.FindAll("tbody tr[data-row] td").Select(td => td.TextContent.Trim()).Take(5).ToList();
        Assert.Equal(["SA", "06:00", "14:00", "7.50", "0.50"], cells);
        var minutes = Render<RosterGrid>(p => p.Add(x => x.Rows, [Shift]));
        Assert.Equal("30m", minutes.FindAll("tbody tr[data-row] td")[4].TextContent.Trim());
    }

    [Fact]
    public void Tile_OutsideTheLoadRunningPeriods_IsAnErrorTileNamingThePeriods()
    {
        var row = Shift with { Blocks = [new DisplayBlock(11 * 60, "load_running"), new DisplayBlock(7 * 60, "load_running")] };
        var cut = Render<RosterGrid>(p => p.Add(x => x.Rows, [row]).Add(x => x.LoadRunningPeriods, Lr));
        var wrong = cut.Find("td[data-c=\"660\"] div");
        Assert.Equal("Load Running outside the store's load running period (06:00–10:00)", wrong.GetAttribute("title"));
        Assert.Contains("border:1.5px solid var(--brand-red)", wrong.GetAttribute("style"));
        var right = cut.Find("td[data-c=\"420\"] div");
        Assert.Equal("Load Running", right.GetAttribute("title"));
        Assert.Equal("LR", right.TextContent);
    }

    [Fact]
    public void Subtasks_NameTheSubtaskOnHover_AndShowItsCodeForAnOpenGroup()
    {
        var row = Shift with { Blocks = [new DisplayBlock(6 * 60, "reset", "re_runs"), new DisplayBlock(7 * 60, "load_running", "produce")] };
        var cut = Render<RosterGrid>(p => p.Add(x => x.Rows, [row]).Add(x => x.Subtasks, new GridSubtasks(new HashSet<string> { "reset" })));
        var reset = cut.Find("td[data-c=\"360\"] div");
        Assert.Equal(("Reset · Re-runs", "RE"), (reset.GetAttribute("title"), reset.TextContent));
        var load = cut.Find("td[data-c=\"420\"] div");
        Assert.Equal(("Load Running · Produce", "LR"), (load.GetAttribute("title"), load.TextContent));
        // Without the sub-task view, the tile is the group's alone.
        var plain = Render<RosterGrid>(p => p.Add(x => x.Rows, [row]));
        Assert.Equal("Reset", plain.Find("td[data-c=\"360\"] div").GetAttribute("title"));
    }

    [Fact]
    public void Cost_AddsAColumnAndADayTotal_AndFlagsARowWithNoPayRate()
    {
        var other = Shift with { Key = "s2", Name = "Sam" };
        var cut = Render<RosterGrid>(p => p
            .Add(x => x.Rows, [Shift with { Name = "Alex" }, other])
            .Add(x => x.ShowName, true)
            .Add(x => x.CostByKey, new Dictionary<string, double> { ["s1"] = 1234.5 }));
        var rows = cut.FindAll("tbody tr[data-row]");
        Assert.Equal("$1,234.50", rows[0].Children[6].TextContent.Trim());
        Assert.Equal("—", rows[1].Children[6].TextContent.Trim());
        Assert.Contains("No pay rate matched", rows[1].Children[6].InnerHtml);
        var total = cut.FindAll("tbody tr").Last();
        Assert.Equal(["Day total", "$1,234.50", ""], total.Children.Select(c => c.TextContent.Trim()));
        Assert.Equal("6", total.Children[0].GetAttribute("colspan"));
    }

    [Fact]
    public void FrontEndRow_CountsTheNeed_AndMarksAColumnWhoseCheckoutTilesDoNotMatch()
    {
        var row = Shift with { Blocks = [new DisplayBlock(6 * 60, "checkout"), new DisplayBlock(6 * 60 + 15, "registers")] };
        var cut = Render<RosterGrid>(p => p
            .Add(x => x.Rows, [row])
            .Add(x => x.TillsByTime, new Dictionary<double, double> { [360] = 1, [375] = 3, [390] = 1 }));
        var fe = cut.FindAll("tbody tr")[0];
        Assert.StartsWith("Front End Team Members", fe.TextContent.Trim());
        Assert.Empty(fe.QuerySelectorAll("td[data-c=\"360\"] span")); // 1 needed, 1 on checkout
        var out1 = fe.QuerySelector("td[data-c=\"375\"] span")!; // 3 needed, 1 on checkout
        Assert.Equal("1 on checkout vs 3 needed on the front end", out1.GetAttribute("title"));
        Assert.Contains("var(--brand-red)", out1.GetAttribute("style"));
        Assert.Contains("var(--brand-yellow)", fe.QuerySelector("td[data-c=\"390\"] span")!.GetAttribute("style"));
        Assert.Equal("", fe.QuerySelector("td[data-c=\"405\"]")!.TextContent.Trim());
    }

    [Fact]
    public void Empty_SaysSoAcrossTheWholeGrid()
    {
        var cut = Render<RosterGrid>(p => p.Add(x => x.Rows, []).Add(x => x.EmptyLabel, "No shifts generated."));
        var td = cut.Find("tbody td");
        Assert.Equal("No shifts generated.", td.TextContent);
        Assert.Equal((5 + CoreGrid.DisplayBlocks().Count).ToString(), td.GetAttribute("colspan"));
    }

    [Fact]
    public void EditMode_ClicksACellInsideTheSpan_AndOutsideItOnlyWhenExtendable()
    {
        var clicks = new List<GridCell>();
        var cut = Render<RosterGrid>(p => p.Add(x => x.Rows, [Shift]).Add(x => x.OnCellClick, (GridCell c) => clicks.Add(c)));
        cut.Find("td[data-c=\"360\"]").Click();
        Assert.Equal([new GridCell("s1", 360)], clicks);
        Assert.Throws<MissingEventHandlerException>(() => cut.Find("td[data-c=\"300\"]").Click());
        var ext = Render<RosterGrid>(p => p.Add(x => x.Rows, [Shift]).Add(x => x.Extendable, true).Add(x => x.OnCellClick, (GridCell c) => clicks.Add(c)));
        var outside = ext.Find("td[data-c=\"300\"]");
        Assert.Equal("Outside this shift - painting here extends it", outside.GetAttribute("title"));
        outside.Click();
        Assert.Equal(new GridCell("s1", 300), clicks[^1]);
    }

    [Fact]
    public void HeaderColumns_RunFrom5To2145_HoursBoldAndQuartersByMinute()
    {
        var cut = Render<RosterGrid>(p => p.Add(x => x.Rows, [Shift]));
        var heads = cut.FindAll("thead th[data-c]");
        Assert.Equal(68, heads.Count);
        Assert.Equal(["5", "15", "30", "45", "6"], heads.Take(5).Select(h => h.TextContent));
        Assert.Contains("bg-[var(--brand-red-tint)]", heads[0].ClassName); // before 7am: premium time
        Assert.Contains("bg-white", heads[8].ClassName); // 7:00
    }
}
