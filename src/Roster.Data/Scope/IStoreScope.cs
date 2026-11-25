// Port of src/lib/authz.ts, src/lib/storeScope.ts and the read side of src/lib/auth.ts,
// with src/lib/access.ts and src/lib/reportingScope.ts in AccessMatrix.cs.
using Roster.Data.Entities;

namespace Roster.Data.Scope;

/// <summary>
/// Who may see and change which store, for the current <see cref="AccessContext"/>. Pages
/// and services call this before every write (it returns the TypeScript app's own refusal
/// messages); <see cref="RosterDbContext"/> enforces the same rules again on every query
/// (filters) and every SaveChanges (write checks), as row-level security did.
/// </summary>
public interface IStoreScope
{
    /// <summary>Who is acting.</summary>
    AccessContext Access { get; }

    /// <summary>The signed-in user, or null (anonymous, kiosk, feed, system).</summary>
    ScopedUser? User { get; }

    /// <summary>visible_store_ids(): the stores the caller may read. Null = every store.</summary>
    Task<IReadOnlyList<Guid>?> ReadableStoreIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>writable_store_ids(): the stores the caller may write. Null = every store.</summary>
    Task<IReadOnlyList<Guid>?> WritableStoreIdsAsync(CancellationToken cancellationToken = default);

    Task<bool> CanReadStoreAsync(Guid storeId, CancellationToken cancellationToken = default);

    Task<bool> CanWriteStoreAsync(Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// storeAccessFor: write / read / peer for one store. Null when nobody is signed in or
    /// the store does not exist.
    /// </summary>
    Task<StoreAccess?> AccessForStoreAsync(Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>denyWrite: may this caller write at all? Null when allowed, else the message.</summary>
    string? DenyWrite();

    /// <summary>denyStoreWrite: guard a write against a specific store. Null when allowed.</summary>
    Task<string?> DenyStoreWriteAsync(Guid? storeId, CancellationToken cancellationToken = default);

    /// <summary>denyEmployeeWrite: guard a write against the store that owns an employee.</summary>
    Task<string?> DenyEmployeeWriteAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>denyEmployeesWrite: refuse if ANY of the employees is out of scope.</summary>
    Task<string?> DenyEmployeesWriteAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken = default);

    /// <summary>requireTier: is the caller signed in with one of these tiers?</summary>
    bool HasTier(params Tier[] tiers);

    /// <summary>
    /// The stores a network roll-up should load (networkStoreIds): the caller's remit, or
    /// the whole network (null) for a super user, or for PA/DSO asking for national.
    /// </summary>
    Task<IReadOnlyList<Guid>?> NetworkStoreIdsAsync(NetworkScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Refusal messages, worded exactly as src/lib/authz.ts words them.</summary>
public static class ScopeMessages
{
    public const string DeniedReadOnly = "Your access level is read-only - this change was not saved.";
    public const string DeniedScope = "You don't have access to that store.";
    public const string DeniedAuth = "You are not signed in.";
    public const string EmployeeGone = "That team member no longer exists.";
}

/// <summary>A store, as the store pickers and the scope checks need it.</summary>
public sealed record StoreInfo
{
    public required Guid Id { get; init; }

    /// <summary>The store code ("G101").</summary>
    public required string Code { get; init; }

    public required string Name { get; init; }

    /// <summary>The area (EMSO patch) the store is in.</summary>
    public Guid? AreaId { get; init; }

    public Guid? RegionId { get; init; }

    /// <summary>The state, which sets the store's clock.</summary>
    public string? State { get; init; }

    /// <summary>"G101 - Northside", as the store pickers write it.</summary>
    public string Label => $"{Code} - {Name}";
}

/// <summary>Every store, for the store pickers (peer-visible pages list the whole network).</summary>
public interface IStoreDirectory
{
    /// <summary>Every store, ordered by code.</summary>
    Task<IReadOnlyList<StoreInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One store, or null.</summary>
    Task<StoreInfo?> FindAsync(Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>The store a kiosk token opens (Floor Display, Load Planner), or null.</summary>
    Task<StoreInfo?> FindByKioskTokenAsync(string token, CancellationToken cancellationToken = default);
}
