using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roster.Data.Feeds;

// Row-level upload validation report (src/lib/validation.ts). Every ingest surfaces a
// clear, row-level error report rather than a silent partial import or a generic failure.

/// <summary>One rejected row. Row is the 1-based data line (the header is not counted).</summary>
public sealed record RowError(
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("storeCode")] string? StoreCode,
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("message")] string Message)
{
    public RowError(int row, string message) : this(row, null, null, message) { }
}

/// <summary>The outcome of validating one file: what can load, and why the rest cannot.</summary>
public sealed class ValidationResult<T>
{
    public int TotalRows { get; init; }
    public IReadOnlyList<T> ValidRows { get; init; } = [];
    public IReadOnlyList<RowError> Errors { get; init; } = [];
    /// <summary>File-level errors (missing columns, wrong day span…). These always block.</summary>
    public IReadOnlyList<string> FileErrors { get; init; } = [];
    /// <summary>Rows that exactly repeated another row of the file and were kept once.</summary>
    public int DuplicatesCollapsed { get; init; }
}

/// <summary>Every parsed row carries its source line for traceability (TypeScript __row).</summary>
public interface IFeedRow
{
    int Row { get; }
    string StoreCode { get; }
}

internal static class FeedJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
