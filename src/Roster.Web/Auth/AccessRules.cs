namespace Roster.Web.Auth;

/// <summary>How a user relates to one store, whatever the page.</summary>
public enum StoreAccess
{
    /// <summary>Full control: the store's own manager, their area's EMSO, or a super user.</summary>
    Write,

    /// <summary>The full read-only view: PA and DSO oversight see everything, unchanged.</summary>
    Read,

    /// <summary>
    /// Another store's manager, or an EMSO outside their area: may open the
    /// peer-visible pages (Plan structure, Non-Op, dashboards) in trimmed
    /// read-only form, never coverage flags, day summaries or approval state.
    /// Private pages (Roster, Setup, Analysis, Sales, Weekends) never offer
    /// peer stores at all.
    /// </summary>
    Peer,
}

/// <summary>Where a user may write.</summary>
public abstract record WriteScope
{
    private WriteScope() { }

    public sealed record All : WriteScope;

    public sealed record OneStore(Guid StoreId) : WriteScope;

    public sealed record Area(Guid AreaId) : WriteScope;

    public sealed record None : WriteScope;
}

/// <summary>
/// The central access matrix, pure: the TypeScript app's <c>src/lib/access.ts</c>
/// and the database's <c>visible_store_ids()</c> / <c>writable_store_ids()</c>.
/// <code>
///   super_user  write everywhere
///   store       write own store          (per-person SM/ASM logins)
///   emso        write every store in their AREA
///   pa / dso    read-only, whole network ("director" is the DSO tier)
/// </code>
/// </summary>
public static class AccessRules
{
    /// <summary>Tiers that can never write (PA and DSO).</summary>
    public static bool IsReadOnlyTier(Tier tier) => tier.IsReadOnly();

    public static WriteScope WriteScopeFor(AppUser user) => user.Tier switch
    {
        Tier.SuperUser => new WriteScope.All(),
        // A store user without a store (a misconfigured row) can write nothing.
        Tier.Store => user.StoreId is { } store ? new WriteScope.OneStore(store) : new WriteScope.None(),
        Tier.Emso => user.AreaId is { } area ? new WriteScope.Area(area) : new WriteScope.None(),
        _ => new WriteScope.None(),
    };

    public static bool StoreInWriteScope(WriteScope scope, StoreInfo store) => scope switch
    {
        WriteScope.All => true,
        WriteScope.OneStore s => s.StoreId == store.Id,
        // A store with no area can never fall into an area scope by accident.
        WriteScope.Area a => store.AreaId is { } area && area == a.AreaId,
        _ => false,
    };

    /// <summary>How the user relates to this store: write, read or peer.</summary>
    public static StoreAccess StoreAccessFor(AppUser user, StoreInfo store)
    {
        if (IsReadOnlyTier(user.Tier)) return StoreAccess.Read;
        return StoreInWriteScope(WriteScopeFor(user), store) ? StoreAccess.Write : StoreAccess.Peer;
    }

    /// <summary>
    /// The stores a user may see on the private pages (<c>visible_store_ids()</c>):
    /// a super user, PA and DSO every store, a store user their own, an EMSO the
    /// stores of their area.
    /// </summary>
    public static bool StoreVisibleTo(AppUser user, StoreInfo store) => user.Tier switch
    {
        Tier.SuperUser or Tier.Pa or Tier.Director => true,
        Tier.Store => user.StoreId is { } own && own == store.Id,
        Tier.Emso => user.AreaId is { } area && store.AreaId == area,
        _ => false,
    };
}
