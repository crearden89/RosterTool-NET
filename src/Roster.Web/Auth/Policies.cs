using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.Extensions.Options;
using Roster.Web.Kiosk;

namespace Roster.Web.Auth;

/// <summary>
/// The authorization policies. A page with a plain <c>[Authorize]</c> (every
/// page under Components/Pages, through its _Imports.razor) needs someone with
/// access to the app; a Super User page adds <c>[Authorize(Policy = Policies.SuperUser)]</c>.
/// Store scope is not a policy: a page asks <see cref="StoreScope"/>.
/// </summary>
public static class Policies
{
    /// <summary>Signed in with access to the app (an app user with a tier). The default policy.</summary>
    public const string SignedIn = "Roster.SignedIn";

    /// <summary>A tier that can write somewhere: Super User, Store or EMSO (not PA or DSO).</summary>
    public const string Writer = "Roster.Writer";

    public const string SuperUser = "Roster.SuperUser";
    public const string Store = "Roster.Store";
    public const string Emso = "Roster.Emso";
    public const string Pa = "Roster.Pa";

    /// <summary>The DSO tier (stored as "director").</summary>
    public const string Director = "Roster.Director";

    /// <summary>The kiosk pages: anyone with the link, or a signed-in device when <c>Kiosk:RequireDeviceSignIn</c> is on.</summary>
    public const string Kiosk = "Roster.Kiosk";

    /// <summary>The policy name for one tier.</summary>
    public static string For(Tier tier) => tier switch
    {
        Tier.SuperUser => SuperUser,
        Tier.Store => Store,
        Tier.Emso => Emso,
        Tier.Pa => Pa,
        Tier.Director => Director,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    internal static void Configure(AuthorizationOptions options)
    {
        var signedIn = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(RosterClaims.Tier)
            .Build();
        options.DefaultPolicy = signedIn;
        options.AddPolicy(SignedIn, signedIn);
        options.AddPolicy(Writer, p => p.Combine(signedIn).RequireAssertion(c => AppUser.FromPrincipal(c.User) is { IsReadOnly: false }));
        foreach (var tier in Tiers.All)
            options.AddPolicy(For(tier), p => p.Combine(signedIn).RequireClaim(RosterClaims.Tier, tier.Key()));
        options.AddPolicy(Kiosk, p => p.AddRequirements(new KioskAccessRequirement()));
    }
}

/// <summary>The kiosk pages' rule, read from <see cref="KioskOptions"/> on every request.</summary>
public sealed class KioskAccessRequirement : IAuthorizationRequirement;

internal sealed class KioskAccessHandler(IOptionsMonitor<KioskOptions> kiosk, IOptions<AuthOptions> auth) : AuthorizationHandler<KioskAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, KioskAccessRequirement requirement)
    {
        var options = kiosk.CurrentValue;
        if (!options.RequireDeviceSignIn)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        var groups = options.DeviceGroups.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        if (groups.Length == 0 || groups.Any(g => context.User.HasClaim(RosterClaims.DeviceGroup, g)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// What a refused page request does, the way the TypeScript app does it: not
/// signed in goes to the sign-in (the scheme's challenge); signed in without
/// access to the app goes to "No access"; signed in without the tier a page
/// needs goes back to the Dashboard (<c>requireTier</c>). Anything that is not a
/// page (a form post, the app's connection) gets the plain 401 or 403.
/// </summary>
internal sealed class PageAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && IsPage(context))
        {
            var hasAccess = context.User.HasClaim(c => c.Type == RosterClaims.Tier);
            context.Response.Redirect($"{context.Request.PathBase}{(hasAccess ? "/overview" : "/account/no-access")}");
            return Task.CompletedTask;
        }
        return _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool IsPage(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;
}
