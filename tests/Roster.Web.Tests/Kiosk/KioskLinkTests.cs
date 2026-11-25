// Port of the kiosk pages' store lookup (src/app/floor/[token]/page.tsx and
// src/app/load/[token]/page.tsx: the store by its kiosk token, notFound otherwise),
// with the Kiosk:RequireDeviceSignIn switch.

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Roster.Web.Auth;
using Roster.Web.Kiosk;
using Roster.Web.Tests.Auth;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Kiosk;

public class KioskLinkTests
{
    private static readonly DirectoryOptions Stores = new()
    {
        Stores = [new() { Code = "G101", Name = "Northside", State = "NSW", KioskToken = "secret-g101" }],
    };

    private static KioskLinkCheck Check(bool requireDeviceSignIn, ClaimsPrincipal user) => new(
        new ConfigurationDirectory(PolicyTests.Monitor(Stores)),
        PolicyTests.Monitor(new KioskOptions { RequireDeviceSignIn = requireDeviceSignIn }),
        new FixedAuthentication(user));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    [Fact]
    public async Task A_known_link_opens_its_store_with_no_sign_in()
    {
        var link = await Check(false, Anonymous).CheckAsync("secret-g101");
        Assert.Equal(KioskLinkStatus.Open, link.Status);
        Assert.Equal(("G101", "Northside", "NSW"), (link.Store!.Code, link.Store.Name, link.Store.State));
        Assert.True(link.NoLoginNeeded);
    }

    [Theory]
    [InlineData("secret-g102")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unknown_link_is_not_found(string? token) =>
        Assert.Equal(KioskLinkStatus.NotFound, (await Check(false, Anonymous).CheckAsync(token)).Status);

    [Fact]
    public async Task With_device_sign_in_required_an_unsigned_device_goes_to_the_sign_in()
    {
        Assert.Equal(KioskLinkStatus.SignInRequired, (await Check(true, Anonymous).CheckAsync("secret-g101")).Status);
        var device = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "kiosk-g101")], RosterClaims.AuthenticationType));
        var link = await Check(true, device).CheckAsync("secret-g101");
        Assert.Equal(KioskLinkStatus.Open, link.Status);
        Assert.False(link.NoLoginNeeded);
    }

    [Fact]
    public async Task The_kiosk_stylesheets_are_served_without_a_sign_in()
    {
        await using var app = new AppFactory("Development", new Dictionary<string, string?> { ["Auth:Mode"] = "Development" });
        var client = app.Browser();
        var floor = await client.GetAsync("/css/kiosk/floor.css");
        Assert.Equal(HttpStatusCode.OK, floor.StatusCode);
        Assert.Contains(".floor-screen", await floor.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/kiosk/load.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/app.css")).StatusCode);
    }

    private sealed class FixedAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
}
