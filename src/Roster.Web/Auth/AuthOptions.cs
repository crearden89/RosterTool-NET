using System.Security.Claims;

namespace Roster.Web.Auth;

/// <summary>How people sign in (<c>Auth:Mode</c>).</summary>
public enum AuthMode
{
    /// <summary>Single sign-on through Microsoft Entra ID (OpenID Connect). The default.</summary>
    EntraId,

    /// <summary>Windows Authentication (Kerberos/NTLM through Negotiate), for on-premises AD.</summary>
    Windows,

    /// <summary>Pick a tier and a store at <c>/dev/sign-in</c>. Only allowed in the Development environment.</summary>
    Development,
}

/// <summary>The <c>Auth</c> section of appsettings.json.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public AuthMode Mode { get; set; } = AuthMode.EntraId;

    /// <summary>
    /// The AD groups that give each tier, by tier name ("SuperUser", "Store",
    /// "Pa", "Director" (DSO), "Emso"). A value matches a role the sign-in
    /// carries: an Entra ID app role value ("Roster.SuperUser") or group object
    /// id, or a Windows group ("CORP\Roster Super Users"). When any tier has
    /// groups here, the groups decide the tier and someone in none of them has no
    /// access; when none has, the tier the directory holds is used. Someone in
    /// several groups gets the first in the order SuperUser, Emso, Director, Pa,
    /// Store.
    /// </summary>
    public Dictionary<string, string[]> TierGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a sign-in lasts without use before the company sign-in is asked again (sliding).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Whether any tier has groups, so the groups decide the tier.</summary>
    public bool GroupsDecideTier => TierGroups.Values.Any(g => g is { Length: > 0 });

    // The order a person in several tier groups is placed by: the widest reach first.
    private static readonly Tier[] GroupPriority = [Tier.SuperUser, Tier.Emso, Tier.Director, Tier.Pa, Tier.Store];

    /// <summary>The tier the principal's groups give, or null when none of them is mapped.</summary>
    public Tier? TierFromGroups(ClaimsPrincipal principal)
    {
        foreach (var tier in GroupPriority)
        {
            if (!TryGroupsFor(tier, out var groups)) continue;
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group)) continue;
                if (GroupMembership.Has(principal, group)) return tier;
            }
        }
        return null;
    }

    private bool TryGroupsFor(Tier tier, out string[] groups)
    {
        if (TierGroups.TryGetValue(tier.ToString(), out groups!) && groups is { Length: > 0 }) return true;
        if (TierGroups.TryGetValue(tier.Key(), out groups!) && groups is { Length: > 0 }) return true;
        groups = [];
        return false;
    }
}

/// <summary>Whether a sign-in carries a group or role.</summary>
public static class GroupMembership
{
    // The claim types a group or role can arrive in: Entra ID app roles and
    // group ids, the standard role claim (Windows groups resolved over LDAP, or
    // Entra roles mapped by the handler) and Windows group SIDs.
    private static readonly string[] GroupClaimTypes =
        ["roles", "groups", ClaimTypes.Role, ClaimTypes.GroupSid];

    /// <summary>
    /// True when the principal carries the group: as a claim value (an Entra ID
    /// app role or group id, a role, a group SID), or, for a Windows principal,
    /// as a group name the domain resolves ("CORP\Roster Super Users").
    /// </summary>
    public static bool Has(ClaimsPrincipal principal, string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return false;
        foreach (var claim in principal.Claims)
        {
            if (GroupClaimTypes.Contains(claim.Type) && string.Equals(claim.Value, group, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        try
        {
            return principal.IsInRole(group);
        }
        catch (SystemException)
        {
            // A name the domain cannot translate is not a group the person is in.
            return false;
        }
    }
}
