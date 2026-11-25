// Port of bulkUploadNonOpEntries in src/app/(app)/admin/non-op-categories/actions.ts.

using Roster.Data;
using Roster.Data.Scope;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.NonOpCategories;

public sealed record NonOpRowError(int Row, string Message);

public sealed record NonOpUploadResult(bool Ok, string? Error = null, int? Accepted = null, IReadOnlyList<NonOpRowError>? RowErrors = null);

/// <summary>
/// Bulk entry upload - SUPER USER ONLY (stores record their own hours one at a
/// time, so a store can never post entries against another store, or backdate
/// a batch). Matches the client's current export: Day (DD/MM/YYYY),
/// NonOpHoursCategory, NonOpHoursSubCategory, Description, Minutes, Store ID.
/// Categories/sub-categories match by name (case-insensitive); Store ID may be
/// a bare number or a GXXX code. Partial-accept: valid rows land, rejects come
/// back with row numbers.
/// </summary>
public interface INonOpEntryUpload
{
    Task<NonOpUploadResult> BulkUploadNonOpEntriesAsync(string csvText, CancellationToken ct = default);
}

internal sealed class NonOpEntryUpload(IRosterDbContextFactory contexts, IStoreScope scope) : INonOpEntryUpload
{
    public Task<NonOpUploadResult> BulkUploadNonOpEntriesAsync(string csvText, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        _ = contexts;
        // Reads the file with lib/csv/parse (parseCsv, missingHeaders, cell, num,
        // toIsoDate), which D3 is porting to Roster.Data/Feeds.
        throw new NotImplementedException("Waits for D3's port of lib/csv/parse.");
    }
}
