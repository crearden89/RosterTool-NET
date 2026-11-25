// New: the T-SQL script a DBA runs (Migrations/Sql/roster-schema.sql) parses as SQL Server
// 2022 T-SQL and builds every table.
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Roster.Data.Tests.Migrations;

public class ScriptTests
{
    internal static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Roster.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Roster.Data", "Migrations", "Sql", "roster-schema.sql");
    }

    [Fact]
    public void The_dba_script_is_valid_sql_server_2022_t_sql()
    {
        var text = File.ReadAllText(ScriptPath());
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors.Select(e => $"line {e.Line}: {e.Message}"));

        var creates = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>()
            .Select(c => c.SchemaObjectName.BaseIdentifier.Value).Where(n => n != "__ef_migrations_history").ToList();
        Assert.Equal(70, creates.Count);
        Assert.Contains("generated_shifts", creates);
        Assert.Contains("app_users", creates);
    }
}
