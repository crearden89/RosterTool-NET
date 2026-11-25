// Tests for the ports of the database functions (supabase/migrations 0049,
// 0027, 0037, 0007, 0043), on D's in-memory SQLite. The TypeScript has no
// tests of them; these pin each function's documented behaviour.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Services.Functions;

public sealed class DatabaseFunctionsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 1, 0, 0, TimeSpan.Zero);
    private readonly TestDatabase _tdb = new();
    private readonly TestContexts _contexts;
    private readonly FixedClock _clock = new(Now);
    private readonly DatabaseFunctionsService _fn;

    public DatabaseFunctionsTests()
    {
        _contexts = new TestContexts(_tdb);
        _fn = new DatabaseFunctionsService(_contexts, _contexts.Scope(), _clock);
    }

    public void Dispose() => _tdb.Dispose();

    private static readonly DateOnly Day = new(2026, 9, 3);

    private static RosterDayShift Shift(Guid emp, string name, int from, int to, bool locked, params RosterDayTile[] tiles) =>
        new(emp, name, "SA", new TimeOnly(from, 0), new TimeOnly(to, 0), 30, locked, tiles);

    [Fact]
    public async Task Approve_roster_day_replaces_the_whole_day_and_flips_the_status_last()
    {
        var store = Seed.Stores(_tdb)[0];
        var a = Seed.Employee(_tdb, store.Id, "1-Able, Ann");
        var b = Seed.Employee(_tdb, store.Id, "2-Baker, Ben");

        await _fn.ApproveRosterDayAsync(store.Id, Day, [
            Shift(a.Id, a.Name, 7, 15, locked: true, new RosterDayTile(new TimeOnly(7, 0), "load_running", "ambient"), new RosterDayTile(new TimeOnly(7, 15), "break")),
            Shift(b.Id, b.Name, 9, 17, locked: false, new RosterDayTile(new TimeOnly(9, 0), "registers", "")),
        ]);
        using (var db = _tdb.System())
        {
            var shifts = await db.GeneratedShifts.Where(s => s.StoreId == store.Id && s.Date == Day).Include(s => s.Blocks).OrderBy(s => s.StartTime).ToListAsync();
            Assert.Equal(2, shifts.Count);
            Assert.True(shifts[0].Locked);
            Assert.Equal(["ambient", null], shifts[0].Blocks.OrderBy(x => x.BlockTime).Select(x => x.SubTask));
            // nullif(sub_task, ''): an empty label is stored as none.
            Assert.Null(shifts[1].Blocks.Single().SubTask);
            var plan = await db.DayPlans.SingleAsync(d => d.StoreId == store.Id && d.Date == Day);
            Assert.Equal(DayStatuses.Approved, plan.RosterStatus);
            Assert.Equal(Now, plan.RosterGeneratedAt);
            Assert.Equal(DayStatuses.None, plan.PlanStatus);
        }

        // Again, with one shift: the locked one goes too - the payload is the whole day.
        _clock.Now = Now.AddHours(1);
        await _fn.ApproveRosterDayAsync(store.Id, Day, [Shift(b.Id, b.Name, 10, 16, locked: false)]);
        using (var db = _tdb.System())
        {
            var shift = await db.GeneratedShifts.Include(s => s.Blocks).SingleAsync(s => s.StoreId == store.Id && s.Date == Day);
            Assert.Equal(new TimeOnly(10, 0), shift.StartTime);
            Assert.Empty(shift.Blocks);
            Assert.Equal(0, await db.ShiftTaskBlocks.CountAsync());
            Assert.Equal(Now.AddHours(1), (await db.DayPlans.SingleAsync()).RosterGeneratedAt);
        }

        // An empty payload clears the day and still approves it.
        await _fn.ApproveRosterDayAsync(store.Id, Day, null);
        using (var db = _tdb.System())
        {
            Assert.Equal(0, await db.GeneratedShifts.CountAsync());
            Assert.Equal(DayStatuses.Approved, (await db.DayPlans.SingleAsync()).RosterStatus);
        }
    }

    [Fact]
    public async Task Approve_roster_day_leaves_other_days_and_stores_alone()
    {
        var stores = Seed.Stores(_tdb, 2);
        var a = Seed.Employee(_tdb, stores[0].Id, "1-Able, Ann");
        var c = Seed.Employee(_tdb, stores[1].Id, "1-Cole, Cat");
        await _fn.ApproveRosterDayAsync(stores[0].Id, Day.AddDays(1), [Shift(a.Id, a.Name, 7, 15, false)]);
        await _fn.ApproveRosterDayAsync(stores[1].Id, Day, [Shift(c.Id, c.Name, 7, 15, false)]);
        await _fn.ApproveRosterDayAsync(stores[0].Id, Day, [Shift(a.Id, a.Name, 8, 16, false)]);
        using var db = _tdb.System();
        Assert.Equal(3, await db.GeneratedShifts.CountAsync());
        Assert.Equal(3, await db.DayPlans.CountAsync(d => d.RosterStatus == DayStatuses.Approved));
    }

    [Fact]
    public async Task Overview_rollup_sums_each_date_exactly_across_the_stores()
    {
        var stores = Seed.Stores(_tdb, 3);
        using (var db = _tdb.System())
        {
            foreach (var (s, sales) in new[] { (stores[0], 0.1), (stores[1], 0.2) })
            {
                db.BenchmarkDays.Add(new BenchmarkDay { StoreId = s.Id, Date = Day, SalesForecast = sales, Items = 10, CustomerCount = 2.5, LrHours100 = 9, CheckoutHours100 = 1.35 });
            }
            db.ActualsDays.Add(new ActualsDay { StoreId = stores[0].Id, Date = Day.AddDays(1), ActualSales = 5, ActualHours100 = 7.25, UkgActualHours = 8, ActualCustomers = null });
            db.NonOpEntries.Add(new NonOpEntry { Id = Guid.NewGuid(), StoreId = stores[1].Id, Date = Day, SubcategoryId = NewSubcategory(db), Minutes = 45 });
            db.DayPlans.Add(new DayPlan { StoreId = stores[0].Id, Date = Day, RosterStatus = DayStatuses.Generated });
            db.DayPlans.Add(new DayPlan { StoreId = stores[1].Id, Date = Day, RosterStatus = DayStatuses.Approved });
            db.DayPlans.Add(new DayPlan { StoreId = stores[2].Id, Date = Day.AddDays(2), RosterStatus = DayStatuses.None });
            // Outside the range, and a store not asked for.
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = stores[0].Id, Date = Day.AddDays(30), SalesForecast = 99 });
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = stores[2].Id, Date = Day, SalesForecast = 1000 });
            await db.SaveChangesAsync();
        }

        var rows = await _fn.OverviewRollupAsync([stores[0].Id, stores[1].Id], Day, Day.AddDays(2));
        Assert.Equal([Day, Day.AddDays(1)], rows.Select(r => r.Date));
        var first = rows[0];
        // Postgres summed numerics: 0.1 + 0.2 is 0.3, not 0.30000000000000004.
        Assert.Equal(0.3, first.ForecastSales);
        Assert.Equal(20, first.BenchItems);
        Assert.Equal(5, first.CustomerCount);
        Assert.Equal(2 * (9 + 1.35), first.Hours100, 12);
        Assert.Equal(45, first.NonopMinutes);
        Assert.True(first.AnyApproved);
        Assert.True(first.AnyGenerated);
        var second = rows[1];
        Assert.Equal(5, second.ActualSales);
        Assert.Equal(0, second.ActualCustomers);
        Assert.Equal(7.25, second.ActualHours100);
        Assert.Equal(8, second.KronosHours);
        Assert.False(second.AnyApproved);
        Assert.Equal(0, second.ForecastSales);
    }

    [Fact]
    public async Task Overview_rollup_counts_only_the_stores_the_caller_can_see_unless_as_a_peer()
    {
        var stores = Seed.Stores(_tdb, 2);
        using (var db = _tdb.System())
        {
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = stores[0].Id, Date = Day, SalesForecast = 10 });
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = stores[1].Id, Date = Day, SalesForecast = 20 });
            await db.SaveChangesAsync();
        }
        _contexts.Current = AccessContext.ForUser(new ScopedUser(Guid.NewGuid(), "store.user", Tier.Store, stores[0].Id, null, null, null));
        var fn = new DatabaseFunctionsService(_contexts, _contexts.Scope(), _clock);
        var mine = await fn.OverviewRollupAsync([stores[0].Id, stores[1].Id], Day, Day);
        Assert.Equal(10, Assert.Single(mine).ForecastSales);
        var peer = await fn.OverviewRollupAsync([stores[0].Id, stores[1].Id], Day, Day, asPeer: true);
        Assert.Equal(30, Assert.Single(peer).ForecastSales);
    }

    private static Guid NewSubcategory(RosterDbContext db)
    {
        var cat = new NonOpCategory { Id = Guid.NewGuid(), Name = "Training" };
        var sub = new NonOpSubcategory { Id = Guid.NewGuid(), CategoryId = cat.Id, Name = "Induction" };
        db.NonOpCategories.Add(cat);
        db.NonOpSubcategories.Add(sub);
        return sub.Id;
    }

    [Fact]
    public async Task Leave_period_ends_are_distinct_and_newest_first()
    {
        var stores = Seed.Stores(_tdb, 2);
        using (var db = _tdb.System())
        {
            void Add(Guid store, string end) => db.LeaveLiabilitySnapshots.Add(new LeaveLiabilitySnapshot
            {
                Id = Guid.NewGuid(), StoreId = store, PeriodEnd = DateOnly.Parse(end), UkgEmployeeId = Guid.NewGuid().ToString("N")[..8], EmployeeName = "X",
            });
            Add(stores[0].Id, "2026-10-04");
            Add(stores[0].Id, "2026-10-04");
            Add(stores[0].Id, "2026-10-18");
            Add(stores[1].Id, "2026-11-01");
            await db.SaveChangesAsync();
        }
        Assert.Equal([new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 4)], await _fn.LeavePeriodEndsAsync());
        Assert.Equal([new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 4)], await _fn.LeavePeriodEndsAsync(stores[0].Id));
    }

    [Fact]
    public async Task Is_public_holiday_combines_the_three_tiers()
    {
        var stores = Seed.Stores(_tdb, 2);
        using (var db = _tdb.System())
        {
            db.PublicHolidaysNational.Add(new PublicHolidayNational { Id = Guid.NewGuid(), Date = new DateOnly(2026, 12, 25), Name = "Christmas Day" });
            db.PublicHolidaysState.Add(new PublicHolidayState { Id = Guid.NewGuid(), State = AuStates.Nsw, Date = new DateOnly(2026, 10, 5), Name = "Labour Day" });
            db.PublicHolidaysState.Add(new PublicHolidayState { Id = Guid.NewGuid(), State = AuStates.Vic, Date = new DateOnly(2026, 11, 3), Name = "Cup Day" });
            db.PublicHolidaysLocal.Add(new PublicHolidayLocal { Id = Guid.NewGuid(), StoreId = stores[1].Id, Date = new DateOnly(2026, 9, 3), Name = "Show Day" });
            await db.SaveChangesAsync();
        }
        Assert.True(await _fn.IsPublicHolidayAsync(stores[0].Id, new DateOnly(2026, 12, 25)));
        Assert.True(await _fn.IsPublicHolidayAsync(stores[0].Id, new DateOnly(2026, 10, 5)));
        Assert.False(await _fn.IsPublicHolidayAsync(stores[0].Id, new DateOnly(2026, 11, 3)));
        Assert.False(await _fn.IsPublicHolidayAsync(stores[0].Id, new DateOnly(2026, 9, 3)));
        Assert.True(await _fn.IsPublicHolidayAsync(stores[1].Id, new DateOnly(2026, 9, 3)));
    }

    [Fact]
    public async Task Bump_cron_run_inserts_then_counts()
    {
        await _fn.BumpCronRunAsync("emso-check", JsonSerializer.SerializeToElement(new { notified = 2 }));
        _clock.Now = Now.AddDays(7);
        await _fn.BumpCronRunAsync("emso-check");
        await _fn.BumpCronRunAsync("keep-alive", JsonSerializer.SerializeToElement((object?)null));
        using var db = _tdb.System();
        var run = await db.CronRuns.SingleAsync(c => c.Job == "emso-check");
        Assert.Equal(2, run.RunCount);
        Assert.Equal(Now.AddDays(7), run.LastRunAt);
        // excluded.last_result: the latest run's result replaces the last, a missing one included.
        Assert.Null(run.LastResult);
        var alive = await db.CronRuns.SingleAsync(c => c.Job == "keep-alive");
        Assert.Equal(1, alive.RunCount);
        Assert.Null(alive.LastResult);
    }
}
