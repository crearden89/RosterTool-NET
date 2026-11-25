// New: the Roster Order actions (src/app/(app)/admin/process/roster/actions.ts), the page's
// read and its day-order box, against the real model on SQLite.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Roster.Data.Entities;
using Roster.Engine.Rostering.Painter;
using Roster.Web.Features.Process;
using Roster.Web.Features.Process.RosterPhases;
using Roster.Web.Features.Rules;
using Roster.Web.Tests.Rules;

namespace Roster.Web.Tests.Process;

public class RosterOrderTests
{
    [Fact]
    public void The_day_order_box_reads_and_writes_the_seven_days()
    {
        Assert.Equal("Sat, Sun, Tue, Fri, Mon, Wed, Thu", DayOrderText.Format(RosterOrder.DefaultFloorDayOrder));
        Assert.Equal("", DayOrderText.Format(null));
        Assert.Equal([5, 6, 1, 4, 0, 2, 3], DayOrderText.Parse(" sat,SUN , tue,fri,mon,wed,thu,"));
        Assert.Null(DayOrderText.Parse("Sat, Sun, Tue, Fri, Mon, Wed"));
        Assert.Null(DayOrderText.Parse("Sat, Sat, Tue, Fri, Mon, Wed, Thu"));
        Assert.Null(DayOrderText.Parse("Sat, Sunday, Tue, Fri, Mon, Wed, Thu"));
    }

    [Fact]
    public void CheckSteps_refuses_an_unknown_step_or_a_bad_week()
    {
        Assert.Null(RosterOrderChecks.CheckSteps(RosterOrder.DefaultOrder));
        Assert.Equal("Unknown step NAP", RosterOrderChecks.CheckSteps([new("NAP")]));
        Assert.Equal("Day order must use each of the 7 days exactly once.", RosterOrderChecks.CheckSteps([new("FILL_FLOOR", [0, 1, 2])]));
        Assert.Equal("Day order must use each of the 7 days exactly once.", RosterOrderChecks.CheckSteps([new("FILL_FLOOR", [0, 1, 2, 3, 4, 5, 5])]));
    }

    [Fact]
    public async Task Load_is_empty_until_saved_then_normalised_as_the_engine_reads_it()
    {
        using var t = new RulesTestDatabase();
        var service = new RosterOrderService(t, t.Scope, t.Clock);
        Assert.Empty(await service.LoadAsync());

        t.Seed(db =>
        {
            db.RosterOrderSteps.Add(new RosterOrderStep { Position = 1, StepType = "FILL_FLOOR", DayOrder = [0, 0, 1, 2, 3, 4, 5] });
            db.RosterOrderSteps.Add(new RosterOrderStep { Position = 0, StepType = "CONTRACT_CHECK" });
        });
        var steps = await service.LoadAsync();
        Assert.Equal(["CONTRACT_CHECK", "FILL_FLOOR"], steps.Select(s => s.Type));
        Assert.Null(steps[1].DayOrder);
    }

    [Fact]
    public async Task Save_replaces_the_order_and_reset_clears_it()
    {
        using var t = new RulesTestDatabase();
        var service = new RosterOrderService(t, t.Scope, t.Clock);

        Assert.Equal(SaveResult.Success, await service.SaveAsync([new("SHUFFLE_JSA"), new("FILL_FLOOR", [6, 5, 4, 3, 2, 1, 0])]));
        var rows = t.Read(db => db.RosterOrderSteps.OrderBy(r => r.Position).ToList());
        Assert.Equal(["SHUFFLE_JSA", "FILL_FLOOR"], rows.Select(r => r.StepType));
        Assert.Equal([6, 5, 4, 3, 2, 1, 0], rows[1].DayOrder);
        Assert.Null(rows[0].DayOrder);

        Assert.Equal(SaveResult.Fail("Unknown step NAP"), await service.SaveAsync([new("NAP")]));
        Assert.Equal(2, t.Read(db => db.RosterOrderSteps.Count()));

        Assert.True((await service.ResetAsync()).Ok);
        Assert.Equal(0, t.Read(db => db.RosterOrderSteps.Count()));
    }

    [Theory]
    [InlineData("/admin/planning-order", "/admin/process/planning")]
    [InlineData("/admin/roster-order", "/admin/process/roster")]
    public async Task An_old_process_path_redirects_with_308(string from, string to)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        new ProcessModule().Map(app);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(from);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(to, response.Headers.Location?.OriginalString);
    }
}
