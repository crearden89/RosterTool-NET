// New: an in-memory SQLite database for the rules and process services' tests, opened
// as a signed-in user (Roster.Data.Tests' TestDatabase does the same for the data layer).

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Web.Tests.Rules;

/// <summary>A clock that always reads the same instant.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class RulesTestDatabase : IRosterDbContextFactory, IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RosterDbContext> _options;

    public RulesTestDatabase(Tier tier = Tier.SuperUser)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<RosterDbContext>().UseSqlite(_connection).Options;
        using (var db = CreateSystem("test setup")) db.Database.EnsureCreated();
        Accessor.Current = AccessContext.ForUser(new ScopedUser(Guid.NewGuid(), "test.user", tier, null, null, null, null));
        Scope = new StoreScope(Accessor, this);
    }

    public FixedClock Clock { get; } = new(Now);

    public AccessContextAccessor Accessor { get; } = new();

    /// <summary>The store scope of the signed-in test user.</summary>
    public IStoreScope Scope { get; }

    public RosterDbContext Create() => new(_options, Accessor.Current, Clock);

    public RosterDbContext Create(AccessContext access) => new(_options, access, Clock);

    public RosterDbContext CreateSystem(string reason) => new(_options, AccessContext.System(reason), Clock);

    /// <summary>Writes rows as the test setup (no user checks).</summary>
    public void Seed(Action<RosterDbContext> add)
    {
        using var db = CreateSystem("test setup");
        add(db);
        db.SaveChanges();
    }

    /// <summary>Reads as the test setup.</summary>
    public T Read<T>(Func<RosterDbContext, T> read)
    {
        using var db = CreateSystem("test read");
        return read(db);
    }

    public void Dispose() => _connection.Dispose();
}
