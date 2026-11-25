// New: sign-in by Auth:Mode - Entra ID single sign-on, Windows Authentication, or the
// development sign-in - replacing the TypeScript app's Supabase username/password
// login (src/app/login, src/lib/auth.ts).

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Roster.Data.Users;
using Roster.Web.Kiosk;

namespace Roster.Web.Auth;

/// <summary>
/// Sign-in and authorization, by <c>Auth:Mode</c>:
/// <list type="bullet">
/// <item><b>EntraId</b> (default): OpenID Connect with Microsoft Entra ID through
/// Microsoft.Identity.Web (<c>Auth:EntraId</c>).</item>
/// <item><b>Windows</b>: Windows Authentication (Negotiate) at
/// <c>/account/windows-sign-in</c>, for on-premises AD; works on Kestrel and
/// under IIS.</item>
/// <item><b>Development</b>: pick a tier and a store at <c>/dev/sign-in</c>; refused
/// at startup outside the Development environment.</item>
/// </list>
/// Every mode ends the same way: the person is resolved once to the app's user
/// (<see cref="AppUserResolver"/>) and kept in the app's sign-in cookie, which
/// the pages, the policies and the interactive connection all read.
/// </summary>
public static class AuthSetup
{
    public const string CookieName = ".Roster.Session";

    public static WebApplicationBuilder AddRosterAuth(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.Configure<KioskOptions>(configuration.GetSection(KioskOptions.Section));

        var mode = configuration.GetSection(AuthOptions.Section).GetValue(nameof(AuthOptions.Mode), AuthMode.EntraId);
        if (mode == AuthMode.Development && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException(
                "Auth:Mode is Development, which is only allowed when ASPNETCORE_ENVIRONMENT is Development. " +
                "Set Auth:Mode to EntraId or Windows.");

        switch (mode)
        {
            case AuthMode.EntraId:
                services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                    .AddMicrosoftIdentityWebApp(configuration.GetSection("Auth:EntraId"),
                        OpenIdConnectDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme);
                // After Microsoft.Identity.Web's own handler: swap the token's
                // principal for the app's (who they are, and their app user).
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                {
                    var theirs = o.Events.OnTokenValidated;
                    o.Events.OnTokenValidated = async context =>
                    {
                        if (theirs is not null) await theirs(context);
                        if (context.Principal is null) return;
                        context.Principal = await SignInPrincipalAsync(context.HttpContext, EntraIdentity, context.Principal);
                    };
                });
                break;

            case AuthMode.Windows:
                services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie()
                    .AddNegotiate();
                break;

            case AuthMode.Development:
                services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie();
                break;
        }

        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.SlidingExpiration = true;
            o.LoginPath = mode switch
            {
                AuthMode.Windows => "/account/windows-sign-in",
                AuthMode.Development => "/dev/sign-in",
                _ => "/account/sign-in",
            };
            o.LogoutPath = "/account/sign-out";
            o.AccessDeniedPath = "/account/no-access";
            o.ReturnUrlParameter = "returnUrl";
        });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((o, auth) => o.ExpireTimeSpan = auth.Value.SessionLifetime);

        services.AddAuthorization(Policies.Configure);
        services.AddSingleton<IAuthorizationHandler, KioskAccessHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, PageAuthorizationResultHandler>();
        services.AddCascadingAuthenticationState();
        services.TryAddScoped<AppUserResolver>();
        services.TryAddScoped<IKioskLinkCheck, KioskLinkCheck>();
        return builder;
    }

    /// <summary>
    /// The principal the sign-in cookie keeps for someone the company sign-in has
    /// just identified: their app user when they have access, and the kiosk
    /// device groups they are in.
    /// </summary>
    internal static async Task<ClaimsPrincipal> SignInPrincipalAsync(
        HttpContext http, Func<ClaimsPrincipal, AuthOptions, SignInIdentity> identify, ClaimsPrincipal external)
    {
        var services = http.RequestServices;
        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var kiosk = services.GetRequiredService<IOptions<KioskOptions>>().Value;
        var identity = identify(external, auth);
        var user = await services.GetRequiredService<AppUserResolver>().ResolveAsync(identity, http.RequestAborted);

        var extra = new List<Claim>();
        foreach (var group in kiosk.DeviceGroups.Where(g => GroupMembership.Has(external, g)))
            extra.Add(new Claim(RosterClaims.DeviceGroup, group));
        // Kept for a clean Entra ID sign-out (the account to sign out of).
        foreach (var type in new[] { "login_hint", "sid" })
            if (external.FindFirst(type) is { } c) extra.Add(new Claim(type, c.Value));
        return AppUserResolver.CookiePrincipal(identity, user, extra);
    }

    internal static SignInIdentity EntraIdentity(ClaimsPrincipal p, AuthOptions auth)
    {
        string? First(params string[] types) => types.Select(t => p.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var objectId = First(ClaimConstants.ObjectId, ClaimConstants.Oid);
        return new SignInIdentity
        {
            Method = "entra",
            Username = First(ClaimConstants.PreferredUserName, ClaimTypes.Upn, "upn", ClaimTypes.Email, "email") ?? p.Identity?.Name ?? objectId ?? "",
            ObjectId = objectId,
            Email = First(ClaimTypes.Email, "email"),
            DisplayName = First(ClaimConstants.Name, ClaimTypes.GivenName),
            GroupTier = GroupTier(auth, p),
        };
    }

    internal static SignInIdentity WindowsIdentity(ClaimsPrincipal p, AuthOptions auth)
    {
        var account = p.Identity?.Name ?? "";
        var slash = account.IndexOf('\\');
        return new SignInIdentity
        {
            Method = "windows",
            Username = slash >= 0 ? account[(slash + 1)..] : account,
            AccountName = account,
            Email = p.FindFirst(ClaimTypes.Upn)?.Value ?? p.FindFirst(ClaimTypes.Email)?.Value,
            GroupTier = GroupTier(auth, p),
        };
    }

    private static Roster.Data.Entities.Tier? GroupTier(AuthOptions auth, ClaimsPrincipal p) =>
        auth.TierFromGroups(p) is { } key ? TierKeys.ToTier(key) : null;

    /// <summary>A return address that stays inside the app.</summary>
    internal static string LocalReturnUrl(HttpContext http, string? returnUrl)
    {
        var fallback = $"{http.Request.PathBase}/";
        if (string.IsNullOrWhiteSpace(returnUrl)) return fallback;
        // Only an app-relative path: "/x" but not "//host" or "/\host".
        if (returnUrl[0] != '/' || (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))) return fallback;
        return returnUrl;
    }

    internal static string NegotiateScheme => NegotiateDefaults.AuthenticationScheme;
}
