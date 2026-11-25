// The database functions on SQL Server: approve_roster_day's one transaction per
// day, and overview_rollup's set-based T-SQL with exact decimal sums.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Tests.Infrastructure;

namespace Roster.Data.Tests.Services.Functions;

[Trait("Category", "SqlServer")]
public sealed class DatabaseFunctionsSqlServerTests(SqlServerDatabaseFixture sql) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 9, 3);
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => sql.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DatabaseFunctionsService Fn()
    {
        var access = new AccessContextAccessor { Current = AccessContext.System("test caller") };
        var contexts = sql.Factory(access.Current);
        return new DatabaseFunctionsService(contexts, new StoreScope(access, contexts), new FixedClock(Now));
    }

    private async Task<(Guid Store, Guid Emp)> StoreWithEmployeeAsync()
    {
        await using var db = sql.System();
        var group = new DirectorGroup { Id = Guid.NewGuid(), Name = "G" + Guid.NewGuid().ToString("N")[..8] };
        var region = new Region { Id = Guid.NewGuid(), Name = "501 - Example", DirectorGroupId = group.Id };
        var store = new Store
        {
            Id = Guid.NewGuid(), Code = "G" + Random.Shared.Next(100, 999) + Guid.NewGuid().ToString("N")[..4], Name = "Example",
            Slug = Guid.NewGuid().ToString("N"), RegionId = region.Id, State = AuStates.Nsw, KioskToken = Guid.NewGuid().ToString("N"),
        };
        var emp = new Employee { Id = Guid.NewGuid(), StoreId = store.Id, Name = "1-Able, Ann", Role = "SA", ContractHours = 60 };
        db.AddRange(group, region, store, emp);
        await db.SaveChangesAsync();
        return (store.Id, emp.Id);
    }

    [SqlServerFact]
    public async Task Approve_roster_day_is_one_transaction()
    {
        var fn = Fn();
        var (store, emp) = await StoreWithEmployeeAsync();
        RosterDayShift Shift(string role, int from) =>
            new(emp, "1-Able, Ann", role, new TimeOnly(from, 0), new TimeOnly(from + 6, 0), 30, false, [new RosterDayTile(new TimeOnly(from, 0), "load_running", "ambient")]);

        await fn.ApproveRosterDayAsync(store, Day, [Shift("SA", 7)]);
        // The second shift breaks a constraint after the first is written: the whole
        // day rolls back, the published shift and the approval stand.
        await Assert.ThrowsAnyAsync<Exception>(() => fn.ApproveRosterDayAsync(store, Day, [Shift("SA", 9), Shift("XX", 12)]));
        await using var db = sql.System();
        var shift = await db.GeneratedShifts.Include(s => s.Blocks).SingleAsync(s => s.StoreId == store && s.Date == Day);
        Assert.Equal(new TimeOnly(7, 0), shift.StartTime);
        Assert.Equal("ambient", Assert.Single(shift.Blocks).SubTask);
        Assert.Equal(DayStatuses.Approved, (await db.DayPlans.SingleAsync(d => d.StoreId == store && d.Date == Day)).RosterStatus);
    }

    [SqlServerFact]
    public async Task Overview_rollup_in_tsql_sums_exactly_and_joins_every_date()
    {
        var fn = Fn();
        var (a, _) = await StoreWithEmployeeAsync();
        var (b, _) = await StoreWithEmployeeAsync();
        await using (var db = sql.System())
        {
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = a, Date = Day, SalesForecast = 0.1, Items = 10, CustomerCount = 2.5, LrHours100 = 9, CheckoutHours100 = 1.35 });
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = b, Date = Day, SalesForecast = 0.2, Items = 10, CustomerCount = 2.5, LrHours100 = 9, CheckoutHours100 = 1.35 });
            db.ActualsDays.Add(new ActualsDay { StoreId = a, Date = Day.AddDays(1), ActualSales = 1.1, ActualHours100 = 2.675, UkgActualHours = 8 });
            db.ActualsDays.Add(new ActualsDay { StoreId = b, Date = Day.AddDays(1), ActualSales = 2.2, ActualHours100 = 0.005, UkgActualHours = 8, ActualCustomers = 3 });
            db.DayPlans.Add(new DayPlan { StoreId = a, Date = Day.AddDays(2), RosterStatus = DayStatuses.Approved });
            await db.SaveChangesAsync();
        }
        var rows = await fn.OverviewRollupAsync([a, b], Day, Day.AddDays(2));
        Assert.Equal([Day, Day.AddDays(1), Day.AddDays(2)], rows.Select(r => r.Date));
        Assert.Equal(0.3, rows[0].ForecastSales);
        Assert.Equal(20, rows[0].BenchItems);
        Assert.Equal(5, rows[0].CustomerCount);
        Assert.Equal(20.7, rows[0].Hours100);
        // As Postgres's numeric sums: 1.1 + 2.2 is 3.3 (a float sum is 3.3000000000000003)
        // and 2.675 + 0.005 is 2.68 (a float sum is 2.6799999999999997).
        Assert.Equal(3.3, rows[1].ActualSales);
        Assert.Equal(2.68, rows[1].ActualHours100);
        Assert.Equal(3, rows[1].ActualCustomers);
        Assert.False(rows[1].AnyApproved);
        Assert.True(rows[2].AnyApproved);
        Assert.Equal(0, rows[2].ForecastSales);
    }
}
