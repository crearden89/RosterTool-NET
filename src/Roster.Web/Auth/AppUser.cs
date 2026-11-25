using System.Globalization;
using System.Security.Claims;

namespace Roster.Web.Auth;

/// <summary>
/// The signed-in person as the app knows them: their tier and the scope the
/// tier applies to (the TypeScript app's <c>SessionUser</c>, one row of
/// <c>app_users</c>). Made once at sign-in by <see cref="AppUserResolver"/> and
/// carried in the sign-in cookie as <see cref="RosterClaims"/>.
/// </summary>
public sealed record AppUser
{
    public required Guid Id { get; init; }

    /// <summary>The login name the directory knows them by.</summary>
    public required string Username { get; init; }

    /// <summary>The name to show; the username when the directory has no other.</summary>
    public string DisplayName { get; init; } = "";

    public required Tier Tier { get; init; }

    /// <summary>The store a Store-tier user manages.</summary>
    public Guid? StoreId { get; init; }

    public Guid? RegionId { get; init; }

    /// <summary>The area an EMSO writes to.</summary>
    public Guid? AreaId { get; init; }

    public Guid? DirectorGroupId { get; init; }

    /// <summary>PA and DSO: they see their scope and change nothing.</summary>
    public bool IsReadOnly => Tier.IsReadOnly();

    /// <summary>The name for the menu and headers.</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    /// <summary>The claims this user travels in.</summary>
    public IEnumerable<Claim> ToClaims()
    {
        yield return new Claim(RosterClaims.UserId, Id.ToString());
        yield return new Claim(RosterClaims.Username, Username);
        yield return new Claim(RosterClaims.DisplayName, Name);
        yield return new Claim(RosterClaims.Tier, Tier.Key());
        if (StoreId is { } store) yield return new Claim(RosterClaims.StoreId, store.ToString());
        if (RegionId is { } region) yield return new Claim(RosterClaims.RegionId, region.ToString());
        if (AreaId is { } area) yield return new Claim(RosterClaims.AreaId, area.ToString());
        if (DirectorGroupId is { } group) yield return new Claim(RosterClaims.DirectorGroupId, group.ToString());
    }

    /// <summary>
    /// The app user a principal carries, or null when it carries none: not signed
    /// in, or signed in without access to the app.
    /// </summary>
    public static AppUser? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        if (!Tiers.TryParse(principal.FindFirst(RosterClaims.Tier)?.Value, out var tier)) return null;
        if (!Guid.TryParse(principal.FindFirst(RosterClaims.UserId)?.Value, out var id)) return null;
        return new AppUser
        {
            Id = id,
            Username = principal.FindFirst(RosterClaims.Username)?.Value ?? principal.Identity?.Name ?? "",
            DisplayName = principal.FindFirst(RosterClaims.DisplayName)?.Value ?? "",
            Tier = tier,
            StoreId = GuidClaim(principal, RosterClaims.StoreId),
            RegionId = GuidClaim(principal, RosterClaims.RegionId),
            AreaId = GuidClaim(principal, RosterClaims.AreaId),
            DirectorGroupId = GuidClaim(principal, RosterClaims.DirectorGroupId),
        };
    }

    private static Guid? GuidClaim(ClaimsPrincipal principal, string type) =>
        Guid.TryParse(principal.FindFirst(type)?.Value, CultureInfo.InvariantCulture, out var g) ? g : null;
}

/// <summary>The claim types the app's own facts about a user travel in.</summary>
public static class RosterClaims
{
    public const string UserId = "roster:user_id";
    public const string Username = "roster:username";
    public const string DisplayName = "roster:display_name";
    public const string Tier = "roster:tier";
    public const string StoreId = "roster:store_id";
    public const string RegionId = "roster:region_id";
    public const string AreaId = "roster:area_id";
    public const string DirectorGroupId = "roster:director_group_id";

    /// <summary>How the person signed in: "entra", "windows" or "development".</summary>
    public const string SignInMethod = "roster:sign_in";

    /// <summary>A kiosk device group (<c>Kiosk:DeviceGroups</c>) the person was in at sign-in.</summary>
    public const string DeviceGroup = "roster:device_group";

    /// <summary>The authentication type of the identity the sign-in cookie holds.</summary>
    public const string AuthenticationType = "Roster";
}
