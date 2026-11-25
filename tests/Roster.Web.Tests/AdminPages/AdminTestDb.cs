// New: a private in-memory SQLite database on the data layer's model, with the Super
// User page services' dependencies wired as the web app wires them: contexts for the
// signed-in person or the system, and the store scope over them.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Web.Tests.AdminPages;

public sealed class AdminTestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public AdminTestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<RosterDbContext>().UseSqlite(_connection).Options;
        using (var db = System()) db.Database.EnsureCreated();
        Accessor.Current = AccessContext.ForUser(SuperUser);
        Contexts = new Factory(Options, Accessor);
        Scope = new StoreScope(Accessor, Contexts);
    }

    public DbContextOptions<RosterDbContext> Options { get; }
    public AccessContextAccessor Accessor { get; } = new();
    public IRosterDbContextFactory Contexts { get; }
    public IStoreScope Scope { get; }
    public TimeProvider Clock { get; } = TimeProvider.System;

    public ScopedUser SuperUser { get; } = new(Guid.NewGuid(), "casey.admin", Tier.SuperUser, null, null, null, null);

    public RosterDbContext System() => new(Options, AccessContext.System("test setup"));

    public void SignInAs(ScopedUser user) => Accessor.Current = AccessContext.ForUser(user);

    /// <summary>A director group, a region and the given stores (codes), returned in that order.</summary>
    public (Guid RegionId, Guid[] StoreIds) Organisation(params string[] codes)
    {
        using var db = System();
        var group = new DirectorGroup { Id = Guid.NewGuid(), Name = "North Group" };
        var region = new Region { Id = Guid.NewGuid(), Name = "501 - Riverside", DirectorGroupId = group.Id };
        db.DirectorGroups.Add(group);
        db.Regions.Add(region);
        var ids = new List<Guid>();
        foreach (var code in codes)
        {
            var s = new Store
            {
                Id = Guid.NewGuid(), Code = code, Name = $"Store {code}", Slug = code.ToLowerInvariant(),
                RegionId = region.Id, State = "NSW", KioskToken = Guid.NewGuid().ToString("N"),
            };
            db.Stores.Add(s);
            ids.Add(s.Id);
        }
        db.SaveChanges();
        return (region.Id, ids.ToArray());
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(DbContextOptions<RosterDbContext> options, IAccessContextAccessor accessor) : IRosterDbContextFactory
    {
        public RosterDbContext Create() => new(options, accessor.Current);

        public RosterDbContext Create(AccessContext access) => new(options, access);

        public RosterDbContext CreateSystem(string reason) => new(options, AccessContext.System(reason));
    }
}
