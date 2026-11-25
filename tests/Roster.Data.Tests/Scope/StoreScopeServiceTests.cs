// Port of src/lib/authz.ts's guards (denyStoreWrite, denyEmployeeWrite, denyEmployeesWrite,
// denyWrite) and src/lib/storeScope.ts's resolveStoreId, over the database.
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Scope;

public sealed class StoreScopeServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly TestOrg _org;

    public StoreScopeServiceTests() => _org = TestOrg.Create(_db);

    public void Dispose() => _db.Dispose();

    private StoreScope As(AccessContext access)
    {
        var c = new TestContexts(_db, access);
        return new StoreScope(c, c);
    }

    private StoreScope As(ScopedUser user) => As(AccessContext.ForUser(user));

    [Fact]
    public async Task Guards_answer_with_the_typescript_apps_messages()
    {
        Assert.Equal(ScopeMessages.DeniedAuth, await As(AccessContext.Anonymous).DenyStoreWriteAsync(_org.S1));
        Assert.Equal(ScopeMessages.DeniedReadOnly, await As(_org.PaR1).DenyStoreWriteAsync(_org.S1));
        Assert.Equal(ScopeMessages.DeniedReadOnly, await As(_org.DirectorD1).DenyStoreWriteAsync(_org.S1));
        Assert.Null(await As(_org.StoreS1).DenyStoreWriteAsync(_org.S1));
        Assert.Equal(ScopeMessages.DeniedScope, await As(_org.StoreS1).DenyStoreWriteAsync(_org.S2));
        Assert.Equal(ScopeMessages.DeniedScope, await As(_org.StoreS1).DenyStoreWriteAsync(null));
        Assert.Null(await As(_org.EmsoA1).DenyStoreWriteAsync(_org.S2));
        Assert.Equal(ScopeMessages.DeniedScope, await As(_org.EmsoA1).DenyStoreWriteAsync(_org.S3));
        Assert.Null(await As(_org.Super).DenyStoreWriteAsync(_org.S4));
        Assert.Null(As(_org.StoreS1).DenyWrite());
        Assert.Equal(ScopeMessages.DeniedReadOnly, As(_org.PaR1).DenyWrite());
    }

    [Fact]
    public async Task Employee_guards_resolve_the_owning_store()
    {
        var me = As(_org.EmsoA1);
        Assert.Null(await me.DenyEmployeeWriteAsync(_org.EmployeeOf[_org.S2]));
        Assert.Equal(ScopeMessages.DeniedScope, await me.DenyEmployeeWriteAsync(_org.EmployeeOf[_org.S3]));
        Assert.Equal(ScopeMessages.EmployeeGone, await me.DenyEmployeeWriteAsync(Guid.NewGuid()));
        Assert.Null(await me.DenyEmployeesWriteAsync([_org.EmployeeOf[_org.S1], _org.EmployeeOf[_org.S2]]));
        Assert.Equal(ScopeMessages.DeniedScope, await me.DenyEmployeesWriteAsync([_org.EmployeeOf[_org.S1], _org.EmployeeOf[_org.S4]]));
        Assert.Null(await me.DenyEmployeesWriteAsync([]));
    }

    [Fact]
    public async Task Readable_and_writable_store_ids_mirror_the_two_postgres_functions()
    {
        Assert.Null(await As(_org.Super).ReadableStoreIdsAsync());
        Assert.Null(await As(_org.PaR1).ReadableStoreIdsAsync());
        Assert.Equal([], await As(_org.PaR1).WritableStoreIdsAsync());
        Assert.Equal([_org.S1], await As(_org.StoreS1).ReadableStoreIdsAsync());
        Assert.Equal([_org.S1], await As(_org.StoreS1).WritableStoreIdsAsync());
        Assert.Equal([_org.S1, _org.S2], await As(_org.EmsoA1).WritableStoreIdsAsync());
        Assert.True(await As(_org.EmsoA1).CanReadStoreAsync(_org.S2));
        Assert.False(await As(_org.EmsoA1).CanReadStoreAsync(_org.S3));
        Assert.Equal([], await As(AccessContext.Anonymous).ReadableStoreIdsAsync());
    }

    [Fact]
    public async Task Store_access_is_write_read_or_peer()
    {
        Assert.Equal(StoreAccess.Write, await As(_org.StoreS1).AccessForStoreAsync(_org.S1));
        Assert.Equal(StoreAccess.Peer, await As(_org.StoreS1).AccessForStoreAsync(_org.S3));
        Assert.Equal(StoreAccess.Read, await As(_org.DirectorD1).AccessForStoreAsync(_org.S3));
        Assert.Equal(StoreAccess.Peer, await As(_org.EmsoA1).AccessForStoreAsync(_org.S4));
        Assert.Null(await As(_org.StoreS1).AccessForStoreAsync(Guid.NewGuid()));
        Assert.Null(await As(AccessContext.Anonymous).AccessForStoreAsync(_org.S1));
    }

    [Fact]
    public async Task Network_roll_ups_use_the_remit_and_national_only_for_pa_and_dso()
    {
        Assert.Equal([_org.S1, _org.S2], await As(_org.PaR1).NetworkStoreIdsAsync(NetworkScope.Remit));
        Assert.Null(await As(_org.PaR1).NetworkStoreIdsAsync(NetworkScope.National));
        Assert.Equal([_org.S1, _org.S2, _org.S3], await As(_org.DirectorD1).NetworkStoreIdsAsync(NetworkScope.Remit));
        Assert.Equal([_org.S1, _org.S2], await As(_org.EmsoA1).NetworkStoreIdsAsync(NetworkScope.National));
        Assert.Null(await As(_org.Super).NetworkStoreIdsAsync(NetworkScope.Remit));
    }

    [Fact]
    public void ResolveStoreId_prefers_the_request_then_the_remembered_choice_then_the_first()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Assert.Equal(b, StoreScope.ResolveStoreId(b, [a, b], c));
        Assert.Equal(a, StoreScope.ResolveStoreId(c, [a, b], a)); // request out of scope
        Assert.Equal(b, StoreScope.ResolveStoreId(null, [a, b], b));
        Assert.Equal(a, StoreScope.ResolveStoreId(null, [a, b], c));
        Assert.Null(StoreScope.ResolveStoreId(null, [], null));
    }

    [Fact]
    public async Task Requiring_a_tier()
    {
        Assert.True(As(_org.Super).HasTier(Tier.SuperUser));
        Assert.False(As(_org.EmsoA1).HasTier(Tier.SuperUser));
        Assert.True(As(_org.EmsoA1).HasTier(Tier.SuperUser, Tier.Emso));
        Assert.False(As(AccessContext.Anonymous).HasTier(Tier.SuperUser));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_store_directory_lists_every_store_and_opens_kiosk_links()
    {
        var c = new TestContexts(_db, AccessContext.Anonymous);
        var dir = new StoreDirectory(c);
        Assert.Equal(["G801", "G802", "G803", "G804"], (await dir.ListAsync()).Select(s => s.Code).ToArray());
        var kiosk = await dir.FindByKioskTokenAsync("kiosk-G803");
        Assert.Equal(_org.S3, kiosk?.Id);
        Assert.Equal("G803 - Example Store G803", kiosk?.Label);
        Assert.Null(await dir.FindByKioskTokenAsync("nope"));
        Assert.Null(await dir.FindByKioskTokenAsync(""));
    }
}
