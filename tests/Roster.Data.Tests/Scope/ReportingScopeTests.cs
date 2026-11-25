// Port of src/lib/reportingScope.test.ts.
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Scope;

public class ReportingScopeTests
{
    private static Guid G(string s) => Guid.Parse(s.PadLeft(32, '0'));
    private static readonly Guid S1 = G("51"), S2 = G("52"), S3 = G("53"), S4 = G("54");
    private static readonly Guid R1 = G("71"), R2 = G("72"), R3 = G("73");
    private static readonly Guid A1 = G("a1"), A2 = G("a2"), A3 = G("a3");
    private static readonly Guid D1 = G("d1"), D2 = G("d2");

    // Two regions under one director group, a third under another. Areas cut ACROSS
    // regions on purpose - an EMSO patch is its own grouping.
    private static readonly ScopeStore[] Stores =
    [
        new(S1, R1, A1),
        new(S2, R1, A2),
        new(S3, R2, A1),
        new(S4, R3, A3),
    ];
    private static readonly ScopeRegion[] Regions = [new(R1, D1), new(R2, D1), new(R3, D2)];

    private static ScopedUser User(Tier tier, Guid? storeId = null, Guid? areaId = null, Guid? regionId = null, Guid? dg = null) =>
        new(Guid.NewGuid(), "u", tier, storeId, regionId, areaId, dg);

    [Fact]
    public void Hides_the_network_view_from_store_logins_only()
    {
        Assert.False(ReportingScope.CanSeeNetwork(Tier.Store));
        foreach (var t in new[] { Tier.SuperUser, Tier.Emso, Tier.Pa, Tier.Director })
            Assert.True(ReportingScope.CanSeeNetwork(t));
    }

    [Fact]
    public void Offers_the_national_switch_to_pa_and_dso_only()
    {
        Assert.True(ReportingScope.CanSeeNational(Tier.Pa));
        Assert.True(ReportingScope.CanSeeNational(Tier.Director));
        Assert.False(ReportingScope.CanSeeNational(Tier.Emso));
        Assert.False(ReportingScope.CanSeeNational(Tier.Store));
        // A super user is already national - nothing to switch to.
        Assert.False(ReportingScope.CanSeeNational(Tier.SuperUser));
    }

    [Fact]
    public void Gives_a_super_user_every_store_signalled_as_null() =>
        Assert.Null(ReportingScope.RemitStoreIds(User(Tier.SuperUser), Stores, Regions));

    [Fact]
    public void Gives_an_emso_their_area_across_regions() =>
        Assert.Equal([S1, S3], ReportingScope.RemitStoreIds(User(Tier.Emso, areaId: A1), Stores, Regions));

    [Fact]
    public void Gives_a_pa_their_region() =>
        Assert.Equal([S1, S2], ReportingScope.RemitStoreIds(User(Tier.Pa, regionId: R1), Stores, Regions));

    [Fact]
    public void Gives_a_dso_every_region_under_their_director_group()
    {
        Assert.Equal([S1, S2, S3], ReportingScope.RemitStoreIds(User(Tier.Director, dg: D1), Stores, Regions));
        Assert.Equal([S4], ReportingScope.RemitStoreIds(User(Tier.Director, dg: D2), Stores, Regions));
    }

    [Fact]
    public void Gives_a_store_login_just_their_own_store() =>
        Assert.Equal([S2], ReportingScope.RemitStoreIds(User(Tier.Store, storeId: S2), Stores, Regions));

    [Fact]
    public void Fails_closed_for_a_misconfigured_account_never_open_to_the_network()
    {
        // The dangerous bug here is a missing scope column reading as "no filter".
        Assert.Equal([], ReportingScope.RemitStoreIds(User(Tier.Pa), Stores, Regions));
        Assert.Equal([], ReportingScope.RemitStoreIds(User(Tier.Emso), Stores, Regions));
        Assert.Equal([], ReportingScope.RemitStoreIds(User(Tier.Director), Stores, Regions));
        Assert.Equal([], ReportingScope.RemitStoreIds(User(Tier.Store), Stores, Regions));
    }

    [Fact]
    public void National_widens_a_pa_and_a_dso_to_every_store()
    {
        Assert.Null(ReportingScope.NetworkStoreIds(User(Tier.Pa, regionId: R1), Stores, Regions, NetworkScope.National));
        Assert.Null(ReportingScope.NetworkStoreIds(User(Tier.Director, dg: D2), Stores, Regions, NetworkScope.National));
    }

    [Fact]
    public void Keeps_them_on_their_remit_when_the_switch_is_off() =>
        Assert.Equal([S1, S2], ReportingScope.NetworkStoreIds(User(Tier.Pa, regionId: R1), Stores, Regions, NetworkScope.Remit));

    [Fact]
    public void Cannot_be_used_to_widen_a_tier_that_is_not_entitled_to_it()
    {
        // A hand-typed ?scope=national must not lift an EMSO out of their area.
        Assert.Equal([S1, S3], ReportingScope.NetworkStoreIds(User(Tier.Emso, areaId: A1), Stores, Regions, NetworkScope.National));
        Assert.Equal([S2], ReportingScope.NetworkStoreIds(User(Tier.Store, storeId: S2), Stores, Regions, NetworkScope.National));
    }
}
