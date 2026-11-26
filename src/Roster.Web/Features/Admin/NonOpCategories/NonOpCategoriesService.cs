// Port of src/app/(app)/admin/non-op-categories/page.tsx (its reads) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.NonOpCategories;

public sealed record NonOpCategoryRow(Guid Id, string Name, int Sort);

public sealed record NonOpSubRow(
    Guid Id,
    Guid CategoryId,
    string Name,
    int? MinMinutes,
    int? MaxMinutes,
    int? DefaultMinutes,
    string? Note,
    IReadOnlyList<int> DaysAllowed);

public sealed record NonOpCategoriesView(IReadOnlyList<NonOpCategoryRow> Categories, IReadOnlyList<NonOpSubRow> Subs);

// Sub-category time rules mirror the client's catalogue sheet: a DEFAULT time
// (pre-filled when a store adds the entry), an optional MAXIMUM, and - for
// items with no fixed time - a note naming who approves it ("As approved by
// EMSO"). Blank default/max mean "no fixed time", which is why both are
// nullable rather than defaulted to a number.
public sealed record SubcategoryInput(
    string Name, double? DefaultMinutes, double? MaxMinutes, string? Note, IReadOnlyList<int>? DaysAllowed = null);

/// <summary>
/// A delete refused because recorded entries depend on the row reports HOW MANY
/// (<see cref="BlockedBy"/>), so the screen can offer an explicit "delete anyway"
/// instead of a dead end. Never forced implicitly - the caller has to ask twice.
/// </summary>
public sealed record DeleteResult(bool Ok, string? Error = null, int? BlockedBy = null);

public interface INonOpCategoriesService
{
    /// <summary>The categories in their sort order and every sub-category by name.</summary>
    Task<NonOpCategoriesView> LoadAsync(CancellationToken ct = default);

    Task<AdminResult> AddCategoryAsync(string name, CancellationToken ct = default);

    Task<AdminResult> RenameCategoryAsync(Guid id, string name, CancellationToken ct = default);

    Task<AdminResult> AddSubcategoryAsync(Guid categoryId, SubcategoryInput input, CancellationToken ct = default);

    Task<AdminResult> UpdateSubcategoryAsync(Guid id, SubcategoryInput input, CancellationToken ct = default);

    Task<DeleteResult> DeleteCategoryAsync(Guid id, bool force = false, CancellationToken ct = default);

    Task<DeleteResult> DeleteSubcategoryAsync(Guid id, bool force = false, CancellationToken ct = default);
}

/// <summary>A sub-category row as cleanSub makes it.</summary>
public sealed record CleanSub(string Name, int? DefaultMinutes, int? MaxMinutes, string? Note, List<int> DaysAllowed);

public static class NonOpRules
{
    public static readonly IReadOnlyList<int> AllDays = [0, 1, 2, 3, 4, 5, 6];

    /// <summary>"7 days" reads "Any"; otherwise the days' first letters ("MWF").</summary>
    public static string DaysLabel(IReadOnlyList<int> days) =>
        days.Count == 7 ? "Any" : string.Concat(days.Select(d => Period.DayLabels[d][0]));

    /// <summary>cleanSub: the checks in their order, then the row; the error when refused.</summary>
    public static (CleanSub? Row, string? Error) Clean(SubcategoryInput input)
    {
        var name = Js.Trim(input.Name);
        if (name.Length == 0) return (null, "Enter a sub-category name.");
        var def = input.DefaultMinutes;
        var max = input.MaxMinutes;
        if (def is { } d && (!double.IsFinite(d) || d < 0)) return (null, "Default must be 0 or more.");
        if (max is { } m && (!double.IsFinite(m) || m < 0)) return (null, "Maximum must be 0 or more.");
        if (def is { } d2 && max is { } m2 && d2 > m2) return (null, "Default time cannot exceed the maximum.");
        // default_minutes and max_minutes are integer columns: Postgres refused a fraction.
        if (def is { } d3 && AdminInts.AsInt(d3) is null) return (null, AdminInts.BadInt(d3));
        if (max is { } m3 && AdminInts.AsInt(m3) is null) return (null, AdminInts.BadInt(m3));
        var note = input.Note is null ? "" : Js.Trim(input.Note);
        var days = input.DaysAllowed is { Count: > 0 } given
            ? Js.Sorted(given.Select(x => (double)x), Js.By<double>(x => x)).Select(x => (int)x).ToList()
            : AllDays.ToList();
        return (new CleanSub(
            name,
            def is { } dv ? (int)dv : null,
            max is { } mv ? (int)mv : null,
            note.Length == 0 ? null : note,
            days), null);
    }

    public static string EntriesDependOnCategory(int n) =>
        $"{n} recorded entr{(n == 1 ? "y depends" : "ies depend")} on this category's sub-categories.";

    public static string EntriesUseSubcategory(int n) =>
        $"{n} recorded entr{(n == 1 ? "y uses" : "ies use")} this sub-category.";
}

internal sealed class NonOpCategoriesService(IServiceProvider services) : INonOpCategoriesService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();

    public async Task<NonOpCategoriesView> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        var categories = await db.NonOpCategories.AsNoTracking().OrderBy(c => c.Sort)
            .Select(c => new NonOpCategoryRow(c.Id, c.Name, c.Sort)).ToListAsync(ct);
        var subs = await db.NonOpSubcategories.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        return new NonOpCategoriesView(
            categories,
            subs.Select(s => new NonOpSubRow(s.Id, s.CategoryId, s.Name, s.MinMinutes, s.MaxMinutes, s.DefaultMinutes, s.Note, s.DaysAllowed)).ToList());
    }

    public async Task<AdminResult> AddCategoryAsync(string name, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var n = Js.Trim(name);
        if (n.Length == 0) return AdminResult.Fail("Enter a category name.");
        await using var db = contexts.CreateSystem("Non-Op Categories: add a category");
        db.NonOpCategories.Add(new NonOpCategory { Name = n });
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> RenameCategoryAsync(Guid id, string name, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var n = Js.Trim(name);
        if (n.Length == 0) return AdminResult.Fail("Enter a category name.");
        await using var db = contexts.CreateSystem("Non-Op Categories: rename a category");
        return await AdminDb.WriteAsync(() =>
            db.NonOpCategories.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, n), ct));
    }

    public async Task<AdminResult> AddSubcategoryAsync(Guid categoryId, SubcategoryInput input, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var (row, error) = NonOpRules.Clean(input);
        if (row is null) return AdminResult.Fail(error!);
        await using var db = contexts.CreateSystem("Non-Op Categories: add a sub-category");
        db.NonOpSubcategories.Add(new NonOpSubcategory
        {
            CategoryId = categoryId,
            Name = row.Name,
            DefaultMinutes = row.DefaultMinutes,
            MaxMinutes = row.MaxMinutes,
            MinMinutes = null,
            Note = row.Note,
            DaysAllowed = row.DaysAllowed,
        });
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> UpdateSubcategoryAsync(Guid id, SubcategoryInput input, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var (row, error) = NonOpRules.Clean(input);
        if (row is null) return AdminResult.Fail(error!);
        await using var db = contexts.CreateSystem("Non-Op Categories: save a sub-category");
        var sub = await db.NonOpSubcategories.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is null) return AdminResult.Success; // update ... eq(id): no row, nothing to do
        sub.Name = row.Name;
        sub.DefaultMinutes = row.DefaultMinutes;
        sub.MaxMinutes = row.MaxMinutes;
        sub.MinMinutes = null;
        sub.Note = row.Note;
        sub.DaysAllowed = row.DaysAllowed;
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    // Deleting a category CASCADES to its sub-categories, and each of those in
    // turn blanks (SET NULL) the sub-category on any entries already recorded -
    // the hours would survive but nobody could say what they were for. So the
    // delete is refused whenever recorded entries depend on it, and the message
    // names how many; with nothing depending on it the cascade is harmless.
    public async Task<DeleteResult> DeleteCategoryAsync(Guid id, bool force = false, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Non-Op Categories: delete a category");
        var subIds = await db.NonOpSubcategories.Where(s => s.CategoryId == id).Select(s => s.Id).ToListAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (subIds.Count > 0)
            {
                var n = await db.NonOpEntries.CountAsync(e => subIds.Contains(e.SubcategoryId), ct);
                if (n > 0 && !force) return new DeleteResult(false, NonOpRules.EntriesDependOnCategory(n), n);
                // Forced: remove the dependent entries outright rather than leaving them
                // with no sub-category, which would keep the hours on the books with
                // nothing to say what they were for.
                if (n > 0) await db.NonOpEntries.Where(e => subIds.Contains(e.SubcategoryId)).ExecuteDeleteAsync(ct);
                // The category's sub-categories go with it (Postgres: ON DELETE CASCADE).
                await db.NonOpSubcategories.Where(s => s.CategoryId == id).ExecuteDeleteAsync(ct);
            }
            await db.NonOpCategories.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return new DeleteResult(true);
        }
        catch (Exception e) when (AdminDb.IsRefusal(e))
        {
            return new DeleteResult(false, AdminDb.MessageOf(e));
        }
    }

    public async Task<DeleteResult> DeleteSubcategoryAsync(Guid id, bool force = false, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Non-Op Categories: delete a sub-category");
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Entries reference sub-categories; refuse rather than orphan real history,
            // unless the super user has explicitly confirmed taking the entries too.
            var n = await db.NonOpEntries.CountAsync(e => e.SubcategoryId == id, ct);
            if (n > 0 && !force) return new DeleteResult(false, NonOpRules.EntriesUseSubcategory(n), n);
            if (n > 0) await db.NonOpEntries.Where(e => e.SubcategoryId == id).ExecuteDeleteAsync(ct);
            await db.NonOpSubcategories.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return new DeleteResult(true);
        }
        catch (Exception e) when (AdminDb.IsRefusal(e))
        {
            return new DeleteResult(false, AdminDb.MessageOf(e));
        }
    }
}

public sealed class NonOpCategoriesModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<INonOpCategoriesService, NonOpCategoriesService>();
        services.AddScoped<INonOpEntryUpload, NonOpEntryUpload>();
    }
}
