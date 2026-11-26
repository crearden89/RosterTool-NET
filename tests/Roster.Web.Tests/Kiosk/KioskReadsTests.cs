// New: the kiosk pages still read their store's truck after the owner tightened daily_loads
// (and plan_snapshots) from "any signed-in user" to the visible stores. The Load Planner and
// the Floor Display open their link as the pages do, then read through the kiosk context,
// whose one visible store is the link's: its own pallets show, another store's never do.

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Floor;
using Roster.Web.Features.LoadPlanner;
using Roster.Web.Kiosk;
using Roster.Web.Tests.Auth;
using Roster.Web.Tests.Rules;

namespace Roster.Web.Tests.Kiosk;

public sealed class KioskReadsTests : IDisposable
{
    // 10:00 on Thursday 26 November in Sydney (AEDT): the stores' today.
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 26);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;
    private readonly Guid _ambient = Guid.NewGuid(), _freezer = Guid.NewGuid();

    public KioskReadsTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddRosterData(b => b.UseSqlite(_connection), configuration);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FixedClock(Now)));
        services.AddScoped<IKioskLinkCheck>(sp => new KioskLinkCheck(
            sp.GetRequiredService<IStoreDirectory>(),
            sp.GetRequiredService<AccessContextAccessor>(),
            PolicyTests.Monitor(new KioskOptions { RequireDeviceSignIn = false }),
            new NoSignIn()));
        new FloorModule().Register(services, configuration);
        new LoadPlannerModule().Register(services, configuration);
        _root = services.BuildServiceProvider();
        _scope = _root.CreateAsyncScope();

        using var db = _scope.ServiceProvider.GetRequiredService<IRosterDbContextFactory>().CreateSystem("test setup");
        db.Database.EnsureCreated();
        var dg = new DirectorGroup { Id = Guid.NewGuid(), Name = "Group One" };
        var region = new Region { Id = Guid.NewGuid(), Name = "501 - Example North", DirectorGroupId = dg.Id };
        db.AddRange(dg, region);
        db.Stores.Add(new Store { Id = StoreA, Code = "G901", Name = "Example North", Slug = "g901", RegionId = region.Id, KioskToken = "kiosk-a" });
        db.Stores.Add(new Store { Id = StoreB, Code = "G902", Name = "Example South", Slug = "g902", RegionId = region.Id, KioskToken = "kiosk-b" });
        db.LoadTypes.Add(new LoadType { Id = _ambient, Name = "Ambient", MinutesPerPallet = 19, SortOrder = 1, Priority = 5 });
        db.LoadTypes.Add(new LoadType { Id = _freezer, Name = "Freezer", MinutesPerPallet = 22, SortOrder = 2, Priority = 2 });
        db.DailyLoads.Add(new DailyLoad { StoreId = StoreA, Date = Today, LoadTypeId = _ambient, Qty = 6, UpdatedAt = Now });
        db.DailyLoads.Add(new DailyLoad { StoreId = StoreB, Date = Today, LoadTypeId = _ambient, Qty = 9, UpdatedAt = Now });
        db.DailyLoads.Add(new DailyLoad { StoreId = StoreB, Date = Today, LoadTypeId = _freezer, Qty = 3, UpdatedAt = Now });
        db.SaveChanges();
    }

    private static readonly Guid StoreA = Guid.Parse("5e000000-0000-4000-8000-000000000901");
    private static readonly Guid StoreB = Guid.Parse("5e000000-0000-4000-8000-000000000902");

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _root.Dispose();
        _connection.Dispose();
    }

    private async Task<LoadPageData> LoadPlanner(string token)
    {
        var link = await Get<IKioskLinkCheck>().CheckAsync(token);
        return await Get<ILoadPlannerService>().LoadAsync(token, link, fromFloor: false);
    }

    private async Task<LoadSummary> FloorTruck(string token)
    {
        var link = await Get<IKioskLinkCheck>().CheckAsync(token);
        var page = await Get<IFloorService>().LoadAsync(token, link, date: null, view: null);
        return page.Screen!.Load;
    }

    [Fact]
    public async Task The_load_planner_reads_its_own_stores_truck_for_today()
    {
        var a = await LoadPlanner("kiosk-a");
        Assert.Equal(Today, a.Day);
        Assert.True(a.HasEntry);
        Assert.Equal([("Ambient", 6.0), ("Freezer", 0.0)], a.Types.Select(t => (t.Name, t.Qty)));

        var b = await LoadPlanner("kiosk-b");
        Assert.Equal([("Ambient", 9.0), ("Freezer", 3.0)], b.Types.Select(t => (t.Name, t.Qty)));
    }

    [Fact]
    public async Task The_floor_display_reads_its_own_stores_truck_for_today()
    {
        var truck = await FloorTruck("kiosk-a");
        Assert.True(truck.Entered);
        Assert.Equal(6, truck.Total);
        Assert.Equal(["Ambient"], truck.Items.Select(i => i.Type));
    }

    [Fact]
    public async Task A_truck_saved_on_the_kiosk_reads_back_on_both_pages_and_nowhere_else()
    {
        var link = await Get<IKioskLinkCheck>().CheckAsync("kiosk-a");
        var saved = await Get<ILoadPlannerService>().SaveDailyLoadAsync("kiosk-a", [(_ambient.ToString(), 7), (_freezer.ToString(), 2)]);
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(KioskLinkStatus.Open, link.Status);

        Assert.Equal([("Ambient", 7.0), ("Freezer", 2.0)], (await LoadPlanner("kiosk-a")).Types.Select(t => (t.Name, t.Qty)));
        Assert.Equal(9, (await FloorTruck("kiosk-a")).Total);
        // The other store's truck is as it was.
        Assert.Equal([("Ambient", 9.0), ("Freezer", 3.0)], (await LoadPlanner("kiosk-b")).Types.Select(t => (t.Name, t.Qty)));
    }

    [Fact]
    public void A_kiosk_reads_only_its_own_stores_daily_loads()
    {
        using var kiosk = Get<IRosterDbContextFactory>().Create(AccessContext.ForKiosk(StoreA));
        Assert.Equal([StoreA], kiosk.DailyLoads.Select(l => l.StoreId).Distinct().ToList());
    }

    private sealed class NoSignIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
