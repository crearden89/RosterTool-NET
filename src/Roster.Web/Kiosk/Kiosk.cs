using Roster.Web.Auth;

namespace Roster.Web.Kiosk;

/// <summary>
/// The <c>Kiosk</c> section of appsettings. The Floor Display and the Load
/// Planner are reached by each store's secret link (<c>/floor/{token}</c>,
/// <c>/load/{token}</c>), pinned on the store's tablet, with no sign-in. If
/// security asks for one, <see cref="RequireDeviceSignIn"/> makes the tablet
/// sign in first, through the same sign-in as the rest of the app.
/// </summary>
public sealed class KioskOptions
{
    public const string Section = "Kiosk";

    /// <summary>Require a sign-in on the kiosk pages (still by the store's link).</summary>
    public bool RequireDeviceSignIn { get; set; }

    /// <summary>
    /// With <see cref="RequireDeviceSignIn"/>: the groups a kiosk device's
    /// account must be in (any of them). Empty: any signed-in account.
    /// </summary>
    public string[] DeviceGroups { get; set; } = [];
}

/// <summary>The store a kiosk link opens.</summary>
public sealed record KioskStore(StoreInfo Store);

/// <summary>
/// Finds the store a kiosk link belongs to. The data layer provides the real
/// one over the stores' kiosk tokens; until then
/// <see cref="ConfigurationDirectory"/> reads <c>Directory:Stores[].KioskToken</c>.
/// </summary>
public interface IKioskDirectory
{
    /// <summary>The store whose kiosk link carries this token, or null for an unknown link.</summary>
    Task<KioskStore?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);
}
