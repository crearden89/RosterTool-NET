// New: a real SQL Server database per test class, built by the EF Core migrations and
// dropped at the end, with a fast reset between tests.
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Infrastructure;

/// <summary>
/// A fresh SQL Server database for one test class: <c>Roster_&lt;ID&gt;_&lt;random&gt;</c>, created
/// and migrated before the class's first test and dropped after its last. Use it as
/// <c>IClassFixture&lt;SqlServerDatabaseFixture&gt;</c> on a class marked
/// <c>[Trait("Category", "SqlServer")]</c>, with <see cref="SqlServerFactAttribute"/> tests,
/// and call <see cref="ResetAsync"/> where a test needs the database as the migrations left it.
/// When no server is configured it does nothing (the tests skip).
/// </summary>
public class SqlServerDatabaseFixture : IAsyncLifetime
{
    private DatabaseReset? _reset;
    private DbContextOptions<RosterDbContext>? _options;

    /// <summary>The database's name, e.g. Roster_D4_1a2b3c4d.</summary>
    public string DatabaseName { get; } = SqlServerTestSettings.NewDatabaseName();

    /// <summary>False when no server is configured (every SQL Server test skips).</summary>
    public bool IsAvailable => SqlServerTestSettings.IsConfigured;

    /// <summary>The clock the contexts are given (the system clock unless a subclass changes it).</summary>
    public virtual TimeProvider Clock => TimeProvider.System;

    /// <summary>A connection string to this test's database.</summary>
    public string ConnectionString => SqlServerTestSettings.ConnectionStringFor(DatabaseName);

    /// <summary>The context options for this database (as the app configures SQL Server).</summary>
    public DbContextOptions<RosterDbContext> Options =>
        _options ?? throw new InvalidOperationException("The fixture is not initialised (no SQL Server configured?).");

    /// <summary>A context acting as <paramref name="access"/>.</summary>
    public RosterDbContext As(AccessContext access) => new(Options, access, Clock);

    /// <summary>A context acting as a signed-in user.</summary>
    public RosterDbContext As(ScopedUser user) => As(AccessContext.ForUser(user));

    /// <summary>A context acting as the server (test set-up and checks).</summary>
    public RosterDbContext System(string reason = "test") => As(AccessContext.System(reason));

    /// <summary>A factory over this database, for services that take one.</summary>
    public IRosterDbContextFactory Factory(AccessContext? current = null) => new FixtureContextFactory(this, current ?? AccessContext.Anonymous);

    /// <summary>The tables the reset restores to their migrated rows (the migrations' reference data).</summary>
    public IReadOnlyList<string> ReferenceTables => _reset?.RestoredTables ?? [];

    public async Task InitializeAsync()
    {
        if (!IsAvailable) return;
        await CreateDatabaseAsync();
        _options = new DbContextOptionsBuilder<RosterDbContext>()
            .UseRosterSqlServer(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options;

        await using (var db = System("test database: apply the migrations"))
            await db.Database.MigrateAsync();

        _reset = await DatabaseReset.CheckpointAsync(ConnectionString);
        await SeedAsync();
    }

    /// <summary>
    /// Data every test of the class starts from, written once after the migrations. It is NOT
    /// restored by <see cref="ResetAsync"/>, which goes back to the migrated database.
    /// </summary>
    protected virtual Task SeedAsync() => Task.CompletedTask;

    /// <summary>Delete every row the tests wrote and put back the migrations' reference rows.</summary>
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (_reset is null) throw new InvalidOperationException("The fixture is not initialised (no SQL Server configured?).");
        return _reset.ResetAsync(ConnectionString, cancellationToken);
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable) return;
        await DropDatabaseAsync(DatabaseName);
    }

    private async Task CreateDatabaseAsync()
    {
        await using var master = new SqlConnection(SqlServerTestSettings.ConnectionStringFor(SqlServerTestSettings.RequireServer(), null));
        await master.OpenAsync();
        await using var cmd = new SqlCommand($"CREATE DATABASE [{DatabaseName}];", master) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Drop one test database, closing the connections still open on it.</summary>
    public static async Task DropDatabaseAsync(string databaseName)
    {
        if (!databaseName.StartsWith("Roster_", StringComparison.Ordinal))
            throw new ArgumentException("Only test databases (Roster_<ID>_...) are dropped.", nameof(databaseName));
        // Pooled connections to the database would hold it open.
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(SqlServerTestSettings.ConnectionStringFor(SqlServerTestSettings.RequireServer(), null));
        await master.OpenAsync();
        var sql = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        await using var cmd = new SqlCommand(sql, master) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FixtureContextFactory(SqlServerDatabaseFixture fixture, AccessContext current) : IRosterDbContextFactory
    {
        public RosterDbContext Create() => fixture.As(current);

        public RosterDbContext Create(AccessContext access) => fixture.As(access);

        public RosterDbContext CreateSystem(string reason) => fixture.System(reason);
    }
}
