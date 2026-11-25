// New: the claims the app's sign-in cookie carries, whatever the sign-in (Entra ID,
// Windows or the development sign-in), so the pages and policies read one shape.

namespace Roster.Web.Auth;

/// <summary>
/// The claim types the app's own facts about a signed-in person travel in. Every
/// sign-in mode ends with one cookie holding these; the policies
/// (<see cref="Policies"/>) read them.
/// </summary>
public static class RosterClaims
{
    /// <summary>The person's app_users id.</summary>
    public const string UserId = "roster:user_id";

    /// <summary>The login name the directory knows them by.</summary>
    public const string Username = "roster:username";

    /// <summary>The name the menu shows.</summary>
    public const string DisplayName = "roster:display_name";

    /// <summary>The tier, as stored: "super_user", "store", "pa", "director" (DSO) or "emso".</summary>
    public const string Tier = "roster:tier";

    public const string StoreId = "roster:store_id";
    public const string RegionId = "roster:region_id";
    public const string AreaId = "roster:area_id";
    public const string DirectorGroupId = "roster:director_group_id";

    /// <summary>How the person signed in: "entra", "windows" or "development".</summary>
    public const string SignInMethod = "roster:sign_in";

    /// <summary>A kiosk device group (<c>Kiosk:DeviceGroups</c>) the person was in at sign-in.</summary>
    public const string DeviceGroup = "roster:device_group";

    /// <summary>The authentication type of the identity in the sign-in cookie.</summary>
    public const string AuthenticationType = "Roster";
}
