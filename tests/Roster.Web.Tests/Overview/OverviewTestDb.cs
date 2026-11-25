// New: the Dashboard's and the Analysis pages' services over a private in-memory SQLite
// database built from the data layer's model, with the real store scope and data services,
// a fixed clock (Wednesday 25 November 2026, Sydney) and an invented user of any tier.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Analysis;
using Roster.Web.Features.Overview;

namespace Roster.Web.Tests.Overview;

public sealed class OverviewTestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    public static readonly DateOnly Today = new(2026, 11, 25);
    public static readonly Guid GroupId = Guid.Parse("d4000000-0000-4000-8000-000000000001");
    public static readonly Guid RegionId = Guid.Parse("e4000000-0000-4000-8000-000000000001");
    public static readonly Guid AreaNorth = Guid.Parse("a4000000-0000-4000-8000-000000000001");
    public static readonly Guid AreaSouth = Guid.Parse("a4000000-0000-4000-8000-000000000002");
    public static readonly Guid StoreA = Guid.Parse("5e400000-0000-4000-8000-000000000101");
    public static readonly Guid StoreB = Guid.Parse("5e400000-0000-4000-8000-000000000102");
    public static readonly Guid StoreC = Guid.Parse("5e400000-0000-4000-8000-000000000103");

    public OverviewTestDb()
    {
        _connection.Open();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 11, 24, 23, 0, 0, TimeSpan.Zero)));
        services.AddRosterData(b => b.UseSqlite(_connection), configuration);
        services.AddScoped<IRememberedStore, Remembered>();
        services.AddLogging();
        new OverviewModule().Register(services, configuration);
        new AnalysisModule().Register(services, configuration);
        _root = services.BuildServiceProvider();
        _scope = _root.CreateAsyncScope();
        Seed(db =>
        {
            db.Database.EnsureCreated();
            db.DirectorGroups.Add(new DirectorGroup { Id = GroupId, Name = "Group One" });
            db.Regions.Add(new Region { Id = RegionId, Name = "501 - Test Region", DirectorGroupId = GroupId });
            db.Areas.Add(new Area { Id = AreaNorth, Name = "North", RegionId = RegionId });
            db.Areas.Add(new Area { Id = AreaSouth, Name = "South", RegionId = RegionId });
            foreach (var (id, code, name, area) in new[] { (StoreA, "G101", "Northside", AreaNorth), (StoreB, "G102", "Riverbend", AreaNorth), (StoreC, "G103", "Southgate", AreaSouth) })
            {
                db.Stores.Add(new Store { Id = id, Code = code, Name = name, Slug = code.ToLowerInvariant(), RegionId = RegionId, AreaId = area, KioskToken = code });
                db.StoreAttributes.Add(new StoreAttributes { StoreId = id, OeTargetPct = id == StoreA ? 92 : 90 });
            }
            foreach (var p in Roster.Engine.Core.Positions.All)
                db.Positions.Add(new Position { Code = p.Code, Label = p.Label, BaseRole = p.BaseRole, WeeklyHours = p.WeeklyHours, YearBand = p.YearBand, HasRate = p.HasRate });
        });
        SignInAs(Tier.SuperUser);
    }

    private IServiceProvider Services => _scope.ServiceProvider;

    public IRosterDbContextFactory Contexts => Services.GetRequiredService<IRosterDbContextFactory>();

    public IOverviewService Overview => Services.GetRequiredService<IOverviewService>();

    public IAnalysisService Analysis => Services.GetRequiredService<IAnalysisService>();

    public Guid? RememberedStore
    {
        get => ((Remembered)Services.GetRequiredService<IRememberedStore>()).StoreId;
        set => ((Remembered)Services.GetRequiredService<IRememberedStore>()).StoreId = value;
    }

    /// <summary>An invented user of a tier: a Store user on store A, an EMSO on the north area.</summary>
    public void SignInAs(Tier tier, Guid? storeId = null, Guid? areaId = null)
    {
        var user = new ScopedUser(Guid.NewGuid(), "tester", tier,
            tier == Tier.Store ? storeId ?? StoreA : null,
            RegionId,
            tier == Tier.Emso ? areaId ?? AreaNorth : null,
            GroupId);
        Services.GetRequiredService<AccessContextAccessor>().Current = AccessContext.ForUser(user);
    }

    public void Seed(Action<RosterDbContext> change)
    {
        using var db = Contexts.CreateSystem("test setup");
        change(db);
        db.SaveChanges();
    }

    /// <summary>A day of benchmark and, optionally, actuals for a store.</summary>
    public void Day(Guid store, DateOnly date, double forecast, double? actual = null, double hours100 = 40, double kronos = 45, double customers = 0)
    {
        Seed(db =>
        {
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = store, Date = date, SalesForecast = forecast, Items = forecast / 4, CustomerCount = forecast / 40, LrHours100 = 45, CheckoutHours100 = 5 });
            if (actual is { } a)
                db.ActualsDays.Add(new ActualsDay { StoreId = store, Date = date, ActualSales = a, ActualItems = a / 4, ActualCustomers = customers > 0 ? customers : a / 40, ActualHours100 = hours100, UkgActualHours = kronos });
        });
    }

    public void Dispose()
    {
        _scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _root.Dispose();
        _connection.Dispose();
    }

    private sealed class Remembered : IRememberedStore
    {
        public Guid? StoreId { get; set; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
