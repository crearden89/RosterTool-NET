using Microsoft.AspNetCore.Components.Authorization;

namespace Roster.Web.Auth;

/// <summary>The signed-in app user, for pages and the services they call.</summary>
public sealed class CurrentUser(AuthenticationStateProvider authentication)
{
    /// <summary>The app user, or null when no one with access is signed in.</summary>
    public async Task<AppUser?> GetAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return AppUser.FromPrincipal(state.User);
    }
}

/// <summary>
/// The store the person last chose in any store picker, which every other page
/// then opens on (the TypeScript app's <c>roster_store_id</c> cookie). App.razor
/// reads the cookie on each full page load; the store pickers set it.
/// </summary>
public sealed class StoreSelection
{
    public const string CookieName = "roster_store_id";

    public Guid? Remembered { get; private set; }

    /// <summary>Takes the cookie's value at the start of a page load or a connection.</summary>
    public void Load(string? cookieValue)
    {
        if (Remembered is null && Guid.TryParse(cookieValue, out var id)) Remembered = id;
    }

    /// <summary>Remembers a choice for the rest of this connection (the picker also writes the cookie).</summary>
    public void Remember(Guid storeId) => Remembered = storeId;
}

/// <summary>Which stores a page offers.</summary>
public enum StoreList
{
    /// <summary>
    /// A private page (Roster, Setup, Analysis, Sales, Weekends...): only the
    /// stores the person may see (<c>visible_store_ids()</c>), and a Store-tier
    /// user always on their own store.
    /// </summary>
    Visible,

    /// <summary>
    /// A peer-visible page (Plan structure, Non-Op, dashboards): every store, the
    /// person's own store first unless they asked for another; other stores open
    /// read-only as <see cref="StoreAccess.Peer"/>.
    /// </summary>
    Network,
}

/// <summary>What a page knows about its store once the scope check has run.</summary>
public sealed record StorePage
{
    public required AppUser User { get; init; }

    /// <summary>The store the page shows; null when the person has no store in scope.</summary>
    public required StoreInfo? Store { get; init; }

    /// <summary>The stores the page's store picker offers.</summary>
    public required IReadOnlyList<StoreInfo> Stores { get; init; }

    /// <summary>How the person relates to <see cref="Store"/>; null without a store.</summary>
    public required StoreAccess? Access { get; init; }

    /// <summary>The page may show its write controls for this store.</summary>
    public bool CanWrite => Store is not null && Access == StoreAccess.Write;

    /// <summary>Another store's page, opened in its trimmed peer form.</summary>
    public bool IsPeer => Access == StoreAccess.Peer;

    /// <summary>A store picker is shown only to someone who sees more than one store.</summary>
    public bool ShowStorePicker => Stores.Count > 1;
}

/// <summary>
/// The store-scope check every page calls: which store the page shows, which
/// stores its picker offers, and whether the person may change it. The read
/// side mirrors <c>visible_store_ids()</c>; the write side the TypeScript app's
/// <c>access.ts</c> and the guards in <c>authz.ts</c>, with their messages.
/// </summary>
public sealed class StoreScope(CurrentUser currentUser, IStoreDirectory stores, StoreSelection selection)
{
    public const string DeniedReadOnly = "Your access level is read-only - this change was not saved.";
    public const string DeniedScope = "You don't have access to that store.";
    public const string DeniedAuth = "You are not signed in.";

    /// <summary>
    /// Resolves the page's store: the one asked for (<c>?store=</c>) when it is in
    /// scope, else the one last chosen, else the first in scope. A Store-tier user
    /// on a private page is always on their own store. Null when no one with
    /// access is signed in.
    /// </summary>
    public async Task<StorePage?> ResolveAsync(string? requestedStoreId, StoreList list = StoreList.Visible, CancellationToken cancellationToken = default)
    {
        var user = await currentUser.GetAsync();
        if (user is null) return null;

        var all = await stores.ListAsync(cancellationToken);
        var offered = list == StoreList.Network ? all : all.Where(s => AccessRules.StoreVisibleTo(user, s)).ToList();
        Guid? requested = Guid.TryParse(requestedStoreId, out var r) ? r : null;

        StoreInfo? store;
        if (list == StoreList.Visible && user.Tier == Tier.Store)
            store = offered.FirstOrDefault(s => s.Id == user.StoreId);
        else if (list == StoreList.Network && requested is null && user.StoreId is { } own && all.Any(s => s.Id == own))
            store = all.First(s => s.Id == own);
        else
            store = Pick(requested, offered);

        return new StorePage
        {
            User = user,
            Store = store,
            Stores = offered,
            Access = store is null ? null : AccessRules.StoreAccessFor(user, store),
        };
    }

    /// <summary>
    /// Guards a write against a store: null when it is allowed, otherwise the
    /// reason, worded as the TypeScript app words it (<c>denyStoreWrite</c>).
    /// </summary>
    public async Task<string?> DenyStoreWriteAsync(Guid? storeId, CancellationToken cancellationToken = default)
    {
        var user = await currentUser.GetAsync();
        if (user is null) return DeniedAuth;
        if (user.IsReadOnly) return DeniedReadOnly;
        if (storeId is not { } id) return DeniedScope;
        var store = (await stores.ListAsync(cancellationToken)).FirstOrDefault(s => s.Id == id)
            ?? new StoreInfo { Id = id, Code = "", Name = "" };
        return AccessRules.StoreInWriteScope(AccessRules.WriteScopeFor(user), store) ? null : DeniedScope;
    }

    /// <summary>Guards a write not tied to one store (<c>denyWrite</c>): null when allowed.</summary>
    public async Task<string?> DenyWriteAsync()
    {
        var user = await currentUser.GetAsync();
        if (user is null) return DeniedAuth;
        return user.IsReadOnly ? DeniedReadOnly : null;
    }

    private StoreInfo? Pick(Guid? requested, IReadOnlyList<StoreInfo> offered)
    {
        if (requested is { } r && offered.FirstOrDefault(s => s.Id == r) is { } asked) return asked;
        if (selection.Remembered is { } m && offered.FirstOrDefault(s => s.Id == m) is { } remembered) return remembered;
        return offered.Count > 0 ? offered[0] : null;
    }
}
