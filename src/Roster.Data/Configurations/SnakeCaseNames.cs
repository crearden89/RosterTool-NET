using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Roster.Data.Configurations;

/// <summary>
/// Keeps the SQL Server schema in the same snake_case names the Postgres schema used
/// (tables, columns, keys, indexes, foreign keys), so a DBA can lay the two side by
/// side and every query in the TypeScript app reads the same against either.
/// Names set explicitly in the configuration are left alone.
/// </summary>
internal static class SnakeCaseNames
{
    public static string ToSnake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                // Split before an upper-case letter that follows a lower-case letter or a
                // digit, or that starts a new word after an acronym ("UKGId" -> "ukg_id").
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Apply snake_case to every name the model did not set explicitly.</summary>
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.IsOwned()) continue;
            var table = entity.GetTableName() ?? ToSnake(entity.ClrType.Name);
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());

            foreach (var property in entity.GetProperties())
            {
                if (property.FindAnnotation(Microsoft.EntityFrameworkCore.Metadata.RelationalAnnotationNames.ColumnName) is null)
                    property.SetColumnName(ToSnake(property.Name));
            }

            // Postgres's own naming: <table>_pkey, <table>_<cols>_key, <table>_<cols>_fkey.
            var pk = entity.FindPrimaryKey();
            if (pk is not null && pk.FindAnnotation(Microsoft.EntityFrameworkCore.Metadata.RelationalAnnotationNames.Name) is null)
                pk.SetName($"{table}_pkey");

            foreach (var key in entity.GetKeys().Where(k => !k.IsPrimaryKey()))
            {
                if (key.FindAnnotation(Microsoft.EntityFrameworkCore.Metadata.RelationalAnnotationNames.Name) is null)
                    key.SetName($"{table}_{string.Join("_", key.Properties.Select(p => p.GetColumnName(store)))}_key");
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.FindAnnotation(Microsoft.EntityFrameworkCore.Metadata.RelationalAnnotationNames.Name) is null)
                {
                    var cols = string.Join("_", fk.Properties.Select(p => p.GetColumnName(store)));
                    fk.SetConstraintName($"{table}_{cols}_fkey");
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.FindAnnotation(Microsoft.EntityFrameworkCore.Metadata.RelationalAnnotationNames.Name) is null)
                {
                    var cols = string.Join("_", index.Properties.Select(p => p.GetColumnName(store)));
                    index.SetDatabaseName($"{table}_{cols}_{(index.IsUnique ? "key" : "idx")}");
                }
            }
        }
    }
}
