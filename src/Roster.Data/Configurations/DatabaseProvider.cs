namespace Roster.Data.Configurations;

/// <summary>
/// The database the model is being built for. SQL Server is the product; SQLite only
/// backs the unit-level tests, so a handful of things that have no SQLite equivalent
/// (T-SQL default expressions, persisted computed columns, ISJSON checks) are emitted
/// for SQL Server only and replaced by a SQLite-safe form or left out there.
/// </summary>
public enum DatabaseProvider
{
    SqlServer,
    Sqlite,
}

internal static class DatabaseProviderExtensions
{
    public static DatabaseProvider FromProviderName(string? providerName) =>
        providerName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => DatabaseProvider.Sqlite,
            _ => DatabaseProvider.SqlServer,
        };
}

/// <summary>
/// Small SQL fragments that differ between SQL Server and SQLite, used by check
/// constraints, defaults and computed columns. Identifiers are written in [brackets],
/// which both databases accept.
/// </summary>
internal sealed class SqlDialect(DatabaseProvider provider)
{
    public DatabaseProvider Provider { get; } = provider;
    public bool IsSqlServer => Provider == DatabaseProvider.SqlServer;

    /// <summary>Character length of a column (Postgres length()).</summary>
    public string Len(string column) => IsSqlServer ? $"LEN([{column}])" : $"length([{column}])";

    /// <summary>Length after trimming spaces (Postgres length(btrim(x))).</summary>
    public string TrimmedLen(string column) =>
        IsSqlServer ? $"LEN(LTRIM(RTRIM([{column}])))" : $"length(trim([{column}]))";

    /// <summary>The current timestamp with offset (Postgres now()).</summary>
    public string? Now => IsSqlServer ? "SYSDATETIMEOFFSET()" : null;

    /// <summary>A database-generated uuid (Postgres gen_random_uuid()).</summary>
    public string? NewGuid => IsSqlServer ? "NEWSEQUENTIALID()" : null;

    /// <summary>32 lower-case hex characters (Postgres md5(gen_random_uuid()::text)).</summary>
    public string? RandomHex32 =>
        IsSqlServer ? "LOWER(CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2))" : null;

    /// <summary>64 lower-case hex characters (two md5 digests, as feed_tokens did).</summary>
    public string? RandomHex64 =>
        IsSqlServer
            ? "LOWER(CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2) + CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2))"
            : null;

    /// <summary>A Unicode string literal.</summary>
    public string Text(string value) => IsSqlServer ? $"N'{value.Replace("'", "''")}'" : $"'{value.Replace("'", "''")}'";

    /// <summary>CHECK that a column holds valid JSON (null allowed).</summary>
    public string IsJson(string column) =>
        IsSqlServer ? $"[{column}] IS NULL OR ISJSON([{column}]) = 1" : $"[{column}] IS NULL OR json_valid([{column}])";

    /// <summary>CHECK that a time column sits on a 15-minute boundary with no seconds.</summary>
    public string QuarterHour(string column) =>
        IsSqlServer
            ? $"DATEPART(MINUTE, [{column}]) % 15 = 0 AND DATEPART(SECOND, [{column}]) = 0"
            : $"CAST(substr([{column}], 4, 2) AS INTEGER) % 15 = 0 AND substr([{column}], 7, 2) = '00'";

    /// <summary>
    /// CHECK that a date is a Monday (Postgres extract(isodow from d) = 1). Written
    /// without DATEPART(weekday), which depends on the session's SET DATEFIRST.
    /// 1900-01-01 was a Monday.
    /// </summary>
    public string IsMonday(string column) =>
        IsSqlServer
            ? $"DATEDIFF(DAY, CONVERT(date, '19000101', 112), [{column}]) % 7 = 0"
            : $"CAST(strftime('%w', [{column}]) AS INTEGER) = 1";

    /// <summary>IN list of string literals.</summary>
    public string In(string column, params string[] values) =>
        $"[{column}] IN ({string.Join(", ", values.Select(Text))})";

    /// <summary>The ÷0.9 planning transform as a stored computed column (IEEE double division).</summary>
    public string Over90(string column) => $"[{column}] / 0.9";
}
