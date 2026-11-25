// Port of the tier rules of src/lib/auth.ts (requireUser, requireTier, isReadOnly) as
// authorization policies, and of the sign-in's group-to-tier mapping.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Roster.Web.Auth;
using Roster.Web.Kiosk;

namespace Roster.Web.Tests.Auth;

public class PolicyTests
{
    private static ClaimsPrincipal User(string? tier, params Claim[] extra)
    {
        if (tier is null && extra.Length == 0) return new ClaimsPrincipal(new ClaimsIdentity());
        var claims = new List<Claim> { new(ClaimTypes.Name, "x") };
        if (tier is not null) claims.AddRange([new Claim(RosterClaims.Tier, tier), new Claim(RosterClaims.UserId, Guid.NewGuid().ToString())]);
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, RosterClaims.AuthenticationType));
    }

    private static IAuthorizationService Authorization(bool requireDeviceSignIn = false, params string[] deviceGroups)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(Policies.Configure);
        services.AddSingleton<IAuthorizationHandler, KioskAccessHandler>();
        services.Configure<KioskOptions>(o => { o.RequireDeviceSignIn = requireDeviceSignIn; o.DeviceGroups = deviceGroups; });
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    [Theory]
    [InlineData(Policies.SuperUser, "super_user", true)]
    [InlineData(Policies.SuperUser, "store", false)]
    [InlineData(Policies.SuperUser, "emso", false)]
    [InlineData(Policies.Emso, "emso", true)]
    [InlineData(Policies.Director, "director", true)]
    [InlineData(Policies.Writer, "store", true)]
    [InlineData(Policies.Writer, "emso", true)]
    [InlineData(Policies.Writer, "super_user", true)]
    [InlineData(Policies.Writer, "pa", false)]
    [InlineData(Policies.Writer, "director", false)]
    [InlineData(Policies.SignedIn, "pa", true)]
    public async Task Tier_policies(string policy, string tier, bool allowed) =>
        Assert.Equal(allowed, (await Authorization().AuthorizeAsync(User(tier), policy)).Succeeded);

    [Fact]
    public async Task Signed_in_without_access_to_the_app_is_not_signed_in_to_it()
    {
        var auth = Authorization();
        var noTier = User(null, new Claim(RosterClaims.SignInMethod, "entra"));
        Assert.False((await auth.AuthorizeAsync(noTier, Policies.SignedIn)).Succeeded);
        Assert.False((await auth.AuthorizeAsync(User(null), Policies.SignedIn)).Succeeded);
    }

    [Fact]
    public async Task Kiosk_policy_lets_anyone_in_until_device_sign_in_is_required()
    {
        Assert.True((await Authorization().AuthorizeAsync(User(null), Policies.Kiosk)).Succeeded);

        var locked = Authorization(requireDeviceSignIn: true);
        Assert.False((await locked.AuthorizeAsync(User(null), Policies.Kiosk)).Succeeded);
        Assert.True((await locked.AuthorizeAsync(User(null, new Claim(RosterClaims.SignInMethod, "windows")), Policies.Kiosk)).Succeeded);

        var grouped = Authorization(true, "Roster.Kiosk");
        Assert.False((await grouped.AuthorizeAsync(User(null, new Claim(RosterClaims.SignInMethod, "windows")), Policies.Kiosk)).Succeeded);
        Assert.True((await grouped.AuthorizeAsync(User(null, new Claim(RosterClaims.DeviceGroup, "Roster.Kiosk")), Policies.Kiosk)).Succeeded);
    }

    [Fact]
    public void Groups_give_the_widest_tier_the_person_has()
    {
        var options = new AuthOptions
        {
            TierGroups = new(StringComparer.OrdinalIgnoreCase)
            {
                ["SuperUser"] = ["Roster.SuperUser"],
                ["Store"] = ["Roster.Store"],
                ["DSO"] = [@"CORP\Roster DSO"],
                ["emso"] = ["5d2b0e0e-0000-0000-0000-000000000001"],
            },
        };
        static ClaimsPrincipal With(params (string Type, string Value)[] claims) =>
            new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

        Assert.Equal("store", options.TierFromGroups(With(("roles", "Roster.Store"))));
        Assert.Equal("super_user", options.TierFromGroups(With(("roles", "Roster.Store"), ("roles", "Roster.SuperUser"))));
        Assert.Equal("emso", options.TierFromGroups(With(("groups", "5d2b0e0e-0000-0000-0000-000000000001"), ("roles", "Roster.Store"))));
        Assert.Equal("director", options.TierFromGroups(With((ClaimTypes.Role, @"corp\roster dso"))));
        Assert.Null(options.TierFromGroups(With(("roles", "Something.Else"))));
        Assert.True(options.GroupsDecideTier);
        Assert.False(new AuthOptions().GroupsDecideTier);
    }

    [Fact]
    public async Task When_groups_decide_someone_in_none_of_them_has_no_access()
    {
        var directory = new ConfigurationDirectory(Monitor(new DirectoryOptions
        {
            Stores = [new() { Code = "G101", Name = "Northside", Area = "Area 1" }],
            Users = [new() { Username = "jlee@example.com", Tier = "Store", Store = "G101" }],
        }));
        var groups = new AuthOptions { TierGroups = new() { ["Emso"] = ["Roster.EMSO"] } };
        var resolver = new AppUserResolver(directory, Options.Create(groups), NullLogger<AppUserResolver>.Instance);

        Assert.Null(await resolver.ResolveAsync(new SignInIdentity { Method = "entra", Username = "jlee@example.com" }));
        var emso = await resolver.ResolveAsync(new SignInIdentity { Method = "entra", Username = "JLEE@example.com", GroupTier = "emso" });
        Assert.Equal("emso", emso!.Tier);
        Assert.NotNull(emso.StoreId);

        var stored = new AppUserResolver(directory, Options.Create(new AuthOptions()), NullLogger<AppUserResolver>.Instance);
        Assert.Equal("store", (await stored.ResolveAsync(new SignInIdentity { Method = "windows", Username = "x", AccountName = "jlee@example.com" }))!.Tier);
        Assert.Null(await stored.ResolveAsync(new SignInIdentity { Method = "windows", Username = "nobody" }));
    }

    [Theory]
    [InlineData("/plan/sales?store=1", "/plan/sales?store=1")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData(null, "/")]
    public void Return_addresses_stay_inside_the_app(string? returnUrl, string expected)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.Equal(expected, AuthSetup.LocalReturnUrl(http, returnUrl));
    }

    internal static IOptionsMonitor<T> Monitor<T>(T value) where T : class => new StaticMonitor<T>(value);

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
