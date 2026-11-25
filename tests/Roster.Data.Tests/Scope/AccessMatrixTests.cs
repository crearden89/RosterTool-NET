// Port of src/lib/access.test.ts.
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Data.Tests.Scope;

public class AccessMatrixTests
{
    private static readonly Guid StoreA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid StoreB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid OrphanStore = Guid.Parse("00000000-0000-0000-0000-0000000000ff");
    private static readonly Guid Area1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Area2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static ScopedUser U(Tier tier, Guid? storeId = null, Guid? areaId = null) =>
        new(Guid.NewGuid(), "u", tier, storeId, null, areaId, null);

    private static bool InScope(StoreSet scope, Guid store) =>
        AccessMatrix.StoreInWriteScope(scope, store, store == StoreA ? Area1 : store == StoreB ? Area2 : null);

    [Fact]
    public void Pa_and_dso_are_read_only_emso_and_store_are_write_tiers()
    {
        Assert.True(AccessMatrix.IsReadOnlyTier(Tier.Pa));
        Assert.True(AccessMatrix.IsReadOnlyTier(Tier.Director));
        Assert.False(AccessMatrix.IsReadOnlyTier(Tier.Emso));
        Assert.False(AccessMatrix.IsReadOnlyTier(Tier.Store));
        Assert.False(AccessMatrix.IsReadOnlyTier(Tier.SuperUser));
    }

    [Fact]
    public void Super_user_writes_every_store()
    {
        var scope = AccessMatrix.WriteScopeFor(U(Tier.SuperUser));
        Assert.True(InScope(scope, StoreA));
        Assert.True(InScope(scope, OrphanStore));
    }

    [Fact]
    public void Store_tier_writes_only_its_own_store_whatever_id_a_request_carries()
    {
        var scope = AccessMatrix.WriteScopeFor(U(Tier.Store, storeId: StoreA));
        Assert.True(InScope(scope, StoreA));
        Assert.False(InScope(scope, StoreB));
    }

    [Fact]
    public void Emso_writes_every_store_in_its_area_and_nothing_outside_it()
    {
        var scope = AccessMatrix.WriteScopeFor(U(Tier.Emso, areaId: Area1));
        Assert.True(InScope(scope, StoreA));
        Assert.False(InScope(scope, StoreB));
        // A store with no area can never fall into an area scope by accident.
        Assert.False(InScope(scope, OrphanStore));
    }

    [Fact]
    public void Pa_and_dso_get_no_write_scope_at_all()
    {
        foreach (var tier in new[] { Tier.Pa, Tier.Director })
        {
            var scope = AccessMatrix.WriteScopeFor(U(tier, storeId: StoreA, areaId: Area1));
            Assert.IsType<StoreSet.Nothing>(scope);
            Assert.False(InScope(scope, StoreA));
        }
    }

    [Fact]
    public void A_misconfigured_row_with_no_scope_column_fails_closed()
    {
        Assert.IsType<StoreSet.Nothing>(AccessMatrix.WriteScopeFor(U(Tier.Store)));
        Assert.IsType<StoreSet.Nothing>(AccessMatrix.WriteScopeFor(U(Tier.Emso)));
        // The read side too: visible_store_ids() returns nothing for them.
        Assert.IsType<StoreSet.Nothing>(AccessMatrix.ReadScopeFor(U(Tier.Store)));
        Assert.IsType<StoreSet.Nothing>(AccessMatrix.ReadScopeFor(U(Tier.Emso)));
    }

    [Fact]
    public void StoreAccessFor_own_store_writes_other_stores_are_peers_oversight_reads()
    {
        var sm = U(Tier.Store, storeId: StoreA);
        Assert.Equal(StoreAccess.Write, AccessMatrix.StoreAccessFor(sm, StoreA, Area1));
        Assert.Equal(StoreAccess.Peer, AccessMatrix.StoreAccessFor(sm, StoreB, Area2));

        var emso = U(Tier.Emso, areaId: Area1);
        Assert.Equal(StoreAccess.Write, AccessMatrix.StoreAccessFor(emso, StoreA, Area1));
        Assert.Equal(StoreAccess.Peer, AccessMatrix.StoreAccessFor(emso, StoreB, Area2));

        Assert.Equal(StoreAccess.Read, AccessMatrix.StoreAccessFor(U(Tier.Pa), StoreB, Area2));
        Assert.Equal(StoreAccess.Read, AccessMatrix.StoreAccessFor(U(Tier.Director), StoreB, Area2));
        Assert.Equal(StoreAccess.Write, AccessMatrix.StoreAccessFor(U(Tier.SuperUser), StoreB, Area2));
    }

    [Fact]
    public void Read_scope_mirrors_visible_store_ids()
    {
        Assert.IsType<StoreSet.All>(AccessMatrix.ReadScopeFor(U(Tier.SuperUser)));
        Assert.IsType<StoreSet.All>(AccessMatrix.ReadScopeFor(U(Tier.Pa)));
        Assert.IsType<StoreSet.All>(AccessMatrix.ReadScopeFor(U(Tier.Director)));
        Assert.Equal(new StoreSet.OneStore(StoreA), AccessMatrix.ReadScopeFor(U(Tier.Store, storeId: StoreA)));
        Assert.Equal(new StoreSet.OneArea(Area1), AccessMatrix.ReadScopeFor(U(Tier.Emso, areaId: Area1)));
    }

    [Fact]
    public void Tiers_keep_the_stored_values_and_labels()
    {
        Assert.Equal(["super_user", "store", "pa", "director", "emso"], Tiers.All.Select(t => t.Key()).ToArray());
        Assert.Equal("DSO (network read-only)", Tier.Director.Label());
        Assert.True(Tiers.TryParse("DSO", out var dso) && dso == Tier.Director);
        Assert.True(Tiers.TryParse("SuperUser", out var su) && su == Tier.SuperUser);
        Assert.False(Tiers.TryParse("admin", out _));
    }
}
