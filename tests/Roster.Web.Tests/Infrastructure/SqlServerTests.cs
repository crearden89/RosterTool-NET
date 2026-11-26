// New: the web tests' SQL Server switch, read from the environment as the data tests' is
// (tests/Roster.Data.Tests/Infrastructure/README.md): ROSTER_TEST_SQLSERVER names the server,
// ROSTER_TEST_ID starts the throwaway databases' names. Without a server the tests skip.

using Microsoft.Data.SqlClient;

namespace Roster.Web.Tests.Infrastructure;

public static class SqlServerTests
{
    public const string ConnectionVariable = "ROSTER_TEST_SQLSERVER";

    /// <summary>The server's connection string, or null when the SQL Server tests are not configured.</summary>
    public static string? Server =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>A connection string to a new database name, Roster_&lt;ID&gt;_&lt;8 hex digits&gt;, on that server.</summary>
    public static string NewDatabase()
    {
        var server = Server ?? throw new InvalidOperationException($"{ConnectionVariable} is not set.");
        var id = Environment.GetEnvironmentVariable("ROSTER_TEST_ID")?.Trim() is { Length: > 0 } given ? given : "Dev";
        return new SqlConnectionStringBuilder(server) { InitialCatalog = $"Roster_{id}_{Guid.NewGuid():N}"[..(8 + id.Length + 8)] }.ConnectionString;
    }
}

/// <summary>A [Fact] that needs SQL Server: skipped when ROSTER_TEST_SQLSERVER is not set. Put [Trait("Category", "SqlServer")] on the class too.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (SqlServerTests.Server is null)
            Skip = $"SQL Server not configured: set {SqlServerTests.ConnectionVariable} (see tests/Roster.Data.Tests/Infrastructure/README.md).";
    }
}
