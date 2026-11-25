// New: the three sign-in modes (Auth:Mode) end to end through the app: Entra ID
// (OpenID Connect), Windows Authentication (Negotiate) and the development sign-in.

using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Auth;

public class SignInModeTests
{
    private static readonly Dictionary<string, string?> Dev = new() { ["Auth:Mode"] = "Development" };

    [Fact]
    public async Task Development_sign_in_is_the_way_in_when_the_mode_is_development()
    {
        await using var app = new AppFactory("Development", Dev);
        var client = app.Browser();

        var response = await AppFactory.FollowAsync(client, "/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("/dev/sign-in", response.RequestMessage!.RequestUri!.AbsolutePath);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Development sign-in", html);
        Assert.Contains("G101 - Northside", html);
    }

    [Fact]
    public async Task Development_sign_in_signs_in_with_the_chosen_tier_and_store()
    {
        await using var app = new AppFactory("Development", Dev);
        var client = app.Browser();
        await SignInAsync(client, "store", "G101");

        var page = await AppFactory.FollowAsync(client, "/plan/sales");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Sales", html);
        Assert.Contains("Pat Store", html);
        // The store tier's menu has no Super User section.
        Assert.DoesNotContain(">Super User</p>", html);
    }

    [Fact]
    public async Task A_page_for_another_tier_sends_you_back_to_the_dashboard()
    {
        await using var app = new AppFactory("Development", Dev);
        var client = app.Browser();
        await SignInAsync(client, "store", "G101");

        var response = await client.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/overview", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_super_user_opens_the_super_user_pages()
    {
        await using var app = new AppFactory("Development", Dev);
        var client = app.Browser();
        await SignInAsync(client, "super_user", "");

        var response = await client.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">Super User</p>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Read_only_tiers_get_the_read_only_line()
    {
        await using var app = new AppFactory("Development", Dev);
        var client = app.Browser();
        await SignInAsync(client, "pa", "");
        var html = await (await AppFactory.FollowAsync(client, "/overview")).Content.ReadAsStringAsync();
        Assert.Contains("Read-only access - you can view data across your scope but cannot make changes.", html);
        Assert.Contains("PA (network read-only)", html);
    }

    [Fact]
    public async Task The_development_sign_in_does_not_exist_outside_development()
    {
        await using var app = new AppFactory("Production", EntraSettings(), StaticEntraMetadata);
        var client = app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/sign-in")).StatusCode);
        var post = await client.PostAsync("/dev/sign-in", new FormUrlEncodedContent(new Dictionary<string, string> { ["Tier"] = "super_user" }));
        Assert.NotEqual(HttpStatusCode.Redirect, post.StatusCode);
        Assert.False(post.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(".Roster.Session")));
    }

    [Fact]
    public void Development_mode_is_refused_outside_development()
    {
        using var app = new AppFactory("Production", Dev);
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("only allowed when ASPNETCORE_ENVIRONMENT is Development", error.ToString());
    }

    [Fact]
    public async Task Entra_ID_mode_sends_you_to_the_microsoft_sign_in()
    {
        await using var app = new AppFactory("Production", EntraSettings(), StaticEntraMetadata);
        var client = app.Browser();

        var response = await AppFactory.FollowAsync(client, "/overview");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("login.microsoftonline.com", location.Host);
        Assert.Contains("client_id=11111111-2222-3333-4444-555555555555", location.Query);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%2Fsignin-oidc", location.Query);
    }

    [Fact]
    public async Task Windows_mode_asks_the_browser_for_its_windows_sign_in()
    {
        await using var app = new AppFactory("Production", new Dictionary<string, string?> { ["Auth:Mode"] = "Windows" });
        var client = app.Browser();

        var first = await client.GetAsync("/account/sign-in?returnUrl=%2Foverview");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.StartsWith("/account/windows-sign-in", first.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    internal static async Task SignInAsync(HttpClient client, string tier, string storeCode, string name = "")
    {
        var form = await client.GetAsync("/dev/sign-in");
        var html = await form.Content.ReadAsStringAsync();
        var token = AppFactory.AntiforgeryToken(html);
        var store = "";
        if (storeCode.Length > 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(html, $"<option value=\"([0-9a-f-]+)\">{storeCode} - ");
            store = m.Groups[1].Value;
        }
        var response = await client.PostAsync("/dev/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Tier"] = tier,
            ["Store"] = store,
            ["Name"] = name.Length > 0 ? name : tier switch { "store" => "Pat Store", "super_user" => "Sam Admin", _ => "Robin Read" },
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static Dictionary<string, string?> EntraSettings() => new()
    {
        ["Auth:Mode"] = "EntraId",
        ["Auth:EntraId:Instance"] = "https://login.microsoftonline.com/",
        ["Auth:EntraId:TenantId"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        ["Auth:EntraId:ClientId"] = "11111111-2222-3333-4444-555555555555",
    };

    // No network in tests: the sign-in addresses Entra ID would publish.
    private static void StaticEntraMetadata(IServiceCollection services) =>
        services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
        {
            var tenant = "https://login.microsoftonline.com/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
            o.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = $"{tenant}/v2.0",
                AuthorizationEndpoint = $"{tenant}/oauth2/v2.0/authorize",
                TokenEndpoint = $"{tenant}/oauth2/v2.0/token",
                EndSessionEndpoint = $"{tenant}/oauth2/v2.0/logout",
            };
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
        });
}
