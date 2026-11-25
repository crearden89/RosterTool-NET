// New: the sign-in and sign-out addresses for every Auth:Mode, and the development
// sign-in's form post. The TypeScript app signed in with a username and password
// (src/app/login/LoginForm.tsx) and out with SignOutButton.tsx.

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Roster.Data.Scope;
using Roster.Data.Users;

namespace Roster.Web.Auth;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var mode = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value.Mode;
        var account = app.MapGroup("/account").WithMetadata(new SkipStatusCodePagesAttribute());

        // Where a page sends someone it cannot let in: the configured sign-in,
        // then back to the page.
        account.MapGet("/sign-in", (HttpContext http, string? returnUrl) =>
        {
            var target = AuthSetup.LocalReturnUrl(http, returnUrl);
            return http.User.Identity?.IsAuthenticated == true
                ? Results.LocalRedirect(target)
                : Results.Challenge(new AuthenticationProperties { RedirectUri = target });
        }).AllowAnonymous();

        if (mode == AuthMode.Windows)
        {
            // Windows Authentication: the browser answers the Negotiate challenge
            // with the person's Windows sign-in, and the app keeps the result in
            // its own cookie, so the interactive connection and the kiosk pages
            // work the same way as with the other modes.
            account.MapGet("/windows-sign-in", async (HttpContext http, string? returnUrl) =>
            {
                var result = await http.AuthenticateAsync(AuthSetup.NegotiateScheme);
                if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
                    return Results.Challenge(null, [AuthSetup.NegotiateScheme]);
                var principal = await AuthSetup.SignInPrincipalAsync(http, AuthSetup.WindowsIdentity, result.Principal);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                return Results.LocalRedirect(AuthSetup.LocalReturnUrl(http, returnUrl));
            }).AllowAnonymous();
        }

        // Sign out: a form post with the antiforgery token (SignOutButton).
        account.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return mode switch
            {
                AuthMode.EntraId => Results.SignOut(
                    new AuthenticationProperties { RedirectUri = $"{http.Request.PathBase}/account/signed-out" },
                    [OpenIdConnectDefaults.AuthenticationScheme]),
                AuthMode.Development => Results.LocalRedirect($"{http.Request.PathBase}/dev/sign-in"),
                _ => Results.LocalRedirect($"{http.Request.PathBase}/account/signed-out"),
            };
        }).AllowAnonymous().DisableAntiforgery();

        return app;
    }

    /// <summary>
    /// The development sign-in's form post (the form is the /dev/sign-in page).
    /// Mapped only when <c>Auth:Mode</c> is Development, which startup allows only
    /// in the Development environment.
    /// </summary>
    public static IEndpointRouteBuilder MapDevSignIn(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/sign-in", async (HttpContext http, [FromForm] DevSignInForm form) =>
        {
            if (!TierKeys.TryNormalize(form.Tier, out var tier)) return Results.BadRequest("Pick a tier.");
            var stores = await http.RequestServices.GetRequiredService<IStoreDirectory>().ListAsync(http.RequestAborted);
            var store = Guid.TryParse(form.Store, out var storeId) ? stores.FirstOrDefault(s => s.Id == storeId) : null;
            var needsStore = tier is Roster.Engine.Core.Tiers.Store or Roster.Engine.Core.Tiers.Emso;
            if (needsStore && store is null) return Results.BadRequest("Pick a store for a Store or EMSO sign-in.");

            var name = string.IsNullOrWhiteSpace(form.Name) ? $"Dev {TierKeys.Label(tier)}" : form.Name.Trim();
            var username = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (username.Length == 0) username = "dev";
            var user = new SignedInUser
            {
                Id = DevUserId($"{username}|{tier}|{store?.Code}"),
                Username = username,
                DisplayName = name,
                Tier = tier,
                // As the TypeScript app's rows: a store user has a store, an EMSO an
                // area, the network tiers neither (the chosen store just opens first).
                StoreId = tier == Roster.Engine.Core.Tiers.Store ? store?.Id : null,
                AreaId = tier == Roster.Engine.Core.Tiers.Emso ? store?.AreaId : null,
                RegionId = store?.RegionId,
            };
            var identity = new SignInIdentity { Method = "development", Username = username, DisplayName = name };
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AppUserResolver.CookiePrincipal(identity, user));
            if (store is not null)
                http.Response.Cookies.Append(Components.Ui.StoreSelection.CookieName, store.Id.ToString(),
                    new CookieOptions { Path = "/", MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax, IsEssential = true });
            var back = AuthSetup.LocalReturnUrl(http, form.ReturnUrl);
            return Results.LocalRedirect(back == $"{http.Request.PathBase}/" ? $"{http.Request.PathBase}/overview" : back);
        }).AllowAnonymous();
        return app;
    }

    // The same choices always give the same user, so a development session can come back.
    private static Guid DevUserId(string key) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"dev-user:{key}")).AsSpan(0, 16));
}

/// <summary>The development sign-in's fields.</summary>
public sealed class DevSignInForm
{
    public string? Tier { get; set; }
    public string? Store { get; set; }
    public string? Name { get; set; }
    public string? ReturnUrl { get; set; }
}
