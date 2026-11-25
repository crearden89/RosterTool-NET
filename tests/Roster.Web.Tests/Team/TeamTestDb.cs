// New: the Team Members and Weekends services over a private in-memory SQLite database
// built from the data layer's model, with the real store scope, reference data and
// planning periods, signed in as whichever invented user a test chooses.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Team;
using Roster.Web.Features.Weekends;

namespace Roster.Web.Tests.Team;

public sealed class TeamTestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    public static readonly Guid DirectorGroupId = Guid.Parse("d1000000-0000-4000-8000-000000000001");
    public static readonly Guid RegionId = Guid.Parse("e1000000-0000-4000-8000-000000000001");
    public static readonly Guid AreaNorth = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    public static readonly Guid AreaSouth = Guid.Parse("a1000000-0000-4000-8000-000000000002");
    public static readonly Guid StoreA = Guid.Parse("5e000000-0000-4000-8000-000000000101");
    public static readonly Guid StoreB = Guid.Parse("5e000000-0000-4000-8000-000000000102");
    public static readonly Guid StoreC = Guid.Parse("5e000000-0000-4000-8000-000000000103");

    public TeamTestDb()
    {
        _connection.Open();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddRosterData(b => b.UseSqlite(_connection), configuration);
        services.AddScoped<IRememberedStore, Remembered>();
        new TeamModule().Register(services, configuration);
        new WeekendsModule().Register(services, configuration);
        _root = services.BuildServiceProvider();
        _scope = _root.CreateAsyncScope();
        using (var db = Contexts.CreateSystem("test setup"))
        {
            db.Database.EnsureCreated();
            db.DirectorGroups.Add(new DirectorGroup { Id = DirectorGroupId, Name = "Group One" });
            db.Regions.Add(new Region { Id = RegionId, Name = "501 - Test Region", DirectorGroupId = DirectorGroupId });
            db.Areas.Add(new Area { Id = AreaNorth, Name = "North", RegionId = RegionId });
            db.Areas.Add(new Area { Id = AreaSouth, Name = "South", RegionId = RegionId });
            db.Stores.Add(new Store { Id = StoreA, Code = "G101", Name = "Northside", Slug = "g101", RegionId = RegionId, AreaId = AreaNorth });
            db.Stores.Add(new Store { Id = StoreB, Code = "G102", Name = "Riverbend", Slug = "g102", RegionId = RegionId, AreaId = AreaNorth });
            db.Stores.Add(new Store { Id = StoreC, Code = "G103", Name = "Southgate", Slug = "g103", RegionId = RegionId, AreaId = AreaSouth });
            foreach (var p in Roster.Engine.Core.Positions.All)
                db.Positions.Add(new Position { Code = p.Code, Label = p.Label, BaseRole = p.BaseRole, WeeklyHours = p.WeeklyHours, YearBand = p.YearBand, HasRate = p.HasRate });
            db.SaveChanges();
        }
        SignInAs(Tier.SuperUser);
    }

    private IServiceProvider Services => _scope.ServiceProvider;

    public IRosterDbContextFactory Contexts => Services.GetRequiredService<IRosterDbContextFactory>();

    public ITeamService Team => Services.GetRequiredService<ITeamService>();

    public IWeekendsService Weekends => Services.GetRequiredService<IWeekendsService>();

    /// <summary>The store last chosen in a picker (the roster_store_id cookie).</summary>
    public Guid? RememberedStore
    {
        get => ((Remembered)Services.GetRequiredService<IRememberedStore>()).StoreId;
        set => ((Remembered)Services.GetRequiredService<IRememberedStore>()).StoreId = value;
    }

    /// <summary>Signs in an invented user of a tier (a Store user on store A, an EMSO on the north area).</summary>
    public void SignInAs(Tier tier, Guid? storeId = null, Guid? areaId = null)
    {
        var user = new ScopedUser(Guid.NewGuid(), "tester", tier,
            tier == Tier.Store ? storeId ?? StoreA : null,
            RegionId,
            tier == Tier.Emso ? areaId ?? AreaNorth : null,
            DirectorGroupId);
        Services.GetRequiredService<AccessContextAccessor>().Current = AccessContext.ForUser(user);
    }

    /// <summary>Adds a team member as the system, with a full week of availability.</summary>
    public Guid AddEmployee(Guid storeId, string name, string role = "SA", double contract = 60, bool active = true, string? position = null)
    {
        using var db = Contexts.CreateSystem("test setup");
        var id = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = id, StoreId = storeId, Name = name, Role = role, ContractHours = contract, Active = active,
            PositionCode = position ?? Roster.Engine.Core.Positions.DefaultPositionFor(role),
        });
        for (var d = 0; d < 7; d++) db.EmployeeAvailability.Add(new EmployeeAvailability { EmployeeId = id, DayOfWeek = d, Available = true });
        db.SaveChanges();
        return id;
    }

    /// <summary>Runs a change as the system (rows a test needs in place).</summary>
    public void Seed(Action<RosterDbContext> change)
    {
        using var db = Contexts.CreateSystem("test setup");
        change(db);
        db.SaveChanges();
    }

    /// <summary>Reads as the system.</summary>
    public T Read<T>(Func<RosterDbContext, T> query)
    {
        using var db = Contexts.CreateSystem("test read");
        return query(db);
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
}
