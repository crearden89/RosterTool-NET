// Port of bulkUploadNonOpEntries in src/app/(app)/admin/non-op-categories/actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Feeds;
using Roster.Engine.Core;
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

internal sealed class NonOpEntryUpload(IServiceProvider services) : INonOpEntryUpload
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();

    private static readonly string[] Required = ["Day", "NonOpHoursCategory", "NonOpHoursSubCategory", "Minutes", "Store ID"];

    public async Task<NonOpUploadResult> BulkUploadNonOpEntriesAsync(string csvText, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var (headers, rows) = CsvParse.ParseCsv(csvText);
        var missing = CsvParse.MissingHeaders(headers, Required);
        if (missing.Count > 0) return new NonOpUploadResult(false, $"Missing required column(s): {string.Join(", ", missing)}");
        if (rows.Count == 0) return new NonOpUploadResult(false, "No rows found in the file.");

        await using var db = contexts.CreateSystem("Non-Op Categories: bulk upload of entries");
        var storeByCode = new Dictionary<string, Guid>();
        foreach (var s in await db.Stores.AsNoTracking().Select(s => new { s.Id, s.Code }).ToListAsync(ct))
            storeByCode[s.Code.ToUpperInvariant()] = s.Id;
        var catByName = new Dictionary<string, Guid>();
        foreach (var c in await db.NonOpCategories.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            catByName[Js.Trim(c.Name).ToLowerInvariant()] = c.Id;
        var subByCatAndName = new Dictionary<string, Guid>();
        foreach (var s in await db.NonOpSubcategories.AsNoTracking().Select(s => new { s.Id, s.CategoryId, s.Name }).ToListAsync(ct))
            subByCatAndName[$"{s.CategoryId}:{Js.Trim(s.Name).ToLowerInvariant()}"] = s.Id;

        var rowErrors = new List<NonOpRowError>();
        var inserts = new List<NonOpEntry>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNo = i + 2; // 1-based + header row
            var date = CsvParse.ToIsoDate(CsvParse.Cell(row, "Day"));
            if (date is null || !AdminParse.TryDate(date, out var day))
            {
                rowErrors.Add(new NonOpRowError(rowNo, $"Day '{CsvParse.Cell(row, "Day") ?? ""}' is not a valid DD/MM/YYYY date"));
                continue;
            }
            var storeCode = StoreIds.FormatStoreId(CsvParse.Cell(row, "Store ID") ?? "");
            if (!storeByCode.TryGetValue(storeCode.ToUpperInvariant(), out var storeId))
            {
                rowErrors.Add(new NonOpRowError(rowNo, $"Unknown store '{CsvParse.Cell(row, "Store ID") ?? ""}'"));
                continue;
            }
            var catName = Js.Trim(CsvParse.Cell(row, "NonOpHoursCategory") ?? "");
            if (!catByName.TryGetValue(catName.ToLowerInvariant(), out var catId))
            {
                rowErrors.Add(new NonOpRowError(rowNo, $"Unknown category '{catName}'"));
                continue;
            }
            var subName = Js.Trim(CsvParse.Cell(row, "NonOpHoursSubCategory") ?? "");
            if (!subByCatAndName.TryGetValue($"{catId}:{subName.ToLowerInvariant()}", out var subId))
            {
                rowErrors.Add(new NonOpRowError(rowNo, $"Unknown sub-category '{subName}' under '{catName}'"));
                continue;
            }
            var minutes = Js.Round(CsvParse.Num(row, "Minutes"));
            if (!double.IsFinite(minutes) || minutes <= 0)
            {
                rowErrors.Add(new NonOpRowError(rowNo, $"Minutes '{CsvParse.Cell(row, "Minutes") ?? ""}' must be a positive number"));
                continue;
            }
            inserts.Add(new NonOpEntry
            {
                StoreId = storeId,
                Date = day,
                SubcategoryId = subId,
                EmployeeId = null,
                Minutes = (int)minutes,
                Description = CsvParse.Cell(row, "Description") ?? "",
            });
        }

        if (inserts.Count > 0)
        {
            db.NonOpEntries.AddRange(inserts);
            var saved = await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
            if (!saved.Ok) return new NonOpUploadResult(false, saved.Error, null, rowErrors);
        }
        return new NonOpUploadResult(true, null, inserts.Count, rowErrors);
    }
}
