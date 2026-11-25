// New: what each sign-in does once the company sign-in has answered - the Windows
// account or the Entra ID token becomes the app's user in the sign-in cookie - and
// signing out.

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Roster.Data.Entities;
using Roster.Web.Auth;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Auth;

public class SignInFlowTests
{
    [Fact]
    public async Task A_windows_sign_in_becomes_the_apps_user()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "Windows",
            ["Auth:TierGroups:Store:0"] = @"CORP\Roster Store Managers",
        };
        await using var app = new AppFactory("Production", settings, services =>
            // Kerberos cannot run in a test: a stand-in answers for the Negotiate scheme.
            services.PostConfigure<AuthenticationOptions>(o =>
                o.Schemes.First(s => s.Name == NegotiateDefaults.AuthenticationScheme).HandlerType = typeof(WindowsAccountHandler)));
        app.Directory.WithUser("jlee", Tier.Store, "G101");
        var client = app.Browser();

        var signIn = await client.GetAsync("/account/windows-sign-in?returnUrl=%2Fplan%2Fsales");
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/plan/sales", signIn.Headers.Location!.OriginalString);
        Assert.Contains(signIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AuthSetup.CookieName));

        var page = await client.GetAsync("/plan/sales");
        Assert.True(page.StatusCode == HttpStatusCode.OK, $"{page.StatusCode} -> {page.Headers.Location}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("jlee", html);
        Assert.Contains(">Store</p>", html);
    }

    [Fact]
    public async Task A_windows_account_the_app_does_not_know_gets_no_access()
    {
        await using var app = new AppFactory("Production", new Dictionary<string, string?> { ["Auth:Mode"] = "Windows" }, services =>
            services.PostConfigure<AuthenticationOptions>(o =>
                o.Schemes.First(s => s.Name == NegotiateDefaults.AuthenticationScheme).HandlerType = typeof(WindowsAccountHandler)));
        var client = app.Browser();

        await client.GetAsync("/account/windows-sign-in?returnUrl=%2Fplan%2Fsales");
        var response = await AppFactory.FollowAsync(client, "/plan/sales");
        Assert.Equal("/account/no-access", response.RequestMessage!.RequestUri!.AbsolutePath);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("You don't have access yet", html);
        Assert.Contains("jlee", html);
    }

    [Fact]
    public void An_entra_id_token_is_read_for_the_directory()
    {
        var token = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "0b6f-oid"),
            new Claim("preferred_username", "jordan.lee@example.com"),
            new Claim("name", "Jordan Lee"),
            new Claim("roles", "Roster.EMSO"),
        ], "AuthenticationTypes.Federation"));
        var options = new AuthOptions { TierGroups = new() { ["Emso"] = ["Roster.EMSO"] } };

        var identity = AuthSetup.EntraIdentity(token, options);
        Assert.Equal("entra", identity.Method);
        Assert.Equal("jordan.lee@example.com", identity.Username);
        Assert.Equal("0b6f-oid", identity.ObjectId);
        Assert.Equal("Jordan Lee", identity.DisplayName);
        Assert.Equal(Tier.Emso, identity.GroupTier);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        await using var app = new AppFactory("Development", new Dictionary<string, string?> { ["Auth:Mode"] = "Development" });
        var client = app.Browser();
        await SignInModeTests.SignInAsync(client, "super_user", "");
        var page = await AppFactory.FollowAsync(client, "/overview");
        var token = AppFactory.AntiforgeryToken(await page.Content.ReadAsStringAsync());

        var signOut = await client.PostAsync("/account/sign-out", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Equal("/dev/sign-in", signOut.Headers.Location!.OriginalString);
        var after = await AppFactory.FollowAsync(client, "/overview");
        Assert.Equal("/dev/sign-in", after.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Signing_out_needs_the_pages_antiforgery_token()
    {
        await using var app = new AppFactory("Development", new Dictionary<string, string?> { ["Auth:Mode"] = "Development" });
        var client = app.Browser();
        await SignInModeTests.SignInAsync(client, "super_user", "");
        var forged = await client.PostAsync("/account/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AppFactory.FollowAsync(client, "/overview")).StatusCode);
    }

    /// <summary>Answers the Negotiate scheme as the Windows account CORP\jlee in the store managers' group.</summary>
    private sealed class WindowsAccountHandler(IOptionsMonitor<NegotiateOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<NegotiateOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, @"CORP\jlee"), new Claim(ClaimTypes.Role, @"CORP\Roster Store Managers")],
                "Kerberos", ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
