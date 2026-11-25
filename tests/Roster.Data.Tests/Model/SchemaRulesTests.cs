// New: checks on the SQL Server model that would otherwise surface only on a DBA's server:
// every table has an access policy, no foreign key opens a second cascade path (SQL Server
// error 1785), index keys fit SQL Server's limits, and the migrations match the model.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Model;

public class SchemaRulesTests
{
    /// <summary>The model as SQL Server sees it (no connection is opened).</summary>
    internal static RosterDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<RosterDbContext>()
            .UseRosterSqlServer("Server=localhost;Database=model_only;TrustServerCertificate=True").Options,
            AccessContext.System("model checks"));

    [Fact]
    public void Every_table_has_exactly_one_access_policy()
    {
        using var ctx = SqlServerContext();
        var tables = ctx.Model.GetEntityTypes().Where(e => !e.IsOwned()).Select(e => e.ClrType).ToHashSet();
        var policies = TablePolicies.ByEntity.Keys.ToHashSet();
        Assert.Empty(tables.Except(policies).Select(t => t.Name));
        Assert.Empty(policies.Except(tables).Select(t => t.Name));
        Assert.Equal(70, tables.Count);
    }

    [Fact]
    public void Every_table_is_filtered()
    {
        using var ctx = SqlServerContext();
        var unfiltered = ctx.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !e.GetDeclaredQueryFilters().Any())
            .Select(e => e.GetTableName()).ToList();
        Assert.Empty(unfiltered);
    }

    [Fact]
    public void No_foreign_key_opens_a_second_cascade_path()
    {
        // Conservative form of SQL Server's rule: follow every ON DELETE action other than
        // NO ACTION (cascade and set null alike) transitively; no table may be reached twice
        // from the same table, and no cycle may exist.
        using var ctx = SqlServerContext();
        var edges = ctx.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull)
            .Select(fk => (From: fk.PrincipalEntityType.GetTableName()!, To: fk.DeclaringEntityType.GetTableName()!, Name: fk.GetConstraintName()))
            .ToList();
        var problems = new List<string>();
        foreach (var root in edges.Select(e => e.From).Distinct())
        {
            var paths = new Dictionary<string, int>();
            void Walk(string table, HashSet<string> onPath)
            {
                foreach (var e in edges.Where(e => e.From == table))
                {
                    if (onPath.Contains(e.To)) { problems.Add($"cycle {root} -> {e.To}"); continue; }
                    paths[e.To] = paths.GetValueOrDefault(e.To) + 1;
                    onPath.Add(e.To);
                    Walk(e.To, onPath);
                    onPath.Remove(e.To);
                }
            }
            Walk(root, [root]);
            problems.AddRange(paths.Where(p => p.Value > 1).Select(p => $"{root} reaches {p.Key} by {p.Value} paths"));
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Index_keys_fit_sql_servers_limits()
    {
        // 900 bytes for a clustered key (the primary keys), 1,700 for a non-clustered one.
        using var ctx = SqlServerContext();
        var problems = new List<string>();
        foreach (var e in ctx.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            var table = StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
            var pk = e.FindPrimaryKey()!;
            var pkBytes = pk.Properties.Sum(p => Bytes(p, table));
            if (pkBytes > 900) problems.Add($"{table.Name} primary key: {pkBytes} bytes");
            foreach (var ix in e.GetIndexes())
            {
                var bytes = ix.Properties.Sum(p => Bytes(p, table));
                if (bytes > 1700) problems.Add($"{ix.GetDatabaseName()}: {bytes} bytes");
            }
        }
        Assert.Empty(problems);
    }

    private static int Bytes(IProperty p, StoreObjectIdentifier table)
    {
        var type = p.GetColumnType(table)!.ToLowerInvariant();
        if (type.StartsWith("nvarchar("))
        {
            var n = type["nvarchar(".Length..^1];
            if (n == "max") return int.MaxValue / 4;
            return int.Parse(n) * 2;
        }
        return type switch
        {
            "uniqueidentifier" => 16,
            "date" => 3,
            "time(0)" => 3,
            "int" => 4,
            "smallint" => 2,
            "bit" => 1,
            "float" => 8,
            "bigint" => 8,
            "datetimeoffset" => 10,
            _ => throw new InvalidOperationException($"{p.Name}: no size for {type}"),
        };
    }

    [Fact]
    public void The_migrations_match_the_model()
    {
        using var ctx = SqlServerContext();
        Assert.False(ctx.Database.HasPendingModelChanges(), "Run 'dotnet ef migrations add <Name>' for the model changes.");
        Assert.Contains("20261125062411_InitialCreate", ctx.Database.GetMigrations());
    }
}
