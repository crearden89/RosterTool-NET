// Port of src/lib/authz.ts (the write guards) and src/lib/storeScope.ts.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Scope;

/// <summary>
/// <see cref="IStoreScope"/> over the database. Look-ups (a store's area, an employee's
/// store) read past the row filters, as authz.ts read them with the service-role client:
/// they decide access, so they must not themselves be limited by it.
/// </summary>
public sealed class StoreScope(IAccessContextAccessor accessor, IRosterDbContextFactory contexts) : IStoreScope
{
    private readonly Dictionary<Guid, Guid?> _areaForStore = [];
    private readonly Dictionary<Guid, Guid?> _storeForEmployee = [];

    public AccessContext Access => accessor.Current;

    public ScopedUser? User => Access.User;

    public async Task<IReadOnlyList<Guid>?> ReadableStoreIdsAsync(CancellationToken cancellationToken = default) =>
        await IdsAsync(Access.ReadScope, cancellationToken);

    public async Task<IReadOnlyList<Guid>?> WritableStoreIdsAsync(CancellationToken cancellationToken = default) =>
        await IdsAsync(Access.WriteScope, cancellationToken);

    public async Task<bool> CanReadStoreAsync(Guid storeId, CancellationToken cancellationToken = default) =>
        await ContainsAsync(Access.ReadScope, storeId, cancellationToken);

    public async Task<bool> CanWriteStoreAsync(Guid storeId, CancellationToken cancellationToken = default) =>
        await ContainsAsync(Access.WriteScope, storeId, cancellationToken);

    public async Task<StoreAccess?> AccessForStoreAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        if (User is not { } user) return null;
        await using var db = contexts.CreateSystem("store access");
        var store = await db.Stores.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == storeId).Select(s => new { s.Id, s.AreaId }).FirstOrDefaultAsync(cancellationToken);
        if (store is null) return null;
        _areaForStore[store.Id] = store.AreaId;
        return AccessMatrix.StoreAccessFor(user, store.Id, store.AreaId);
    }

    // ─── authz.ts ──────────────────────────────────────────────────────────

    /// <summary>May this user write at all? (writer())</summary>
    public string? DenyWrite()
    {
        if (Access.Kind == AccessKind.System) return null;
        if (User is not { } user) return ScopeMessages.DeniedAuth;
        return AccessMatrix.IsReadOnlyTier(user.Tier) ? ScopeMessages.DeniedReadOnly : null;
    }

    public async Task<string?> DenyStoreWriteAsync(Guid? storeId, CancellationToken cancellationToken = default)
    {
        var denied = DenyWrite();
        if (denied is not null) return denied;
        if (storeId is not { } id) return ScopeMessages.DeniedScope;
        return await InWriteScopeAsync(id, cancellationToken) ? null : ScopeMessages.DeniedScope;
    }

    public async Task<string?> DenyEmployeeWriteAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var denied = DenyWrite();
        if (denied is not null) return denied;
        var storeId = await StoreIdForEmployeeAsync(employeeId, cancellationToken);
        if (storeId is null) return ScopeMessages.EmployeeGone;
        return await InWriteScopeAsync(storeId.Value, cancellationToken) ? null : ScopeMessages.DeniedScope;
    }

    public async Task<string?> DenyEmployeesWriteAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        var denied = DenyWrite();
        if (denied is not null) return denied;
        var unique = employeeIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (unique.Count == 0) return null;
        foreach (var id in unique)
        {
            var storeId = await StoreIdForEmployeeAsync(id, cancellationToken);
            if (storeId is null) return ScopeMessages.EmployeeGone;
            if (!await InWriteScopeAsync(storeId.Value, cancellationToken)) return ScopeMessages.DeniedScope;
        }
        return null;
    }

    public bool HasTier(params Tier[] tiers) => User is { } u && tiers.Contains(u.Tier);

    public async Task<IReadOnlyList<Guid>?> NetworkStoreIdsAsync(NetworkScope scope, CancellationToken cancellationToken = default)
    {
        if (User is not { } user) return [];
        await using var db = contexts.CreateSystem("network scope");
        var stores = await db.Stores.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(s => s.Code).Select(s => new ScopeStore(s.Id, s.RegionId, s.AreaId)).ToListAsync(cancellationToken);
        var regions = await db.Regions.IgnoreQueryFilters().AsNoTracking()
            .Select(r => new ScopeRegion(r.Id, r.DirectorGroupId)).ToListAsync(cancellationToken);
        return ReportingScope.NetworkStoreIds(user, stores, regions, scope);
    }

    /// <summary>
    /// resolveStoreId (storeScope.ts): an explicit request wins when it is in scope, then the
    /// person's last choice (the roster_store_id cookie), then the first store in scope.
    /// </summary>
    public static Guid? ResolveStoreId(Guid? requested, IReadOnlyList<Guid> scopeStores, Guid? remembered)
    {
        if (requested is { } r && scopeStores.Contains(r)) return r;
        if (remembered is { } c && scopeStores.Contains(c)) return c;
        return scopeStores.Count > 0 ? scopeStores[0] : null;
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private async Task<bool> InWriteScopeAsync(Guid storeId, CancellationToken ct) =>
        await ContainsAsync(Access.WriteScope, storeId, ct);

    private async Task<bool> ContainsAsync(StoreSet set, Guid storeId, CancellationToken ct)
    {
        // Only an area scope needs the store's area looked up; every other kind decides on ids.
        var area = set is StoreSet.OneArea ? await AreaForStoreAsync(storeId, ct) : null;
        return set.Contains(storeId, area);
    }

    private async Task<IReadOnlyList<Guid>?> IdsAsync(StoreSet set, CancellationToken ct)
    {
        switch (set)
        {
            case StoreSet.All:
                return null;
            case StoreSet.OneStore one:
            {
                await using var db = contexts.CreateSystem("store scope");
                var exists = await db.Stores.IgnoreQueryFilters().AnyAsync(s => s.Id == one.StoreId, ct);
                return exists ? [one.StoreId] : [];
            }
            case StoreSet.OneArea area:
            {
                await using var db = contexts.CreateSystem("store scope");
                return await db.Stores.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.AreaId == area.AreaId).OrderBy(s => s.Code).Select(s => s.Id).ToListAsync(ct);
            }
            default:
                return [];
        }
    }

    private async Task<Guid?> AreaForStoreAsync(Guid storeId, CancellationToken ct)
    {
        if (_areaForStore.TryGetValue(storeId, out var cached)) return cached;
        await using var db = contexts.CreateSystem("store area");
        var area = await db.Stores.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == storeId).Select(s => s.AreaId).FirstOrDefaultAsync(ct);
        return _areaForStore[storeId] = area;
    }

    private async Task<Guid?> StoreIdForEmployeeAsync(Guid employeeId, CancellationToken ct)
    {
        if (employeeId == Guid.Empty) return null;
        if (_storeForEmployee.TryGetValue(employeeId, out var cached)) return cached;
        await using var db = contexts.CreateSystem("employee store");
        var store = await db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == employeeId).Select(e => (Guid?)e.StoreId).FirstOrDefaultAsync(ct);
        return _storeForEmployee[employeeId] = store;
    }
}

/// <summary><see cref="IStoreDirectory"/> over the stores table.</summary>
public sealed class StoreDirectory(IRosterDbContextFactory contexts) : IStoreDirectory
{
    public async Task<IReadOnlyList<StoreInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        // The whole network: peer-visible pages list every store, as the TypeScript app
        // listed them with the service-role client. Visibility is decided per store after.
        await using var db = contexts.CreateSystem("store list");
        return await Project(db.Stores.IgnoreQueryFilters().AsNoTracking().OrderBy(s => s.Code)).ToListAsync(cancellationToken);
    }

    public async Task<StoreInfo?> FindAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts.CreateSystem("store lookup");
        return await Project(db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == storeId)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<StoreInfo?> FindByKioskTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await using var db = contexts.CreateSystem("kiosk token");
        return await Project(db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.KioskToken == token)).FirstOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<StoreInfo> Project(IQueryable<Store> stores) =>
        stores.Select(s => new StoreInfo { Id = s.Id, Code = s.Code, Name = s.Name, AreaId = s.AreaId, RegionId = s.RegionId, State = s.State });
}
