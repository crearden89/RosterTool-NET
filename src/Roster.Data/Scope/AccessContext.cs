using Roster.Data.Entities;

namespace Roster.Data.Scope;

/// <summary>Who is acting on the database.</summary>
public enum AccessKind
{
    /// <summary>Nobody signed in: reads nothing, writes nothing.</summary>
    Anonymous,
    /// <summary>A signed-in person, scoped by their tier (the RLS "authenticated" role).</summary>
    User,
    /// <summary>
    /// A no-login kiosk page (Floor Display, Load Planner) reached by a store's secret token:
    /// reads that one store and the shared configuration, writes only the kiosk tables of
    /// that store (daily loads, load assignments, walk notes, admin-task ticks).
    /// </summary>
    Kiosk,
    /// <summary>
    /// The data-feed API, authenticated by a feed token: reads like the server, writes only
    /// the fed tables (benchmark, actuals, POS statistics, their batch and period rows).
    /// </summary>
    Feed,
    /// <summary>
    /// Trusted server code with no user behind it — scheduled jobs, seeding, sign-in
    /// provisioning. The equivalent of the Supabase service role. Use sparingly.
    /// </summary>
    System,
}

/// <summary>
/// The identity a <see cref="RosterDbContext"/> works under. Every query is filtered by it
/// (the row-level-security policies, as EF Core global query filters) and every write is
/// checked against it before SaveChanges (the policies' WITH CHECK side).
/// </summary>
public sealed class AccessContext
{
    private AccessContext(AccessKind kind, ScopedUser? user, Guid? kioskStoreId, Guid? feedTokenId, string? reason)
    {
        Kind = kind;
        User = user;
        KioskStoreId = kioskStoreId;
        FeedTokenId = feedTokenId;
        Reason = reason;
    }

    public AccessKind Kind { get; }
    public ScopedUser? User { get; }
    public Guid? KioskStoreId { get; }
    public Guid? FeedTokenId { get; }
    /// <summary>Why a System context was opened (for logs).</summary>
    public string? Reason { get; }

    public static readonly AccessContext Anonymous = new(AccessKind.Anonymous, null, null, null, null);

    public static AccessContext ForUser(ScopedUser user) =>
        new(AccessKind.User, user ?? throw new ArgumentNullException(nameof(user)), null, null, null);

    public static AccessContext ForKiosk(Guid storeId) => new(AccessKind.Kiosk, null, storeId, null, null);

    public static AccessContext ForFeed(Guid feedTokenId) => new(AccessKind.Feed, null, null, feedTokenId, null);

    public static AccessContext System(string reason) =>
        new(AccessKind.System, null, null, null, string.IsNullOrWhiteSpace(reason) ? throw new ArgumentException("Say why.", nameof(reason)) : reason);

    public bool IsSignedIn => Kind == AccessKind.User;
    public bool IsSuperUser => User?.Tier == Tier.SuperUser;
    public bool IsUnrestricted => Kind == AccessKind.System;

    /// <summary>The stores this context may read (visible_store_ids() for a user).</summary>
    public StoreSet ReadScope => Kind switch
    {
        AccessKind.User => AccessMatrix.ReadScopeFor(User!),
        AccessKind.Kiosk => new StoreSet.OneStore(KioskStoreId!.Value),
        AccessKind.Feed or AccessKind.System => StoreSet.Everything,
        _ => StoreSet.Empty,
    };

    /// <summary>The stores this context may write in the store-scoped tables (writable_store_ids()).</summary>
    public StoreSet WriteScope => Kind switch
    {
        AccessKind.User => AccessMatrix.WriteScopeFor(User!),
        AccessKind.System => StoreSet.Everything,
        _ => StoreSet.Empty,
    };

    public override string ToString() => Kind switch
    {
        AccessKind.User => $"user {User!.Username} ({User.Tier.Key()})",
        AccessKind.Kiosk => $"kiosk for store {KioskStoreId}",
        AccessKind.Feed => $"data feed token {FeedTokenId}",
        AccessKind.System => $"system ({Reason})",
        _ => "anonymous",
    };
}

/// <summary>Supplies the access context for the current request or circuit (set by sign-in).</summary>
public interface IAccessContextAccessor
{
    AccessContext Current { get; }
}

/// <summary>A settable accessor: the web layer sets <see cref="Current"/> once it knows the user.</summary>
public sealed class AccessContextAccessor : IAccessContextAccessor
{
    public AccessContext Current { get; set; } = AccessContext.Anonymous;
}

/// <summary>A write the access context may not make. The equivalent of an RLS WITH CHECK failure.</summary>
public sealed class ScopeViolationException(string message) : InvalidOperationException(message);
