// New: the kiosk link check. The TypeScript app's Floor Display and Load Planner
// (src/app/floor/[token], src/app/load/[token]) find their store by the link's
// token and show nothing else (notFound) for an unknown one; here that check, and
// the switch to require a device sign-in, are shared by both pages (owner P9).

using Roster.Data.Scope;
using Roster.Web.Auth;

namespace Roster.Web.Kiosk;

/// <summary>
/// The <c>Kiosk</c> section of appsettings. The kiosk pages are reached by each
/// store's secret link (<c>/floor/{token}</c>, <c>/load/{token}</c>), pinned on the
/// store's tablet, with no sign-in. If security asks for one,
/// <see cref="RequireDeviceSignIn"/> makes the tablet sign in first, through the
/// same sign-in as the rest of the app; the link still picks the store.
/// </summary>
public sealed class KioskOptions
{
    public const string Section = "Kiosk";

    /// <summary>Require a sign-in on the kiosk pages (the link still picks the store).</summary>
    public bool RequireDeviceSignIn { get; set; }

    /// <summary>
    /// With <see cref="RequireDeviceSignIn"/>: the groups a kiosk device's account
    /// must be in (any of them). Empty: any signed-in account.
    /// </summary>
    public string[] DeviceGroups { get; set; } = [];
}

/// <summary>The store a kiosk link opens.</summary>
/// <param name="State">The store's state, which sets its clock (Roster.Engine.Core.StoreTime).</param>
public sealed record KioskStore(Guid StoreId, string Code, string Name, string? State);

/// <summary>What a kiosk page does with its link.</summary>
public enum KioskLinkStatus
{
    /// <summary>Show the store's screen.</summary>
    Open,

    /// <summary>A device sign-in is required and this device has not signed in: go to the sign-in.</summary>
    SignInRequired,

    /// <summary>An unknown link, or a signed-in account that is not a kiosk device: show the not-found page.</summary>
    NotFound,
}

/// <param name="Store">The link's store when <see cref="Status"/> is Open.</param>
/// <param name="NoLoginNeeded">The kiosk pages are open to anyone with the link (the header's "No login needed" pill).</param>
public sealed record KioskLink(KioskLinkStatus Status, KioskStore? Store, bool NoLoginNeeded);

/// <summary>
/// The kiosk pages' link check (Floor Display and Load Planner, owner P9). A
/// kiosk page is <c>@attribute [AllowAnonymous]</c> and starts with:
/// <code>
/// var link = await KioskLinks.CheckAsync(Token);
/// if (link.Status == KioskLinkStatus.NotFound) { Navigation.NotFound(); return; }
/// if (link.Status == KioskLinkStatus.SignInRequired) { Navigation.NavigateTo(KioskLinks.SignInUrl(Navigation), forceLoad: true); return; }
/// </code>
/// A kiosk HTTP endpoint uses <c>.RequireAuthorization(Policies.Kiosk)</c> for the
/// same device rule, and calls <see cref="CheckAsync"/> for the store.
/// </summary>
public interface IKioskLinkCheck
{
    /// <summary>Checks a link's token, and the device sign-in when <c>Kiosk:RequireDeviceSignIn</c> is on.</summary>
    Task<KioskLink> CheckAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>The sign-in address that comes back to the current page.</summary>
    string SignInUrl(Microsoft.AspNetCore.Components.NavigationManager navigation);
}

internal sealed class KioskLinkCheck(
    IStoreDirectory stores,
    AccessContextAccessor access,
    Microsoft.Extensions.Options.IOptionsMonitor<KioskOptions> options,
    Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider authentication) : IKioskLinkCheck
{
    public async Task<KioskLink> CheckAsync(string? token, CancellationToken cancellationToken = default)
    {
        var kiosk = options.CurrentValue;
        var open = !kiosk.RequireDeviceSignIn;
        if (string.IsNullOrWhiteSpace(token)) return new KioskLink(KioskLinkStatus.NotFound, null, open);

        if (!open)
        {
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            if (user.Identity?.IsAuthenticated != true) return new KioskLink(KioskLinkStatus.SignInRequired, null, open);
            if (!KioskAccessHandler.IsKioskDevice(user, kiosk)) return new KioskLink(KioskLinkStatus.NotFound, null, open);
        }

        var store = await stores.FindByKioskTokenAsync(token.Trim(), cancellationToken);
        if (store is null) return new KioskLink(KioskLinkStatus.NotFound, null, open);

        // From here the page reads and writes as that store's kiosk (the data layer's
        // kiosk access), whoever the device is signed in as.
        access.Current = AccessContext.ForKiosk(store.Id);
        return new KioskLink(KioskLinkStatus.Open, new KioskStore(store.Id, store.Code, store.Name, store.State), open);
    }

    public string SignInUrl(Microsoft.AspNetCore.Components.NavigationManager navigation) =>
        $"account/sign-in?returnUrl={Uri.EscapeDataString(new Uri(navigation.Uri).PathAndQuery)}";
}
