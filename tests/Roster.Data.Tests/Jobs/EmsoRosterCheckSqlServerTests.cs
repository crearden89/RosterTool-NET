// New: the EMSO check against SQL Server: the period-label match in T-SQL (an en dash in
// nvarchar), real foreign keys, and the heartbeat's JSON passing ISJSON.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Jobs;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Tests.Infrastructure;

namespace Roster.Data.Tests.Jobs;

[Trait("Category", "SqlServer")]
public sealed class EmsoRosterCheckSqlServerTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private const string Label = "07/12 – 20/12";

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SqlServerFact]
    public async Task The_check_raises_then_refreshes_one_alert_and_beats()
    {
        Guid emso;
        await using (var sys = db.System())
        {
            var dg = new DirectorGroup { Name = "Example Group" };
            sys.DirectorGroups.Add(dg);
            await sys.SaveChangesAsync();
            var region = new Region { Name = "901 - Example", DirectorGroupId = dg.Id };
            sys.Regions.Add(region);
            await sys.SaveChangesAsync();
            var area = new Area { Name = "Example Area", RegionId = region.Id };
            sys.Areas.Add(area);
            await sys.SaveChangesAsync();
            var s1 = new Store { Code = "G812", Name = "Example 12", Slug = "g812", RegionId = region.Id, AreaId = area.Id, KioskToken = "k812" };
            var s2 = new Store { Code = "G809", Name = "Example 9", Slug = "g809", RegionId = region.Id, AreaId = area.Id, KioskToken = "k809" };
            sys.Stores.AddRange(s1, s2);
            var user = new AppUser { Username = "emso@example.com", Tier = Tier.Emso, AreaId = area.Id };
            sys.AppUsers.Add(user);
            await sys.SaveChangesAsync();
            emso = user.Id;
            sys.BenchmarkDays.Add(new BenchmarkDay { StoreId = s1.Id, Date = new DateOnly(2026, 12, 7), SalesForecast = 1 });
            sys.BenchmarkDays.Add(new BenchmarkDay { StoreId = s2.Id, Date = new DateOnly(2026, 12, 20), SalesForecast = 1 });
            await sys.SaveChangesAsync();
        }

        var clock = new FixedClock(Wednesday);
        var factory = db.Factory();
        var check = new EmsoRosterCheck(factory, clock);
        Assert.Equal(new EmsoCheckResult(1, 0, 0), await check.RunEmsoUnapprovedCheckAsync());
        Assert.Equal(new EmsoCheckResult(0, 1, 0), await check.RunEmsoUnapprovedCheckAsync());

        await using (var sys = db.System())
        {
            var alert = await sys.Notifications.SingleAsync(n => n.UserId == emso);
            Assert.Equal($"2 store(s) without an approved roster for {Label}: G809, G812", alert.Body);
        }

        var heartbeat = new CronHeartbeat(new DatabaseFunctionsService(factory, new StoreScope(new AccessContextAccessor(), factory), clock));
        Assert.True(await heartbeat.RecordCronRunAsync("emso-check", new EmsoCheckResult(0, 1, 0)));
        await using (var sys = db.System())
        {
            var run = await sys.CronRuns.SingleAsync(r => r.Job == "emso-check");
            Assert.Equal("""{"created":0,"updated":1,"failed":0}""", run.LastResult);
            Assert.Equal(1, run.RunCount);
        }
    }
}
