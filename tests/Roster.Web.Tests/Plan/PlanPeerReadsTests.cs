// New: the Plan page's peer view after the owner tightened plan_snapshots (and daily_loads)
// to the visible stores. Another store's manager may still open its Plan page, trimmed and
// read-only: the held plan is read through the peer gate, as the system, while their own
// reads of that store's held plans come back empty.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Held;
using Roster.Data.Snapshots;
using Roster.Web.Features.Plan;
using Roster.Web.Tests.Rules;

namespace Roster.Web.Tests.Plan;

public sealed class PlanPeerReadsTests : IDisposable
{
    private static readonly DateOnly Period = new(2026, 11, 23);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly Guid AreaA = Guid.Parse("a9000000-0000-4000-8000-000000000001");
    private static readonly Guid AreaB = Guid.Parse("a9000000-0000-4000-8000-000000000002");
    private static readonly Guid StoreA = Guid.Parse("59000000-0000-4000-8000-000000000001");
    private static readonly Guid StoreB = Guid.Parse("59000000-0000-4000-8000-000000000002");
    private static readonly ScopedUser ManagerOfA = new(Guid.NewGuid(), "sm.g911", Tier.Store, StoreA, null, null, null);
    private static readonly ScopedUser EmsoOfB = new(Guid.NewGuid(), "emso.south", Tier.Emso, null, null, AreaB, null);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    public PlanPeerReadsTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRosterData(b => b.UseSqlite(_connection), configuration);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 11, 25, 23, 0, 0, TimeSpan.Zero))));
        services.AddScoped<IRememberedStore, NoRememberedStore>();
        new PlanModule().Register(services, configuration);
        _root = services.BuildServiceProvider();
        _scope = _root.CreateAsyncScope();

        using var db = Get<IRosterDbContextFactory>().CreateSystem("test setup");
        db.Database.EnsureCreated();
        var dg = new DirectorGroup { Id = Guid.NewGuid(), Name = "Group One" };
        var region = new Region { Id = Guid.NewGuid(), Name = "501 - Example North", DirectorGroupId = dg.Id };
        db.AddRange(dg, region);
        db.Areas.Add(new Area { Id = AreaA, Name = "North", RegionId = region.Id });
        db.Areas.Add(new Area { Id = AreaB, Name = "South", RegionId = region.Id });
        db.Stores.Add(new Store { Id = StoreA, Code = "G911", Name = "Example North", Slug = "g911", RegionId = region.Id, AreaId = AreaA });
        db.Stores.Add(new Store { Id = StoreB, Code = "G912", Name = "Example South", Slug = "g912", RegionId = region.Id, AreaId = AreaB });
        db.PlanSnapshots.Add(new Roster.Data.Entities.PlanSnapshot { StoreId = StoreB, PeriodStart = Period, Kind = SnapshotKinds.Plan, Payload = "{\"days\":[]}", GeneratedAt = GeneratedAt, Version = 3 });
        db.PlanSnapshots.Add(new Roster.Data.Entities.PlanSnapshot { StoreId = StoreB, PeriodStart = Period, Kind = SnapshotKinds.Roster, Payload = "{\"byDate\":[]}", GeneratedAt = GeneratedAt });
        db.SaveChanges();
    }

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    private void SignIn(ScopedUser user) => Get<AccessContextAccessor>().Current = AccessContext.ForUser(user);

    public void Dispose()
    {
        _scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _root.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Another_stores_manager_still_sees_its_held_plan_on_the_peer_plan_page()
    {
        SignIn(ManagerOfA);
        var page = await Get<IPlanService>().LoadPageAsync(new PlanPageQuery(Store: StoreB.ToString(), Period: "2026-11-23"));
        Assert.Equal(StoreB, page.SelectedStore);
        Assert.True(page.Peer);
        Assert.True(page.HasSnapshot);
        Assert.Equal(GeneratedAt, page.GeneratedAt);
    }

    [Fact]
    public async Task Their_own_reads_of_that_stores_held_plan_and_roster_come_back_empty()
    {
        SignIn(ManagerOfA);
        Assert.Null(await Get<IHeldPlans>().ReadPlanAsync(StoreB, Period));
        var held = await Get<IPlanSnapshotStore>().ReadAsync(StoreB, Period);
        Assert.Null(held.Plan);
        Assert.Null(held.Roster);

        // The store's own area EMSO reads both, as before.
        SignIn(EmsoOfB);
        Assert.Equal(3, (await Get<IHeldPlans>().ReadPlanAsync(StoreB, Period))?.Version);
        Assert.NotNull((await Get<IPlanSnapshotStore>().ReadAsync(StoreB, Period)).Roster);
    }

    private sealed class NoRememberedStore : IRememberedStore
    {
        public Guid? StoreId => null;
    }
}
