// Port of src/app/(app)/plan/non-op/page.tsx (the reads) and actions.ts
// (addNonOpEntry, deleteNonOpEntry, clearNonOpEntriesForDate).

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;

namespace Roster.Web.Features.NonOp;

public sealed class NonOpService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IStoreDirectory directory,
    IRememberedStore remembered,
    TimeProvider clock) : INonOpService
{
    public async Task<NonOpPageData> LoadAsync(Guid? store, DateOnly? start, CancellationToken ct = default)
    {
        var user = scope.User;
        var today = Dates.TodayAu(clock.GetUtcNow());

        // Non-Op is a PEER-VISIBLE page (deliberately - stores learn from each
        // other's non-op planning): every login can switch to any store. Only the
        // access matrix decides who can EDIT the selected store's entries.
        var stores = await directory.ListAsync(ct);
        var storeId = user?.StoreId is { } own && store is null
            ? own
            : StoreScope.ResolveStoreId(store, stores.Select(s => s.Id).ToList(), remembered.StoreId);
        var storeRow = stores.FirstOrDefault(s => s.Id == storeId);
        StoreAccess? access = user is null ? null : AccessMatrix.StoreAccessFor(user, storeId ?? Guid.Empty, storeRow?.AreaId);

        // Peer reads go through a system context - the store filters rightly hide other
        // stores' rows from a store-tier login, and this page is the deliberate gate.
        await using var db = access == StoreAccess.Peer ? contexts.CreateSystem("non-op: the peer-visible page") : contexts.Create();
        await using var userDb = contexts.Create();

        var startDate = start;
        if (storeId is { } sid && startDate is null)
        {
            startDate = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == sid)
                .OrderBy(b => b.Date).Select(b => (DateOnly?)b.Date).FirstOrDefaultAsync(ct);
        }
        // No store at all: today's fortnight, rather than no dates.
        var from = startDate ?? today;
        // The fourteen days, counted on the calendar (not the clock).
        var dates = Enumerable.Range(0, 14).Select(i => Period.AddDaysIso(from, i)).ToList();

        var categories = await userDb.NonOpCategories.AsNoTracking().OrderBy(c => c.Sort)
            .Select(c => new NonOpCategoryRow(c.Id, c.Name)).ToListAsync(ct);
        // No ORDER BY in the TypeScript (the database's own order); the catalogue
        // page's order, by name, is used here.
        var subs = (await userDb.NonOpSubcategories.AsNoTracking().OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct))
            .Select(s => new NonOpSub(s.Id, s.CategoryId, s.Name, s.MinMinutes, s.MaxMinutes, s.DefaultMinutes, s.Note, s.DaysAllowed))
            .ToList();
        List<NonOpEmployee> employees = [];
        List<NonOpEntryRow> entries = [];
        if (storeId is { } id)
        {
            employees = await db.Employees.AsNoTracking().Where(e => e.StoreId == id && e.Active)
                .OrderBy(e => e.Name).ThenBy(e => e.Id)
                .Select(e => new NonOpEmployee(e.Id, e.Name)).ToListAsync(ct);
            entries = await db.NonOpEntries.AsNoTracking().Where(e => e.StoreId == id && dates.Contains(e.Date))
                .OrderBy(e => e.Date).ThenBy(e => e.CreatedAt).ThenBy(e => e.Id)
                .Select(e => new NonOpEntryRow(e.Id, e.Date, e.SubcategoryId, e.EmployeeId, e.Minutes, e.Description ?? ""))
                .ToListAsync(ct);
        }

        return new NonOpPageData(
            StoreId: storeId,
            StoreName: storeRow?.Name ?? "",
            Stores: stores.Select(s => new NonOpStore(s.Id, s.Code, s.Name)).ToList(),
            StartDate: from,
            Dates: dates,
            Categories: categories,
            Subs: subs,
            Employees: employees,
            Entries: entries,
            ReadOnly: access != StoreAccess.Write,
            Peer: access == StoreAccess.Peer);
    }

    // Stores record their Non-Op hours one entry at a time (bulk upload is a
    // Super User action - see Features/Admin/NonOpCategories).
    public async Task<NonOpResult> AddNonOpEntryAsync(AddNonOpEntryInput input, CancellationToken ct = default)
    {
        var denied = await scope.DenyStoreWriteAsync(input.StoreId, ct);
        if (denied is not null) return new NonOpResult(false, denied);
        if (Js.Trim(input.Description) == "") return new NonOpResult(false, "Description is required");
        if (input.EmployeeId is null) return new NonOpResult(false, "A team member is required");
        // The sheet's Save waits for an item; the TypeScript left an empty one to the database to refuse.
        if (input.SubcategoryId is not { } subId) return new NonOpResult(false, "An item is required");

        await using var db = contexts.CreateSystem("non-op entry, after the store-write check");
        db.NonOpEntries.Add(new NonOpEntry
        {
            StoreId = input.StoreId,
            Date = input.Date,
            SubcategoryId = subId,
            EmployeeId = input.EmployeeId,
            Minutes = input.Minutes,
            Description = input.Description,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            return new NonOpResult(false, ex.InnerException?.Message ?? ex.Message);
        }
        return NonOpResult.Success;
    }

    public async Task<NonOpResult> DeleteNonOpEntryAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contexts.CreateSystem("non-op entry removal, after the store-write check");
        // Row-id keyed: resolve the owning store before deleting so the write is
        // scope-checked like every other mutation.
        var row = await db.NonOpEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        var denied = await scope.DenyStoreWriteAsync(row?.StoreId, ct);
        if (denied is not null) return new NonOpResult(false, denied);
        db.NonOpEntries.Remove(row!);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            return new NonOpResult(false, ex.InnerException?.Message ?? ex.Message);
        }
        return NonOpResult.Success;
    }

    public async Task<NonOpResult> ClearNonOpEntriesForDateAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var denied = await scope.DenyStoreWriteAsync(storeId, ct);
        if (denied is not null) return new NonOpResult(false, denied);
        await using var db = contexts.CreateSystem("non-op day cleared, after the store-write check");
        try
        {
            await db.NonOpEntries.Where(e => e.StoreId == storeId && e.Date == date).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            return new NonOpResult(false, ex.InnerException?.Message ?? ex.Message);
        }
        return NonOpResult.Success;
    }
}
