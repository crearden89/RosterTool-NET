// New: a throwaway SQL Server database per test class, built by the real migrations.
// Needs ROSTER_TEST_SQLSERVER (a server-level connection string, see the team's
// sqlserver.md); without it the [SqlServerFact] tests skip.
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Migrations;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlServerDatabase.ServerConnectionString))
            Skip = "Set ROSTER_TEST_SQLSERVER to run the SQL Server tests.";
    }
}

public sealed class SqlServerDatabase : IDisposable
{
    public static string? ServerConnectionString => Environment.GetEnvironmentVariable("ROSTER_TEST_SQLSERVER");

    public SqlServerDatabase(bool migrate = true)
    {
        Name = $"Roster_D_t{Guid.NewGuid():N}"[..30];
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = Name };
        ConnectionString = builder.ConnectionString;
        Options = new DbContextOptionsBuilder<RosterDbContext>().UseRosterSqlServer(ConnectionString).Options;
        Exec("master", $"CREATE DATABASE [{Name}]");
        if (migrate)
        {
            using var db = System();
            db.Database.Migrate();
        }
    }

    public string Name { get; }
    public string ConnectionString { get; }
    public DbContextOptions<RosterDbContext> Options { get; }

    public RosterDbContext As(AccessContext access) => new(Options, access);
    public RosterDbContext System() => As(AccessContext.System("sql server test"));

    public void Exec(string database, string sql)
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = database };
        using var conn = new SqlConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        return (T)cmd.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        Exec("master", $"IF DB_ID('{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
    }
}
