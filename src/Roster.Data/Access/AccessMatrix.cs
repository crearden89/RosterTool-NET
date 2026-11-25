using Roster.Data.Entities;

namespace Roster.Data.Access;

// ─── The central access matrix (pure) — src/lib/access.ts ────────────────────
// The single statement of who may WRITE what, and its read-side mirror (the Postgres
// functions visible_store_ids() and writable_store_ids(), migrations 0016 and 0044):
//
//   super_user  read everywhere, write everywhere
//   store       read and write their own store
//   emso        read and write every store in their AREA (about six stores)
//   pa / dso    read the whole network, write nowhere ("director" is the DSO tier)
//
// A misconfigured account (a store user with no store, an EMSO with no area) reads and
// writes nothing: scope failures fail closed.

/// <summary>The scope columns of a signed-in user (app_users).</summary>
public sealed record ScopedUser(
    Guid Id,
    string Username,
    Tier Tier,
    Guid? StoreId,
    Guid? RegionId,
    Guid? AreaId,
    Guid? DirectorGroupId)
{
    public static ScopedUser From(AppUser u) =>
        new(u.Id, u.Username, u.Tier, u.StoreId, u.RegionId, u.AreaId, u.DirectorGroupId);
}

/// <summary>A set of stores: all of them, one, one area's, or none.</summary>
public abstract record StoreSet
{
    private StoreSet() { }

    public sealed record All : StoreSet;
    public sealed record OneStore(Guid StoreId) : StoreSet;
    public sealed record OneArea(Guid AreaId) : StoreSet;
    public sealed record Nothing : StoreSet;

    public static readonly StoreSet Everything = new All();
    public static readonly StoreSet Empty = new Nothing();

    /// <summary>Does this set contain the store? (storeInWriteScope)</summary>
    public bool Contains(Guid storeId, Guid? storeAreaId) => this switch
    {
        All => true,
        OneStore s => s.StoreId == storeId,
        OneArea a => storeAreaId is not null && a.AreaId == storeAreaId,
        _ => false,
    };
}

/// <summary>How a user relates to one specific store, page-agnostic (storeAccessFor).</summary>
public enum StoreAccess
{
    /// <summary>Full control: the store's own manager, their area's EMSO, or a super user.</summary>
    Write,
    /// <summary>Full read-only view: PA/DSO oversight sees everything.</summary>
    Read,
    /// <summary>
    /// Another store's manager (or an EMSO outside their area): may open the peer-visible
    /// pages (Plan structure, Non-Op, dashboards) in TRIMMED read-only form. Private pages
    /// (Roster, Setup, Analysis, Sales, Weekends) never offer peer stores at all.
    /// </summary>
    Peer,
}

public static class AccessMatrix
{
    /// <summary>Tiers that can never write. EMSO is deliberately not one.</summary>
    public static bool IsReadOnlyTier(Tier tier) => tier is Tier.Pa or Tier.Director;

    /// <summary>The stores a user may WRITE (writeScopeFor / writable_store_ids()).</summary>
    public static StoreSet WriteScopeFor(ScopedUser user) => user.Tier switch
    {
        Tier.SuperUser => StoreSet.Everything,
        Tier.Store => user.StoreId is { } s ? new StoreSet.OneStore(s) : StoreSet.Empty,
        Tier.Emso => user.AreaId is { } a ? new StoreSet.OneArea(a) : StoreSet.Empty,
        _ => StoreSet.Empty,
    };

    /// <summary>The stores a user may READ (visible_store_ids(), 0016).</summary>
    public static StoreSet ReadScopeFor(ScopedUser user) => user.Tier switch
    {
        Tier.SuperUser => StoreSet.Everything,
        Tier.Pa or Tier.Director => StoreSet.Everything,
        Tier.Store => user.StoreId is { } s ? new StoreSet.OneStore(s) : StoreSet.Empty,
        Tier.Emso => user.AreaId is { } a ? new StoreSet.OneArea(a) : StoreSet.Empty,
        _ => StoreSet.Empty,
    };

    public static bool StoreInWriteScope(StoreSet scope, Guid storeId, Guid? storeAreaId) =>
        scope.Contains(storeId, storeAreaId);

    /// <summary>write / read / peer for one store (storeAccessFor).</summary>
    public static StoreAccess StoreAccessFor(ScopedUser user, Guid storeId, Guid? storeAreaId)
    {
        if (IsReadOnlyTier(user.Tier)) return StoreAccess.Read;
        return StoreInWriteScope(WriteScopeFor(user), storeId, storeAreaId) ? StoreAccess.Write : StoreAccess.Peer;
    }
}

/// <summary>Which stores a NETWORK roll-up shows (src/lib/reportingScope.ts).</summary>
public enum NetworkScope
{
    /// <summary>The user's own remit.</summary>
    Remit,
    /// <summary>The whole network; honoured for PA and DSO only.</summary>
    National,
}

public sealed record ScopeStore(Guid Id, Guid? RegionId, Guid? AreaId);
public sealed record ScopeRegion(Guid Id, Guid? DirectorGroupId);

/// <summary>
/// The read side of the hierarchy for cross-store league tables:
///   store → no network view; emso → their area; pa → their region;
///   director → every region in their director group; super user → everything.
/// PA and DSO can additionally switch to national.
/// </summary>
public static class ReportingScope
{
    /// <summary>A store login has no use for a league table of everyone else's stores.</summary>
    public static bool CanSeeNetwork(Tier tier) => tier != Tier.Store;

    /// <summary>Only the network-wide oversight tiers get the remit/national switch.</summary>
    public static bool CanSeeNational(Tier tier) => tier is Tier.Pa or Tier.Director;

    /// <summary>
    /// The stores in a user's own remit. Null means EVERY store (a super user has no remit).
    /// A misconfigured account — a PA with no region, an EMSO with no area — gets an empty
    /// list, not the network.
    /// </summary>
    public static IReadOnlyList<Guid>? RemitStoreIds(ScopedUser user, IEnumerable<ScopeStore> stores, IEnumerable<ScopeRegion> regions)
    {
        switch (user.Tier)
        {
            case Tier.SuperUser:
                return null;
            case Tier.Store:
                return user.StoreId is { } s ? [s] : [];
            case Tier.Emso:
                return user.AreaId is { } a ? stores.Where(x => x.AreaId == a).Select(x => x.Id).ToList() : [];
            case Tier.Pa:
                return user.RegionId is { } r ? stores.Where(x => x.RegionId == r).Select(x => x.Id).ToList() : [];
            case Tier.Director:
            {
                if (user.DirectorGroupId is not { } dg) return [];
                var mine = regions.Where(x => x.DirectorGroupId == dg).Select(x => x.Id).ToHashSet();
                return stores.Where(x => x.RegionId is { } rid && mine.Contains(rid)).Select(x => x.Id).ToList();
            }
            default:
                return [];
        }
    }

    /// <summary>
    /// The stores a network view should load. National is honoured only for the tiers
    /// entitled to it, so a hand-typed ?scope=national cannot widen an EMSO or a store login.
    /// </summary>
    public static IReadOnlyList<Guid>? NetworkStoreIds(
        ScopedUser user, IEnumerable<ScopeStore> stores, IEnumerable<ScopeRegion> regions, NetworkScope scope)
    {
        if (scope == NetworkScope.National && CanSeeNational(user.Tier)) return null;
        return RemitStoreIds(user, stores, regions);
    }
}
