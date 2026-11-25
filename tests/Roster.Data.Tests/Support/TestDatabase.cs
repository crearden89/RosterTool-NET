using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Access;

namespace Roster.Data.Tests.Support;

/// <summary>
/// A private in-memory SQLite database per test, created from the EF model. Every context
/// opened on it shares the one connection, so data written as one identity can be read back
/// as another. SQLite stands in for SQL Server at unit level only: the SQL Server tests in
/// tests/SqlServer run the real migrations when a server is configured.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RosterDbContext> _options;

    public TestDatabase(TimeProvider? clock = null)
    {
        Clock = clock ?? TimeProvider.System;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<RosterDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;
        using var db = System();
        db.Database.EnsureCreated();
    }

    public TimeProvider Clock { get; }

    public DbContextOptions<RosterDbContext> Options => _options;

    public RosterDbContext As(AccessContext access) => new(_options, access, Clock);

    public RosterDbContext As(Roster.Data.Access.ScopedUser user) => As(AccessContext.ForUser(user));

    public RosterDbContext System() => As(AccessContext.System("test setup"));

    public void Dispose() => _connection.Dispose();
}
