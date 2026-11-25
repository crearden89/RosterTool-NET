// New: where the SQL Server tests find their server. Nothing here holds a password:
// the connection string comes from the environment (see README.md in this folder).
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Roster.Data.Tests.Infrastructure;

/// <summary>
/// The SQL Server the [Trait("Category","SqlServer")] tests use. Read from the environment:
/// <list type="bullet">
/// <item><c>ROSTER_TEST_SQLSERVER</c>: a connection string to the server (its database name,
/// if any, is ignored: every test class gets a database of its own).</item>
/// <item><c>ROSTER_TEST_ID</c>: who is running the tests (an agent or developer ID, letters
/// and digits); databases are named <c>Roster_&lt;ID&gt;_&lt;random&gt;</c>. Default "Dev".</item>
/// </list>
/// </summary>
public static partial class SqlServerTestSettings
{
    public const string ConnectionVariable = "ROSTER_TEST_SQLSERVER";
    public const string IdVariable = "ROSTER_TEST_ID";

    /// <summary>The server's connection string, or null when the SQL Server tests are not configured.</summary>
    public static string? ServerConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ConnectionVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>True when a server is configured.</summary>
    public static bool IsConfigured => ServerConnectionString is not null;

    /// <summary>The ID in database names ("D4"), letters and digits only.</summary>
    public static string TestId
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(IdVariable)?.Trim();
            if (string.IsNullOrEmpty(value)) return "Dev";
            if (!IdPattern().IsMatch(value))
                throw new InvalidOperationException($"{IdVariable} must be letters and digits (at most 20), not \"{value}\".");
            return value;
        }
    }

    /// <summary>The prefix every database of this ID starts with: "Roster_D4_".</summary>
    public static string DatabasePrefix => $"Roster_{TestId}_";

    /// <summary>A fresh database name: Roster_&lt;ID&gt;_&lt;8 random hex digits&gt;.</summary>
    public static string NewDatabaseName() => $"{DatabasePrefix}{Guid.NewGuid():N}"[..(DatabasePrefix.Length + 8)];

    /// <summary>The server's connection string pointed at one database.</summary>
    public static string ConnectionStringFor(string databaseName) => ConnectionStringFor(RequireServer(), databaseName);

    /// <summary>A connection string pointed at one database (the server's master when null).</summary>
    public static string ConnectionStringFor(string serverConnectionString, string? databaseName)
    {
        var builder = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = databaseName ?? "master",
            // Each test database is dropped at the end; pooled connections to it would block that.
            Pooling = true,
        };
        if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
            builder.ApplicationName = "Roster.Data.Tests";
        return builder.ConnectionString;
    }

    /// <summary>The server's connection string, or an exception that says how to set it.</summary>
    public static string RequireServer() =>
        ServerConnectionString ?? throw new InvalidOperationException(
            $"No SQL Server for the tests: set {ConnectionVariable} to a connection string (see tests/Roster.Data.Tests/Infrastructure/README.md).");

    [GeneratedRegex("^[A-Za-z0-9]{1,20}$")]
    private static partial Regex IdPattern();
}
