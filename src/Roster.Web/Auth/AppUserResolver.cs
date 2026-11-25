// New: turns a company sign-in into the app's user, once per sign-in (the
// TypeScript app's getSessionUser, src/lib/auth.ts, ran per request against
// app_users; here the answer is kept in the sign-in cookie).

using System.Security.Claims;
using Microsoft.Extensions.Options;
using Roster.Data.Users;

namespace Roster.Web.Auth;

internal sealed class AppUserResolver(IUserDirectory directory, IOptions<AuthOptions> options, ILogger<AppUserResolver> logger)
{
    /// <summary>
    /// The app user, or null when the person has no access: not in the directory,
    /// or (when groups decide the tier) in none of the tier groups.
    /// </summary>
    public async Task<SignedInUser?> ResolveAsync(SignInIdentity identity, CancellationToken cancellationToken = default)
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

        var signedIn = SignedInUser.From(user);
        if (groupsDecide) signedIn = signedIn with { Tier = Roster.Data.Entities.Tiers.Key(identity.GroupTier!.Value) };
        if (string.IsNullOrWhiteSpace(signedIn.DisplayName) && !string.IsNullOrWhiteSpace(identity.DisplayName))
            signedIn = signedIn with { DisplayName = identity.DisplayName };
        return signedIn;
    }

    /// <summary>
    /// The principal the sign-in cookie keeps: who signed in and how, and, when
    /// they have access, their app user. Someone without access still gets a
    /// signed-in principal, so the app can say who they are signed in as.
    /// </summary>
    public static ClaimsPrincipal CookiePrincipal(SignInIdentity identity, SignedInUser? user, IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user?.Username ?? identity.Username),
            new(RosterClaims.SignInMethod, identity.Method),
        };
        if (user is not null) claims.AddRange(user.ToClaims());
        else if (!string.IsNullOrWhiteSpace(identity.DisplayName)) claims.Add(new Claim(RosterClaims.DisplayName, identity.DisplayName));
        if (extra is not null) claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, RosterClaims.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}
