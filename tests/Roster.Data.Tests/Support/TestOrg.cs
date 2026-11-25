// New: a small invented organisation for the store-scope tests.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Support;

/// <summary>
/// Two director groups, three regions, two areas and four example stores:
///   S1, S2 in region R1 and area A1; S3 in region R2 and area A2; S4 in region R3, no area.
/// One login per tier, and per store an employee, a benchmark day, a published shift with
/// one tile, a leave booking, a walk note and a load assignment.
/// </summary>
public sealed class TestOrg
{
    public Guid D1, D2, R1, R2, R3, A1, A2, S1, S2, S3, S4;
    public ScopedUser Super = null!, StoreS1 = null!, EmsoA1 = null!, PaR1 = null!, DirectorD1 = null!;
    public readonly Dictionary<Guid, Guid> EmployeeOf = [];
    public readonly Dictionary<Guid, Guid> ShiftOf = [];
    public Guid[] AllStores => [S1, S2, S3, S4];

    public static TestOrg Create(TestDatabase db) => Create(db.System);

    /// <summary>Seeds through contexts from <paramref name="system"/> (SQLite or SQL Server).</summary>
    public static TestOrg Create(Func<RosterDbContext> system)
    {
        var o = new TestOrg();
        using var sys = system();

        var d1 = new DirectorGroup { Name = "Example Group One" };
        var d2 = new DirectorGroup { Name = "Example Group Two" };
        sys.DirectorGroups.AddRange(d1, d2);
        sys.SaveChanges();
        var r1 = new Region { Name = "901 - Example North", DirectorGroupId = d1.Id };
        var r2 = new Region { Name = "902 - Example South", DirectorGroupId = d1.Id };
        var r3 = new Region { Name = "903 - Example West", DirectorGroupId = d2.Id };
        sys.Regions.AddRange(r1, r2, r3);
        sys.SaveChanges();
        var a1 = new Area { Name = "Example Area One", RegionId = r1.Id };
        var a2 = new Area { Name = "Example Area Two", RegionId = r2.Id };
        sys.Areas.AddRange(a1, a2);
        sys.SaveChanges();

        Store Make(string code, Guid region, Guid? area) =>
            new() { Code = code, Name = $"Example Store {code}", Slug = code.ToLowerInvariant(), RegionId = region, AreaId = area, State = AuStates.Nsw, KioskToken = $"kiosk-{code}" };
        var s1 = Make("G801", r1.Id, a1.Id);
        var s2 = Make("G802", r1.Id, a1.Id);
        var s3 = Make("G803", r2.Id, a2.Id);
        var s4 = Make("G804", r3.Id, null);
        sys.Stores.AddRange(s1, s2, s3, s4);
        sys.SaveChanges();

        (o.D1, o.D2, o.R1, o.R2, o.R3, o.A1, o.A2) = (d1.Id, d2.Id, r1.Id, r2.Id, r3.Id, a1.Id, a2.Id);
        (o.S1, o.S2, o.S3, o.S4) = (s1.Id, s2.Id, s3.Id, s4.Id);

        var day = new DateOnly(2026, 8, 3);
        foreach (var s in new[] { s1, s2, s3, s4 })
        {
            var e = new Employee { StoreId = s.Id, Name = $"4-Example, Person {s.Code}", Role = Roles.Sa, ContractHours = 60 };
            sys.Employees.Add(e);
            sys.SaveChanges();
            o.EmployeeOf[s.Id] = e.Id;
            sys.BenchmarkDays.Add(new BenchmarkDay { StoreId = s.Id, Date = day, SalesForecast = 45000, LrHours100 = 18 });
            var shift = new GeneratedShift
            {
                StoreId = s.Id, Date = day, EmployeeId = e.Id, EmployeeName = e.Name, Role = Roles.Sa,
                StartTime = new TimeOnly(6, 0), EndTime = new TimeOnly(14, 30), BreakMinutes = 60,
                Blocks = [new ShiftTaskBlock { BlockTime = new TimeOnly(6, 0), LabourGroup = "load_running" }],
            };
            sys.GeneratedShifts.Add(shift);
            sys.EmployeeLeave.Add(new EmployeeLeave { EmployeeId = e.Id, StartDate = day, EndDate = day.AddDays(4) });
            sys.StoreWalkNotes.Add(new StoreWalkNote { StoreId = s.Id, Note = "Spill in aisle 3", RaisedDate = day });
            sys.LoadAssignments.Add(new LoadAssignment { StoreId = s.Id, Date = day, EmployeeId = e.Id, EmployeeName = e.Name, LoadType = "Ambient" });
            sys.DayPlans.Add(new DayPlan { StoreId = s.Id, Date = day, RosterStatus = DayStatuses.Approved });
            sys.SaveChanges();
            o.ShiftOf[s.Id] = shift.Id;
        }

        AppUser U(string name, Tier tier, Guid? store = null, Guid? area = null, Guid? region = null, Guid? dg = null) =>
            new() { Username = name, Tier = tier, StoreId = store, AreaId = area, RegionId = region, DirectorGroupId = dg };
        var users = new[]
        {
            U("super@example.com", Tier.SuperUser),
            U("store.s1@example.com", Tier.Store, store: s1.Id),
            U("emso.a1@example.com", Tier.Emso, area: a1.Id),
            U("pa.r1@example.com", Tier.Pa, region: r1.Id),
            U("dso.d1@example.com", Tier.Director, dg: d1.Id),
        };
        sys.AppUsers.AddRange(users);
        sys.SaveChanges();
        o.Super = ScopedUser.From(users[0]);
        o.StoreS1 = ScopedUser.From(users[1]);
        o.EmsoA1 = ScopedUser.From(users[2]);
        o.PaR1 = ScopedUser.From(users[3]);
        o.DirectorD1 = ScopedUser.From(users[4]);

        foreach (var u in users)
            sys.Notifications.Add(new Notification { UserId = u.Id, Kind = "emso_unapproved", Body = $"for {u.Username}" });
        sys.UploadBatches.Add(new UploadBatch { Type = UploadTypes.Benchmark, Filename = "bench.csv" });
        sys.FeedTokens.Add(new FeedToken { Label = "Warehouse sync", Token = "feed-token-1" });
        sys.CronRuns.Add(new CronRun { Job = "emso-check", RunCount = 1 });
        // A database built by the migrations already has Ambient (the ReferenceData migration).
        if (!sys.LoadTypes.IgnoreQueryFilters().Any(l => l.Name == "Ambient"))
            sys.LoadTypes.Add(new LoadType { Name = "Ambient", MinutesPerPallet = 19 });
        sys.SaveChanges();
        return o;
    }
}

/// <summary>An <see cref="IRosterDbContextFactory"/> over a <see cref="TestDatabase"/>, acting as one identity.</summary>
public sealed class TestContexts(TestDatabase db, AccessContext current) : IRosterDbContextFactory, IAccessContextAccessor
{
    public AccessContext Current => current;
    public RosterDbContext Create() => db.As(current);
    public RosterDbContext Create(AccessContext access) => db.As(access);
    public RosterDbContext CreateSystem(string reason) => db.As(AccessContext.System(reason));
}
