using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Roster.Data.Entities;

namespace Roster.Data.Scope;

/// <summary>
/// The write side of the policies, checked on every SaveChanges before anything is sent:
/// the equivalent of the RLS WITH CHECK clauses (0006, 0044) and of the server-action
/// guards in authz.ts, which in the TypeScript app stood in front of every service-role
/// write. A refused write throws <see cref="ScopeViolationException"/> and nothing is saved.
///
/// Bulk ExecuteUpdate/ExecuteDelete statements bypass SaveChanges; every service that uses
/// one checks <see cref="IStoreScope"/> first.
/// </summary>
internal static class WriteScopeGuard
{
    public const string DeniedAuth = "You are not signed in.";
    public const string DeniedReadOnly = "Your access level is read-only - this change was not saved.";
    public const string DeniedScope = "You don't have access to that store.";

    public static async Task EnforceAsync(RosterDbContext db, CancellationToken ct)
    {
        var access = db.Access;
        if (access.Kind == AccessKind.System) return;

        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (entries.Count == 0) return;
        if (access.Kind == AccessKind.Anonymous) throw new ScopeViolationException(DeniedAuth);

        var resolver = new StoreResolver(db, entries);
        foreach (var entry in entries)
        {
            var type = entry.Metadata.ClrType;
            var policy = TablePolicies.For(type);
            var refusal = access.Kind switch
            {
                AccessKind.Feed => FeedRefusal(access, entry, policy),
                AccessKind.Kiosk => await KioskRefusalAsync(access, entry, policy, resolver, ct),
                AccessKind.User => await UserRefusalAsync(access.User!, entry, policy, resolver, ct),
                _ => DeniedAuth,
            };
            if (refusal is not null)
                throw new ScopeViolationException($"{refusal} ({TableName(entry)}, {access})");
        }
    }

    private static string TableName(EntityEntry entry) => entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name;

    private static string? FeedRefusal(AccessContext access, EntityEntry entry, TablePolicy policy)
    {
        if (policy.FeedMayWrite) return null;
        // The feed records its own token's last use, and nothing else.
        if (entry.Entity is FeedToken t && entry.State == EntityState.Modified && t.Id == access.FeedTokenId) return null;
        return "A data feed may write only the fed tables";
    }

    private static async Task<string?> KioskRefusalAsync(
        AccessContext access, EntityEntry entry, TablePolicy policy, StoreResolver resolver, CancellationToken ct)
    {
        if (policy.Write != WriteRule.KioskStore) return "A kiosk may write only its loads, assignments, walk notes and task ticks";
        foreach (var storeId in await resolver.StoresOfAsync(entry, policy, ct))
            if (storeId != access.KioskStoreId) return DeniedScope;
        return null;
    }

    private static async Task<string?> UserRefusalAsync(
        ScopedUser user, EntityEntry entry, TablePolicy policy, StoreResolver resolver, CancellationToken ct)
    {
        switch (policy.Write)
        {
            case WriteRule.SuperUser:
                if (user.Tier == Tier.SuperUser) return null;
                return AccessMatrix.IsReadOnlyTier(user.Tier) ? DeniedReadOnly : "Only a super user can change this";

            case WriteRule.ServerOnly:
                return "Only the server can change this";

            case WriteRule.KioskStore:
                return "Only the store's kiosk can change this";

            case WriteRule.Notification:
                if (user.Tier == Tier.SuperUser) return null;
                if (entry.State != EntityState.Modified) return "Only a super user can create or delete notifications";
                var n = (Notification)entry.Entity;
                var originalUser = (Guid)entry.Property(nameof(Notification.UserId)).OriginalValue!;
                return n.UserId == user.Id && originalUser == user.Id ? null : "That notification is not yours";

            case WriteRule.WritableStore:
            case WriteRule.WritableStoreViaEmployee:
            case WriteRule.WritableStoreViaShift:
            {
                if (AccessMatrix.IsReadOnlyTier(user.Tier)) return DeniedReadOnly;
                var scope = AccessMatrix.WriteScopeFor(user);
                foreach (var storeId in await resolver.StoresOfAsync(entry, policy, ct))
                {
                    if (storeId is null) return "That team member no longer exists.";
                    var areaId = scope is StoreSet.OneArea ? await resolver.AreaOfAsync(storeId.Value, ct) : null;
                    if (!scope.Contains(storeId.Value, areaId)) return DeniedScope;
                }
                return null;
            }

            default:
                return $"Unhandled write rule {policy.Write}";
        }
    }

    /// <summary>Finds the store(s) a pending change belongs to, before and after the change.</summary>
    private sealed class StoreResolver(RosterDbContext db, IReadOnlyList<EntityEntry> entries)
    {
        private readonly Dictionary<Guid, Guid?> _employeeStore = [];
        private readonly Dictionary<Guid, Guid?> _shiftStore = [];
        private readonly Dictionary<Guid, Guid?> _storeArea = [];

        public async Task<IReadOnlyList<Guid?>> StoresOfAsync(EntityEntry entry, TablePolicy policy, CancellationToken ct)
        {
            var key = policy.Write switch
            {
                WriteRule.WritableStoreViaEmployee => "EmployeeId",
                WriteRule.WritableStoreViaShift => "ShiftId",
                _ => "StoreId",
            };
            var values = Values(entry, key);
            return policy.Write switch
            {
                WriteRule.WritableStoreViaEmployee => await MapAsync(values, EmployeeStoreAsync, ct),
                WriteRule.WritableStoreViaShift => await MapAsync(values, ShiftStoreAsync, ct),
                _ => values.Select(v => (Guid?)v).ToList(),
            };
        }

        public async Task<Guid?> AreaOfAsync(Guid storeId, CancellationToken ct)
        {
            if (_storeArea.TryGetValue(storeId, out var cached)) return cached;
            var area = await db.Stores.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == storeId).Select(s => s.AreaId).FirstOrDefaultAsync(ct);
            return _storeArea[storeId] = area;
        }

        private static List<Guid> Values(EntityEntry entry, string property)
        {
            var p = entry.Property(property);
            var list = new List<Guid>();
            if (entry.State != EntityState.Deleted && p.CurrentValue is Guid current) list.Add(current);
            if (entry.State != EntityState.Added && p.OriginalValue is Guid original && !list.Contains(original)) list.Add(original);
            return list;
        }

        private static async Task<IReadOnlyList<Guid?>> MapAsync(
            List<Guid> ids, Func<Guid, CancellationToken, Task<Guid?>> map, CancellationToken ct)
        {
            var result = new List<Guid?>(ids.Count);
            foreach (var id in ids) result.Add(await map(id, ct));
            return result;
        }

        private async Task<Guid?> EmployeeStoreAsync(Guid employeeId, CancellationToken ct)
        {
            if (_employeeStore.TryGetValue(employeeId, out var cached)) return cached;
            // An employee added in this same save (a new team member and their availability).
            var tracked = entries.Select(e => e.Entity).OfType<Employee>().FirstOrDefault(e => e.Id == employeeId);
            var store = tracked?.StoreId ?? await db.Employees.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Id == employeeId).Select(e => (Guid?)e.StoreId).FirstOrDefaultAsync(ct);
            return _employeeStore[employeeId] = store;
        }

        private async Task<Guid?> ShiftStoreAsync(Guid shiftId, CancellationToken ct)
        {
            if (_shiftStore.TryGetValue(shiftId, out var cached)) return cached;
            var tracked = entries.Select(e => e.Entity).OfType<GeneratedShift>().FirstOrDefault(s => s.Id == shiftId)
                ?? db.ChangeTracker.Entries<GeneratedShift>().Select(e => e.Entity).FirstOrDefault(s => s.Id == shiftId);
            var store = tracked?.StoreId ?? await db.GeneratedShifts.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == shiftId).Select(s => (Guid?)s.StoreId).FirstOrDefaultAsync(ct);
            return _shiftStore[shiftId] = store;
        }
    }
}
