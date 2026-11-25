// New: a throwaway SQL Server database for the data services' SqlServer tests
// (Roster_D2_services, created from D's migrations), until D4's fixtures land.
// The connection string comes from ConnectionStrings__Roster (its database name is
// replaced); never from a committed file.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Services;

public sealed class SqlServerDatabase : IAsyncLifetime
{
    public DbContextOptions<RosterDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Roster")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Roster to run the SqlServer tests.");
        var builder = new SqlConnectionStringBuilder(cs) { InitialCatalog = "Roster_D2_services" };
        Options = new DbContextOptionsBuilder<RosterDbContext>().UseRosterSqlServer(builder.ConnectionString).Options;
        await using var db = System();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public RosterDbContext As(AccessContext access) => new(Options, access);

    public RosterDbContext System() => As(AccessContext.System("SqlServer test setup"));
}

/// <summary>Contexts over the SQL Server test database, acting as <see cref="Current"/>.</summary>
internal sealed class SqlServerContexts(SqlServerDatabase db) : IRosterDbContextFactory, IAccessContextAccessor
{
    public AccessContext Current { get; set; } = AccessContext.System("test caller");

    public RosterDbContext Create() => db.As(Current);

    public RosterDbContext Create(AccessContext access) => db.As(access);

    public RosterDbContext CreateSystem(string reason) => db.As(AccessContext.System(reason));

    public StoreScope Scope() => new(this, this);
}
