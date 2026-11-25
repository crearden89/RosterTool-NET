// Port of src/lib/validation.ts.
// Row-level upload validation report (spec 4.4). Every ingest surfaces a clear,
// row-level error report rather than a silent partial import or generic failure.

using System.Text.Json.Serialization;

namespace Roster.Data.Feeds;

/// <summary>
/// One rejected row. <paramref name="Row"/> is the 1-based line number in the file (excluding
/// the header); 0 names a whole-upload failure. Serialises as the TypeScript app's JSON
/// (<c>{"row":2,"storeCode":"G001","field":"Date","message":"..."}</c>), leaving out a
/// store code or field the error does not have.
/// </summary>
public sealed record RowError(
    [property: JsonPropertyName("row"), JsonPropertyOrder(0)] int Row,
    [property: JsonPropertyName("storeCode"), JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StoreCode,
    [property: JsonPropertyName("field"), JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field,
    [property: JsonPropertyName("message"), JsonPropertyOrder(3)] string Message);

/// <param name="TotalRows">Data rows in the file.</param>
/// <param name="ValidRows">Rows that can load.</param>
/// <param name="Errors">Row errors, in file order.</param>
/// <param name="FileErrors">File-level errors (missing required columns, wrong day-span, etc.).</param>
/// <param name="DuplicatesCollapsed">Rows that exactly repeated another row of the file and were kept once.</param>
public sealed record ValidationResult<T>(
    int TotalRows,
    IReadOnlyList<T> ValidRows,
    IReadOnlyList<RowError> Errors,
    IReadOnlyList<string> FileErrors,
    int? DuplicatesCollapsed = null);

/// <summary>
/// A parsed and validated row carries its original file line for traceability
/// (<c>WithRowNo&lt;T&gt;</c>, the TypeScript <c>__row</c>).
/// </summary>
public interface IFeedRow
{
    /// <summary>The row's line in the file (<c>__row</c>).</summary>
    int Row { get; }

    string? StoreCode { get; }
}
