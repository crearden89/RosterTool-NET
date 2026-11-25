// New: an in-memory SQLite database on the data layer's model, with the store scope and
// the context factory the Sales, Non-Op and Leave Liability services take, for their tests.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Snapshots;

namespace Roster.Web.Tests.Sales;

public sealed class PageDb : IDisposable, IRosterDbContextFactory, IRememberedStore
{
    private readonly SqliteConnection _connection;

    public PageDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<RosterDbContext>().UseSqlite(_connection).Options;
        using var db = System();
        db.Database.EnsureCreated();
        Scope = new StoreScope(Accessor, this);
        Snapshots = new PlanSnapshotStore(this);
    }

    public DbContextOptions<RosterDbContext> Options { get; }
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public AccessContextAccessor Accessor { get; } = new();
    public StoreScope Scope { get; }
    public PlanSnapshotStore Snapshots { get; }

    /// <summary>The remembered store choice (the roster_store_id cookie).</summary>
    public Guid? StoreId { get; set; }

    public RosterDbContext Create() => new(Options, Accessor.Current, Clock);
    public RosterDbContext Create(AccessContext access) => new(Options, access, Clock);
    public RosterDbContext CreateSystem(string reason) => new(Options, AccessContext.System(reason), Clock);
    public RosterDbContext System() => CreateSystem("test setup");

    public void SignIn(ScopedUser user) => Accessor.Current = AccessContext.ForUser(user);

    // ─── Seeding ───────────────────────────────────────────────────────────────

    public Guid Region { get; private set; }

    /// <summary>A director group and region, then stores in an area.</summary>
    public (Guid AreaId, List<Guid> StoreIds) SeedStores(params string[] codes)
    {
        using var db = System();
        if (Region == Guid.Empty)
        {
            var dg = new DirectorGroup { Id = Guid.NewGuid(), Name = "Group 1" };
            var region = new Region { Id = Guid.NewGuid(), Name = "Region 1", DirectorGroupId = dg.Id };
            db.Add(dg);
            db.Add(region);
            Region = region.Id;
        }
        var area = new Area { Id = Guid.NewGuid(), Name = "Area " + codes[0], RegionId = Region };
        db.Add(area);
        var ids = new List<Guid>();
        foreach (var code in codes)
        {
            var store = new Store
            {
                Id = Guid.NewGuid(), Code = code, Name = "Store " + code, Slug = code.ToLowerInvariant(), RegionId = Region,
                AreaId = area.Id, KioskToken = "token-" + code,
            };
            db.Add(store);
            ids.Add(store.Id);
        }
        db.SaveChanges();
        return (area.Id, ids);
    }

    /// <summary>An app user, signed in.</summary>
    public ScopedUser User(Tier tier, Guid? storeId = null, Guid? areaId = null)
    {
        using var db = System();
        var u = new AppUser { Id = Guid.NewGuid(), Username = $"{tier}-{Guid.NewGuid():N}"[..20], Tier = tier, StoreId = storeId, AreaId = areaId, RegionId = tier == Tier.Pa ? Region : null };
        db.Add(u);
        db.SaveChanges();
        var scoped = ScopedUser.From(u);
        SignIn(scoped);
        return scoped;
    }

    public void Add(params object[] rows)
    {
        using var db = System();
        foreach (var r in rows) db.Add(r);
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();
}
