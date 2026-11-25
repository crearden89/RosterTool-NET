// New: a bUnit context with what the shared components need: the browser helpers
// (in loose mode, so calls the test does not set up return defaults), the portal,
// the store choice, and a signed-in user with the given tier.

using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Roster.Web.Auth;
using Roster.Web.Components.Layout;
using Roster.Web.Components.Ui;

namespace Roster.Web.Tests.Infrastructure;

public abstract class UiTestContext : BunitContext
{
    protected UiTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<RosterJs>();
        Services.AddScoped<PortalService>();
        Services.AddScoped<StoreSelection>();
        Services.AddScoped<ShellEvents>();
        Services.Configure<BrandingOptions>(_ => { });
    }

    /// <summary>Signs a user in with the given tier key ("store", "pa", "super_user"...).</summary>
    protected BunitAuthorizationContext SignIn(string tier, string name = "Jordan Lee", Guid? storeId = null)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized(name);
        var claims = new List<Claim>
        {
            new(RosterClaims.UserId, Guid.NewGuid().ToString()),
            new(RosterClaims.Username, "jlee"),
            new(RosterClaims.DisplayName, name),
            new(RosterClaims.Tier, tier),
        };
        if (storeId is { } s) claims.Add(new Claim(RosterClaims.StoreId, s.ToString()));
        auth.SetClaims([.. claims]);
        return auth;
    }
}
