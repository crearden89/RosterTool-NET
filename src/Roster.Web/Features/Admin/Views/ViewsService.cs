// Port of src/app/(app)/admin/views/page.tsx (its read) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.Views;

/// <summary>One cell of the matrix: a level (SM, PA, EMSO, DSO), a tab (its href) and whether it shows.</summary>
public sealed record VisibilityCell(string Level, string TabKey, bool Visible);

/// <summary>A menu page as the matrix lists it (ALL_TABS): its key is its href.</summary>
public sealed record ViewTab(string Key, string Label, string Section);

public interface IViewsService
{
    /// <summary>The stored cells, keyed "level:tabKey" (absent = visible by default).</summary>
    Task<IReadOnlyDictionary<string, bool>> LoadAsync(CancellationToken ct = default);

    /// <summary>saveTabVisibility: upserts every cell sent, on (level, tab_key).</summary>
    Task<AdminResult> SaveTabVisibilityAsync(IReadOnlyList<VisibilityCell> cells, CancellationToken ct = default);
}

public static class ViewsMatrix
{
    public static string Key(string level, string tabKey) => $"{level}:{tabKey}";

    /// <summary>
    /// The matrix as the page starts it: every tab for every level, from the stored
    /// cells, visible where nothing is stored.
    /// </summary>
    public static Dictionary<string, bool> Saved(IEnumerable<ViewTab> tabs, IReadOnlyDictionary<string, bool> initial)
    {
        var m = new Dictionary<string, bool>();
        foreach (var t in tabs)
            foreach (var lv in HierarchyLevels.All)
            {
                var key = Key(lv, t.Key);
                m[key] = initial.TryGetValue(key, out var v) ? v : true;
            }
        return m;
    }

    /// <summary>Every cell of the matrix, tab by tab, as the save sends it.</summary>
    public static List<VisibilityCell> Cells(IEnumerable<ViewTab> tabs, IReadOnlyDictionary<string, bool> state)
    {
        var cells = new List<VisibilityCell>();
        foreach (var t in tabs)
            foreach (var lv in HierarchyLevels.All)
                cells.Add(new VisibilityCell(lv, t.Key, state.TryGetValue(Key(lv, t.Key), out var v) ? v : true));
        return cells;
    }
}

internal sealed class ViewsService(IServiceProvider services) : IViewsService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();

    public async Task<IReadOnlyDictionary<string, bool>> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        var rows = await db.TabVisibility.AsNoTracking().ToListAsync(ct);
        var initial = new Dictionary<string, bool>();
        foreach (var r in rows) initial[ViewsMatrix.Key(r.Level, r.TabKey)] = r.Visible;
        return initial;
    }

    public async Task<AdminResult> SaveTabVisibilityAsync(IReadOnlyList<VisibilityCell> cells, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Views: save the visibility matrix");
        var existing = await db.TabVisibility.ToDictionaryAsync(r => ViewsMatrix.Key(r.Level, r.TabKey), ct);
        foreach (var c in cells)
        {
            if (existing.TryGetValue(ViewsMatrix.Key(c.Level, c.TabKey), out var row)) row.Visible = c.Visible;
            else
            {
                row = new TabVisibility { Level = c.Level, TabKey = c.TabKey, Visible = c.Visible };
                db.TabVisibility.Add(row);
                existing[ViewsMatrix.Key(c.Level, c.TabKey)] = row;
            }
        }
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }
}

public sealed class ViewsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IViewsService, ViewsService>();
}
