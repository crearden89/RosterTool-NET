// New: the EMSO unapproved-roster check (src/lib/emso/rosterCheck.ts has no tests of its
// own), on the SQLite test database: who is alerted, the wording, and the idempotence.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Jobs;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Jobs;

public sealed class EmsoRosterCheckTests : IDisposable
{
    // Wednesday 25/11/2026 08:00 UTC = 19:00 in Sydney: the current period is 23/11-06/12,
    // so the planning period is 07/12-20/12.
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly PlanningStart = new(2026, 12, 7);
    private const string Label = "07/12 – 20/12";

    private readonly TestDatabase _db = new(new FixedClock(Wednesday));
    private readonly Guid _areaA, _areaB, _emsoA, _emsoB;
    private readonly Dictionary<string, Guid> _store = [];

    public EmsoRosterCheckTests()
    {
        using var sys = _db.System();
        var dg = new DirectorGroup { Name = "Example Group" };
        sys.DirectorGroups.Add(dg);
        sys.SaveChanges();
        var region = new Region { Name = "901 - Example Region", DirectorGroupId = dg.Id };
        sys.Regions.Add(region);
        sys.SaveChanges();
        var a = new Area { Name = "Example Area A", RegionId = region.Id };
        var b = new Area { Name = "Example Area B", RegionId = region.Id };
        sys.Areas.AddRange(a, b);
        sys.SaveChanges();
        (_areaA, _areaB) = (a.Id, b.Id);
        foreach (var (code, area) in new (string, Guid?)[] { ("G810", a.Id), ("G802", a.Id), ("G803", a.Id), ("G804", b.Id), ("G805", null) })
        {
            var s = new Store { Code = code, Name = $"Example {code}", Slug = code.ToLowerInvariant(), RegionId = region.Id, AreaId = area, KioskToken = $"k-{code}" };
            sys.Stores.Add(s);
            sys.SaveChanges();
            _store[code] = s.Id;
        }
        var users = new[]
        {
            new AppUser { Username = "emso.a@example.com", Tier = Tier.Emso, AreaId = a.Id },
            new AppUser { Username = "emso.b@example.com", Tier = Tier.Emso, AreaId = b.Id },
            new AppUser { Username = "super@example.com", Tier = Tier.SuperUser },
        };
        sys.AppUsers.AddRange(users);
        sys.SaveChanges();
        (_emsoA, _emsoB) = (users[0].Id, users[1].Id);
    }

    public void Dispose() => _db.Dispose();

    private IEmsoRosterCheck Check() =>
        new EmsoRosterCheck(new TestContexts(_db, AccessContext.Anonymous), _db.Clock);

    private void Demand(string code, params int[] days)
    {
        using var sys = _db.System();
        foreach (var d in days)
            sys.BenchmarkDays.Add(new BenchmarkDay { StoreId = _store[code], Date = PlanningStart.AddDays(d), SalesForecast = 40000 });
        sys.SaveChanges();
    }

    private void Roster(string code, string status, params int[] days)
    {
        using var sys = _db.System();
        foreach (var d in days)
            sys.DayPlans.Add(new DayPlan { StoreId = _store[code], Date = PlanningStart.AddDays(d), RosterStatus = status });
        sys.SaveChanges();
    }

    private List<Notification> Alerts(Guid userId)
    {
        using var sys = _db.System();
        return sys.Notifications.AsNoTracking().Where(n => n.UserId == userId).ToList().OrderBy(n => n.CreatedAt).ToList();
    }

    [Fact]
    public async Task A_store_with_demand_and_an_unapproved_day_alerts_its_areas_emso()
    {
        Demand("G802", 0, 1, 2);
        Roster("G802", DayStatuses.Approved, 0, 1);
        Roster("G802", DayStatuses.Generated, 2); // generated is not approved

        var result = await Check().RunEmsoUnapprovedCheckAsync();

        Assert.Equal(new EmsoCheckResult(1, 0, 0), result);
        var alert = Assert.Single(Alerts(_emsoA));
        Assert.Equal("emso_unapproved", alert.Kind);
        Assert.Equal($"1 store(s) without an approved roster for {Label}: G802", alert.Body);
        Assert.Equal("/overview", alert.Link);
        Assert.Null(alert.ReadAt);
        Assert.Empty(Alerts(_emsoB));
    }

    [Fact]
    public async Task Fully_approved_stores_and_stores_without_demand_are_not_behind()
    {
        Demand("G810", Enumerable.Range(0, 14).ToArray());
        Roster("G810", DayStatuses.Approved, Enumerable.Range(0, 14).ToArray());
        Roster("G803", DayStatuses.Generated, 0, 1); // no benchmark: nothing to approve

        Assert.Equal(new EmsoCheckResult(0, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());
        Assert.Empty(Alerts(_emsoA));
    }

    [Fact]
    public async Task Only_the_planning_period_counts()
    {
        // Demand in the current period (unapproved) and just after the planning period.
        using (var sys = _db.System())
        {
            sys.BenchmarkDays.Add(new BenchmarkDay { StoreId = _store["G802"], Date = PlanningStart.AddDays(-1) });
            sys.BenchmarkDays.Add(new BenchmarkDay { StoreId = _store["G802"], Date = PlanningStart.AddDays(14) });
            sys.SaveChanges();
        }
        Assert.Equal(new EmsoCheckResult(0, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());

        Demand("G802", 13); // the period's last Sunday
        Assert.Equal(new EmsoCheckResult(1, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());
    }

    [Fact]
    public async Task Behind_stores_are_listed_by_code_in_javascript_sort_order()
    {
        Demand("G810", 3);
        Demand("G802", 4);
        Demand("G803", 5);
        Demand("G804", 6); // area B
        Demand("G805", 6); // no area: nobody's

        var result = await Check().RunEmsoUnapprovedCheckAsync();

        Assert.Equal(new EmsoCheckResult(2, 0, 0), result);
        Assert.Equal($"3 store(s) without an approved roster for {Label}: G802, G803, G810", Assert.Single(Alerts(_emsoA)).Body);
        Assert.Equal($"1 store(s) without an approved roster for {Label}: G804", Assert.Single(Alerts(_emsoB)).Body);
    }

    [Fact]
    public async Task A_second_run_refreshes_the_unread_alert_instead_of_adding_one()
    {
        Demand("G802", 0);
        Assert.Equal(new EmsoCheckResult(1, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());

        Demand("G803", 1);
        Assert.Equal(new EmsoCheckResult(0, 1, 0), await Check().RunEmsoUnapprovedCheckAsync());

        var alert = Assert.Single(Alerts(_emsoA));
        Assert.Equal($"2 store(s) without an approved roster for {Label}: G802, G803", alert.Body);
    }

    [Fact]
    public async Task A_read_alert_is_not_refreshed_a_new_one_is_raised()
    {
        Demand("G802", 0);
        await Check().RunEmsoUnapprovedCheckAsync();
        using (var sys = _db.System())
        {
            var n = sys.Notifications.Single(x => x.UserId == _emsoA);
            n.ReadAt = Wednesday;
            sys.SaveChanges();
        }

        Assert.Equal(new EmsoCheckResult(1, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());
        Assert.Equal(2, Alerts(_emsoA).Count);
    }

    [Fact]
    public async Task An_unread_alert_for_another_period_or_kind_is_left_alone()
    {
        using (var sys = _db.System())
        {
            sys.Notifications.Add(new Notification { UserId = _emsoA, Kind = EmsoAlert.Kind, Body = "1 store(s) without an approved roster for 23/11 – 06/12: G802" });
            sys.Notifications.Add(new Notification { UserId = _emsoA, Kind = "roster_review", Body = $"Review for {Label}" });
            sys.SaveChanges();
        }
        Demand("G802", 0);

        Assert.Equal(new EmsoCheckResult(1, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());
        Assert.Equal(3, Alerts(_emsoA).Count);
    }

    [Fact]
    public async Task Two_matching_unread_alerts_count_as_none_and_a_third_is_inserted()
    {
        // supabase-js maybeSingle() errors on two rows; the TypeScript check then inserted.
        using (var sys = _db.System())
        {
            for (var i = 0; i < 2; i++)
                sys.Notifications.Add(new Notification { UserId = _emsoA, Kind = EmsoAlert.Kind, Body = $"old {i} for {Label}" });
            sys.SaveChanges();
        }
        Demand("G802", 0);

        Assert.Equal(new EmsoCheckResult(1, 0, 0), await Check().RunEmsoUnapprovedCheckAsync());
        Assert.Equal(3, Alerts(_emsoA).Count);
    }

    [Fact]
    public async Task A_write_that_fails_is_counted_as_failed_and_the_rest_still_run()
    {
        Demand("G802", 0);
        Demand("G804", 0);
        var check = new EmsoRosterCheck(new FailingWritesContexts(_db, _emsoA), _db.Clock);

        var result = await check.RunEmsoUnapprovedCheckAsync();

        Assert.Equal(new EmsoCheckResult(1, 0, 1), result);
        Assert.Empty(Alerts(_emsoA));
        Assert.Single(Alerts(_emsoB));
    }

    [Fact]
    public async Task On_a_wednesday_evening_in_sydney_the_target_is_the_next_period()
    {
        // Tuesday 22:30 UTC is Wednesday 09:30 in Sydney: still the 23/11 period, so the
        // target stays 07/12-20/12.
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 24, 22, 30, 0, TimeSpan.Zero));
        Demand("G802", 0);
        var check = new EmsoRosterCheck(new TestContexts(_db, AccessContext.Anonymous), clock);
        await check.RunEmsoUnapprovedCheckAsync();
        Assert.Contains(Label, Assert.Single(Alerts(_emsoA)).Body);
    }

    /// <summary>A factory whose contexts refuse to save a notification for one user (a write that fails).</summary>
    private sealed class FailingWritesContexts(TestDatabase db, Guid userId) : IRosterDbContextFactory
    {
        public RosterDbContext Create() => Watch(db.System());

        public RosterDbContext Create(AccessContext access) => Watch(db.As(access));

        public RosterDbContext CreateSystem(string reason) => Watch(db.As(AccessContext.System(reason)));

        private RosterDbContext Watch(RosterDbContext ctx)
        {
            ctx.SavingChanges += (_, _) =>
            {
                if (ctx.ChangeTracker.Entries<Notification>().Any(n => n.Entity.UserId == userId))
                    throw new DbUpdateException("simulated failure");
            };
            return ctx;
        }
    }
}

/// <summary>A clock stopped at one instant.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
