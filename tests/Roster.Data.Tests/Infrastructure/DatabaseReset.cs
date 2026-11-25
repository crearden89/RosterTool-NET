// New: puts a test database back to the state the migrations left it in, in one batch,
// without dropping it: much faster than a new database per test.
using System.Text;
using Microsoft.Data.SqlClient;

namespace Roster.Data.Tests.Infrastructure;

/// <summary>
/// Resets every table of a database to a checkpoint taken right after the migrations ran.
/// <para>
/// At the checkpoint, every table that holds rows (the reference data the migrations insert:
/// positions, load types, the non-op and admin-task catalogues, the single-row settings) is
/// copied into the <c>reset_checkpoint</c> schema. A reset then deletes every row of every
/// table, children before parents, and copies the checkpoint rows back, parents first, in
/// one transaction. Foreign keys that form a cycle between tables are switched off for the
/// length of the batch and checked again at its end.
/// </para>
/// </summary>
public sealed class DatabaseReset
{
    public const string CheckpointSchema = "reset_checkpoint";

    private static readonly string[] SkippedTables = ["__ef_migrations_history", "__EFMigrationsHistory"];

    private readonly string _batch;

    private DatabaseReset(string batch, IReadOnlyList<string> deleteOrder, IReadOnlyList<string> restored)
    {
        _batch = batch;
        DeleteOrder = deleteOrder;
        RestoredTables = restored;
    }

    /// <summary>Every table, in the order the reset deletes from them ([schema].[table]).</summary>
    public IReadOnlyList<string> DeleteOrder { get; }

    /// <summary>The tables the checkpoint holds rows for, in the order they are restored.</summary>
    public IReadOnlyList<string> RestoredTables { get; }

    /// <summary>The T-SQL batch a reset runs.</summary>
    public string Batch => _batch;

    /// <summary>
    /// Take the checkpoint of a freshly migrated database and prepare the reset batch.
    /// Call once, before any test writes to the database.
    /// </summary>
    public static async Task<DatabaseReset> CheckpointAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = await ReadTablesAsync(connection, cancellationToken);
        var keys = await ReadForeignKeysAsync(connection, cancellationToken);
        var (deleteOrder, cyclic) = Order(tables.Select(t => t.Name).ToList(), keys);

        // Copy the tables that hold rows into the checkpoint schema.
        await ExecAsync(connection,
            $"IF SCHEMA_ID(N'{CheckpointSchema}') IS NULL EXEC(N'CREATE SCHEMA [{CheckpointSchema}]');", cancellationToken);
        var restored = new List<TableInfo>();
        foreach (var name in deleteOrder.AsEnumerable().Reverse()) // parents first
        {
            var table = tables.Single(t => t.Name == name);
            if (!await HasRowsAsync(connection, table, cancellationToken)) continue;
            var copy = $"[{CheckpointSchema}].[{table.Schema}__{table.Table}]";
            var columns = string.Join(", ", table.InsertColumns.Select(Quote));
            await ExecAsync(connection, $"SELECT {columns} INTO {copy} FROM {table.Name};", cancellationToken);
            restored.Add(table);
        }

        var batch = new StringBuilder();
        batch.AppendLine("SET NOCOUNT ON; SET XACT_ABORT ON;");
        batch.AppendLine("BEGIN TRANSACTION;");
        foreach (var fk in cyclic)
            batch.AppendLine($"ALTER TABLE {fk.Parent} NOCHECK CONSTRAINT {Quote(fk.Name)};");
        foreach (var name in deleteOrder)
            batch.AppendLine($"DELETE FROM {name};");
        foreach (var table in restored)
        {
            var columns = string.Join(", ", table.InsertColumns.Select(Quote));
            var copy = $"[{CheckpointSchema}].[{table.Schema}__{table.Table}]";
            if (table.HasIdentity) batch.AppendLine($"SET IDENTITY_INSERT {table.Name} ON;");
            batch.AppendLine($"INSERT INTO {table.Name} ({columns}) SELECT {columns} FROM {copy};");
            if (table.HasIdentity) batch.AppendLine($"SET IDENTITY_INSERT {table.Name} OFF;");
        }
        foreach (var fk in cyclic)
            batch.AppendLine($"ALTER TABLE {fk.Parent} WITH CHECK CHECK CONSTRAINT {Quote(fk.Name)};");
        batch.AppendLine("COMMIT TRANSACTION;");

        return new DatabaseReset(batch.ToString(), deleteOrder, restored.Select(t => t.Name).ToList());
    }

    /// <summary>Run the reset.</summary>
    public async Task ResetAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecAsync(connection, _batch, cancellationToken);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    private sealed record TableInfo(string Schema, string Table, IReadOnlyList<string> InsertColumns, bool HasIdentity)
    {
        public string Name => $"{Quote(Schema)}.{Quote(Table)}";
    }

    private sealed record ForeignKey(string Name, string Parent, string Referenced);

    private static async Task<List<TableInfo>> ReadTablesAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, t.name, c.name, c.is_identity, c.is_computed, ty.name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0 AND s.name <> @checkpoint
            ORDER BY s.name, t.name, c.column_id;
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@checkpoint", CheckpointSchema);
        var columns = new List<(string Schema, string Table, string Column, bool Identity, bool Computed, string Type)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetString(5)));
        }

        return columns
            .GroupBy(c => (c.Schema, c.Table))
            .Where(g => !SkippedTables.Contains(g.Key.Table, StringComparer.OrdinalIgnoreCase))
            .Select(g => new TableInfo(
                g.Key.Schema,
                g.Key.Table,
                // Computed and rowversion columns are filled in by the server.
                g.Where(c => !c.Computed && c.Type is not ("timestamp" or "rowversion")).Select(c => c.Column).ToList(),
                g.Any(c => c.Identity)))
            .ToList();
    }

    private static async Task<List<ForeignKey>> ReadForeignKeysAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT fk.name,
                   QUOTENAME(SCHEMA_NAME(p.schema_id)) + '.' + QUOTENAME(p.name),
                   QUOTENAME(SCHEMA_NAME(r.schema_id)) + '.' + QUOTENAME(r.name)
            FROM sys.foreign_keys fk
            JOIN sys.tables p ON p.object_id = fk.parent_object_id
            JOIN sys.tables r ON r.object_id = fk.referenced_object_id;
            """;
        await using var cmd = new SqlCommand(sql, connection);
        var keys = new List<ForeignKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(new ForeignKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return keys;
    }

    /// <summary>
    /// Children before parents (a table is deleted from only once every table referencing it
    /// has been), alphabetical among equals so the batch is the same every time. Foreign keys
    /// left over on a cycle are returned, to be switched off during the reset.
    /// </summary>
    internal static (List<string> DeleteOrder, List<(string Name, string Parent)> Cyclic) Order(
        IReadOnlyList<string> tables, IReadOnlyList<(string Name, string Parent, string Referenced)> keys)
    {
        var known = new HashSet<string>(tables, StringComparer.Ordinal);
        var edges = keys.Where(k => k.Parent != k.Referenced && known.Contains(k.Parent) && known.Contains(k.Referenced)).ToList();
        var remaining = new SortedSet<string>(tables, StringComparer.Ordinal);
        var order = new List<string>();
        while (remaining.Count > 0)
        {
            // A table can go when no remaining table references it.
            var next = remaining.FirstOrDefault(t => !edges.Any(e => e.Referenced == t && e.Parent != t && remaining.Contains(e.Parent)));
            if (next is null) break;
            order.Add(next);
            remaining.Remove(next);
        }

        var cyclic = new List<(string Name, string Parent)>();
        if (remaining.Count > 0)
        {
            // A cycle: delete the rest with their foreign keys among themselves switched off.
            cyclic.AddRange(edges.Where(e => remaining.Contains(e.Parent) && remaining.Contains(e.Referenced))
                .Select(e => (e.Name, e.Parent)));
            order.AddRange(remaining);
        }
        return (order, cyclic);
    }

    private static (List<string>, List<(string Name, string Parent)>) Order(List<string> tables, List<ForeignKey> keys) =>
        Order(tables, keys.Select(k => (k.Name, k.Parent, k.Referenced)).ToList());

    private static async Task<bool> HasRowsAsync(SqlConnection connection, TableInfo table, CancellationToken ct)
    {
        await using var cmd = new SqlCommand($"SELECT CASE WHEN EXISTS (SELECT 1 FROM {table.Name}) THEN 1 ELSE 0 END;", connection);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string name) => $"[{name.Replace("]", "]]")}]";
}
