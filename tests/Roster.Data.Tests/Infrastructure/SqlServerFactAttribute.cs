// New: SQL Server tests skip, rather than fail, where no server is configured.
namespace Roster.Data.Tests.Infrastructure;

/// <summary>
/// A [Fact] that needs SQL Server: skipped when <c>ROSTER_TEST_SQLSERVER</c> is not set.
/// Put <c>[Trait("Category", "SqlServer")]</c> on the class as well, so the landing check
/// (which filters that category out) never runs it.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerTestSettings.IsConfigured)
            Skip = $"SQL Server not configured: set {SqlServerTestSettings.ConnectionVariable} (see tests/Roster.Data.Tests/Infrastructure/README.md).";
    }
}

/// <summary>A [Theory] that needs SQL Server; see <see cref="SqlServerFactAttribute"/>.</summary>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (!SqlServerTestSettings.IsConfigured)
            Skip = $"SQL Server not configured: set {SqlServerTestSettings.ConnectionVariable} (see tests/Roster.Data.Tests/Infrastructure/README.md).";
    }
}
