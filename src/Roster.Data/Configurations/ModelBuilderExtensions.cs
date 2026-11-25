using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Roster.Data.Configurations;

internal static class ModelBuilderExtensions
{
    /// <summary>Marks a timestamp that the context stamps on insert when the code left it unset.</summary>
    public const string StampOnInsert = "Roster:StampOnInsert";
    /// <summary>Marks a timestamp that the context also restamps on every update.</summary>
    public const string StampOnUpdate = "Roster:StampOnUpdate";

    /// <summary>
    /// A column default the DDL carries (for a DBA's own inserts) while EF Core always sends
    /// the value the code holds. Without ValueGeneratedNever a CLR default such as false would
    /// be left out of the INSERT and the database default (perhaps true) would win.
    /// </summary>
    public static PropertyBuilder<T> Default<T>(this PropertyBuilder<T> p, T value) =>
        p.HasDefaultValue(value).ValueGeneratedNever();

    /// <summary>A SQL default expression (SQL Server only; null on SQLite), always sent by EF.</summary>
    public static PropertyBuilder<T> DefaultSql<T>(this PropertyBuilder<T> p, string? sql)
    {
        if (sql is not null) p.HasDefaultValueSql(sql);
        return p.ValueGeneratedNever();
    }

    /// <summary>A timestamp defaulting to now(): stamped by the context on insert, defaulted in the DDL.</summary>
    public static PropertyBuilder<DateTimeOffset> Now(this PropertyBuilder<DateTimeOffset> p, SqlDialect d, bool alsoOnUpdate = false)
    {
        p.DefaultSql(d.Now).HasAnnotation(StampOnInsert, true);
        if (alsoOnUpdate) p.HasAnnotation(StampOnUpdate, true);
        return p;
    }

    /// <summary>
    /// A uuid key defaulting to a database-generated value (gen_random_uuid() in Postgres,
    /// NEWSEQUENTIALID() here), while EF Core still assigns sequential GUIDs client-side so
    /// batched inserts need no round trip for the key.
    /// </summary>
    public static PropertyBuilder<Guid> GuidKey(this PropertyBuilder<Guid> p, SqlDialect d)
    {
        if (d.NewGuid is not null) p.HasDefaultValueSql(d.NewGuid);
        return p.ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();
    }

    /// <summary>A clock time to the second (Postgres time): time(0) on SQL Server.</summary>
    public static PropertyBuilder<TimeOnly> Time(this PropertyBuilder<TimeOnly> p, SqlDialect d) =>
        d.IsSqlServer ? p.HasColumnType("time(0)") : p;

    public static PropertyBuilder<TimeOnly?> Time(this PropertyBuilder<TimeOnly?> p, SqlDialect d) =>
        d.IsSqlServer ? p.HasColumnType("time(0)") : p;

    /// <summary>nvarchar(n) when bounded (key and indexed columns), nvarchar(max) otherwise.</summary>
    public static PropertyBuilder<T> Text<T>(this PropertyBuilder<T> p, int? maxLength = null) =>
        maxLength is null ? p : p.HasMaxLength(maxLength.Value);

    /// <summary>Column type for a JSON document (nvarchar(max) holding JSON).</summary>
    public static PropertyBuilder<T> Json<T>(this PropertyBuilder<T> p, SqlDialect d) =>
        d.IsSqlServer ? p.HasColumnType("nvarchar(max)") : p;
}
