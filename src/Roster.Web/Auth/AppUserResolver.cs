using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Roster.Web.Auth;

/// <summary>
/// Turns a company sign-in into the app's user: what the sign-in says
/// (<see cref="SignInIdentity"/>), the tier from the person's groups, and the
/// person's record in the <see cref="IUserDirectory"/>. Called once per
/// sign-in; the answer is kept in the sign-in cookie.
/// </summary>
public sealed class AppUserResolver(IUserDirectory directory, IOptions<AuthOptions> options, ILogger<AppUserResolver> logger)
{
    /// <summary>
    /// The app user, or null when the person has no access: not in the
    /// directory, or (when groups decide the tier) in none of the tier groups.
    /// </summary>
    public async Task<AppUser?> ResolveAsync(SignInIdentity identity, CancellationToken cancellationToken = default)
    {
        var groupsDecide = options.Value.GroupsDecideTier;
        if (groupsDecide && identity.GroupTier is null)
        {
            logger.LogInformation("Sign-in by {Username} refused: in none of the tier groups.", identity.Username);
            return null;
        }

        var user = await directory.FindAsync(identity, cancellationToken);
        if (user is null)
        {
            logger.LogInformation("Sign-in by {Username} refused: not in the user directory.", identity.Username);
            return null;
        }

        if (groupsDecide) user = user with { Tier = identity.GroupTier!.Value };
        if (string.IsNullOrWhiteSpace(user.DisplayName) && !string.IsNullOrWhiteSpace(identity.DisplayName))
            user = user with { DisplayName = identity.DisplayName };
        return user;
    }

    /// <summary>
    /// The principal the sign-in cookie keeps: who signed in and how, and, when
    /// they have access, their app user. Someone without access still gets a
    /// signed-in principal, so the app can say who they are signed in as.
    /// </summary>
    public static ClaimsPrincipal CookiePrincipal(SignInIdentity identity, AppUser? user, IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user?.Username ?? identity.Username),
            new(RosterClaims.SignInMethod, identity.Method),
        };
        if (!string.IsNullOrWhiteSpace(identity.DisplayName) && user is null)
            claims.Add(new Claim(RosterClaims.DisplayName, identity.DisplayName));
        if (user is not null) claims.AddRange(user.ToClaims());
        if (extra is not null) claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, RosterClaims.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}
