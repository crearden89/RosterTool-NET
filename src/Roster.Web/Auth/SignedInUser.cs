// New: the signed-in person as the sign-in cookie carries them (the TypeScript
// app's SessionUser, src/lib/auth.ts). Pages read who may see which store from the
// data layer's store scope; the shell reads this.

using System.Globalization;
using System.Security.Claims;

namespace Roster.Web.Auth;

internal sealed record SignedInUser
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public string DisplayName { get; init; } = "";

    /// <summary>The stored tier key ("super_user", "store", "pa", "director", "emso").</summary>
    public required string Tier { get; init; }

    public Guid? StoreId { get; init; }
    public Guid? RegionId { get; init; }
    public Guid? AreaId { get; init; }
    public Guid? DirectorGroupId { get; init; }

    public bool IsReadOnly => TierKeys.IsReadOnly(Tier);

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    public IEnumerable<Claim> ToClaims()
    {
        yield return new Claim(RosterClaims.UserId, Id.ToString());
        yield return new Claim(RosterClaims.Username, Username);
        yield return new Claim(RosterClaims.DisplayName, Name);
        yield return new Claim(RosterClaims.Tier, Tier);
        if (StoreId is { } store) yield return new Claim(RosterClaims.StoreId, store.ToString());
        if (RegionId is { } region) yield return new Claim(RosterClaims.RegionId, region.ToString());
        if (AreaId is { } area) yield return new Claim(RosterClaims.AreaId, area.ToString());
        if (DirectorGroupId is { } group) yield return new Claim(RosterClaims.DirectorGroupId, group.ToString());
    }

    /// <summary>The user a principal carries; null when not signed in, or signed in without access.</summary>
    public static SignedInUser? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        if (!TierKeys.TryNormalize(principal.FindFirst(RosterClaims.Tier)?.Value, out var tier)) return null;
        if (!Guid.TryParse(principal.FindFirst(RosterClaims.UserId)?.Value, out var id)) return null;
        return new SignedInUser
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
