// New: the Roster page (page.tsx, RosterEditor.tsx) on the invented fortnight: what it
// shows, and that its edits reach the Plan area's roster actions.

using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Roster.Web.Features.Plan;
using Roster.Web.Features.RosterPage;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.RosterPage;

/// <summary>An IPlanService that records every call and answers each action with success.</summary>
public class RecordingPlan : DispatchProxy
{
    public List<(string Method, object?[] Args)> Calls { get; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        Calls.Add((method!.Name, args ?? []));
        if (method.ReturnType == typeof(Task<PlanResult>)) return Task.FromResult(PlanResult.Done);
        if (method.ReturnType == typeof(Task<DateTimeOffset?>)) return Task.FromResult<DateTimeOffset?>(null);
        throw new InvalidOperationException($"{method.Name} not expected");
    }
}

public class PlanRosterPageTests : UiTestContext
{
    private readonly RecordingPlan _plan;

    public PlanRosterPageTests()
    {
        var plan = DispatchProxy.Create<IPlanService, RecordingPlan>();
        _plan = (RecordingPlan)(object)plan;
        Services.AddSingleton(plan);
        Services.AddSingleton<IRosterService, FakeRosterService>();
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void Shows_the_floor_bar_the_fortnight_the_day_and_its_editor()
    {
        var cut = Render<PlanRosterPage>();
        Assert.Contains("Not on the floor yet", cut.Markup);
        Assert.Contains("Approve Roster", cut.Markup);
        Assert.Equal(14, cut.FindAll("a[aria-current], nav a, ol a").Count(a => a.GetAttribute("href")?.Contains("plan/roster?store=") == true));
        Assert.Equal(9, cut.FindAll("tbody tr[data-row]").Count);
        Assert.Contains("Add a shift", cut.Markup);
        Assert.Contains("Contracts this fortnight", cut.Markup);
        Assert.Contains("Checks for ", cut.Markup);
        Assert.Contains("Fortnight cost", cut.Markup);
    }

    [Fact]
    public void A_changed_time_saves_the_row_and_a_pin_toggles_the_lock()
    {
        var cut = Render<PlanRosterPage>();
        var row = cut.FindAll("tbody tr[data-row]")[0];
        var start = row.QuerySelectorAll("input[type=time]")[0];
        start.Input("05:30");
        start.Blur();
        var save = Assert.Single(_plan.Calls, c => c.Method == nameof(IPlanService.SaveShiftRowAsync));
        Assert.Equal(330d, save.Args[4]);
        Assert.Equal(save.Args[2], save.Args[3]); // the same person, new times

        cut.FindAll("tbody tr[data-row]")[0].QuerySelector("button[aria-label$='shift'][title^='Pin'], button[aria-label='Unpin shift']")!.Click();
        Assert.Contains(_plan.Calls, c => c.Method == nameof(IPlanService.SetShiftLockAsync));
    }

    [Fact]
    public void Show_subtasks_puts_the_codes_on_the_tiles_and_the_legend_under_them()
    {
        var cut = Render<PlanRosterPage>();
        Assert.DoesNotContain("Sub-tasks:", cut.Markup);
        cut.Find("button[role=switch]").Click();
        Assert.Contains("Sub-tasks:", cut.Markup);
        Assert.Contains(cut.FindAll("td[data-c] div"), d => d.TextContent is "PR" or "LL" or "FZ" or "AM" or "BD");
    }
}
