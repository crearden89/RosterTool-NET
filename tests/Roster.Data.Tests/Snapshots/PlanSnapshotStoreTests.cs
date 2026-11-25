// Port of the storage behaviour of src/lib/planSnapshots.ts: held snapshots, optimistic
// versions, the peer gate.
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Snapshots;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Snapshots;

public sealed class PlanSnapshotStoreTests : IDisposable
{
    private static readonly DateOnly Period = new(2026, 8, 3);
    private static readonly DateTimeOffset At = new(2026, 7, 31, 4, 0, 0, TimeSpan.Zero);
    private readonly TestDatabase _db = new();
    private readonly TestOrg _org;

    public PlanSnapshotStoreTests() => _org = TestOrg.Create(_db);

    public void Dispose() => _db.Dispose();

    private PlanSnapshotStore As(ScopedUser user) => new(new TestContexts(_db, AccessContext.ForUser(user)));

    [Fact]
    public async Task Holding_a_snapshot_bumps_its_version_each_time_and_keeps_the_json_exactly()
    {
        var store = As(_org.StoreS1);
        const string payload = "{\"days\":[{\"date\":\"2026-08-03\",\"ok\":true,\"result\":{\"x\":1.5}}]}";
        Assert.Equal(1, await store.StoreAsync(_org.S1, Period, SnapshotKinds.Plan, payload, At, _org.StoreS1.Id));
        Assert.Equal(2, await store.StoreAsync(_org.S1, Period, SnapshotKinds.Plan, payload, At.AddHours(1), _org.StoreS1.Id));
        Assert.Equal(1, await store.StoreAsync(_org.S1, Period, SnapshotKinds.Roster, "{\"byDate\":[]}", At, null));

        var held = await store.ReadAsync(_org.S1, Period);
        Assert.Equal(payload, held.Plan?.Payload);
        Assert.Equal(2, held.Plan?.Version);
        Assert.Equal(At.AddHours(1), held.Plan?.GeneratedAt);
        Assert.Equal(_org.StoreS1.Id, held.Plan?.GeneratedBy);
        Assert.Equal("{\"byDate\":[]}", held.Roster?.Payload);
        Assert.Equal(1, held.Roster?.Version);
    }

    [Fact]
    public async Task An_edit_carrying_a_stale_version_is_rejected_instead_of_overwriting()
    {
        var store = As(_org.StoreS1);
        await store.StoreAsync(_org.S1, Period, SnapshotKinds.Plan, "{\"days\":[1]}", At, _org.StoreS1.Id);

        var first = await store.WritePlanPayloadAsync(_org.S1, Period, "{\"days\":[2]}", expectedVersion: 1);
        Assert.True(first.Ok);
        Assert.Equal(2, first.Version);

        var stale = await store.WritePlanPayloadAsync(_org.S1, Period, "{\"days\":[3]}", expectedVersion: 1);
        Assert.False(stale.Ok);
        Assert.Equal(PlanSnapshotStore.ConflictMessage, stale.Error);
        Assert.Equal("{\"days\":[2]}", (await store.ReadAsync(_org.S1, Period)).Plan?.Payload);

        // No plan held at all: the same conflict, nothing created.
        Assert.False((await store.WritePlanPayloadAsync(_org.S2, Period, "{}", 1)).Ok);
    }

    [Fact]
    public async Task Only_a_writer_of_the_store_can_hold_or_edit_its_snapshots()
    {
        await Assert.ThrowsAsync<ScopeViolationException>(() =>
            As(_org.StoreS1).StoreAsync(_org.S2, Period, SnapshotKinds.Plan, "{}", At, _org.StoreS1.Id));
        await Assert.ThrowsAsync<ScopeViolationException>(() =>
            As(_org.PaR1).StoreAsync(_org.S1, Period, SnapshotKinds.Plan, "{}", At, _org.PaR1.Id));
        await As(_org.EmsoA1).StoreAsync(_org.S2, Period, SnapshotKinds.Plan, "{}", At, _org.EmsoA1.Id);
    }

    [Fact]
    public async Task The_peer_gate_returns_the_plan_only_without_who_generated_it()
    {
        await As(_org.EmsoA1).StoreAsync(_org.S2, Period, SnapshotKinds.Plan, "{\"days\":[]}", At, _org.EmsoA1.Id);
        await As(_org.EmsoA1).StoreAsync(_org.S2, Period, SnapshotKinds.Roster, "{\"byDate\":[]}", At, _org.EmsoA1.Id);

        var peer = await As(_org.StoreS1).ReadPeerPlanAsync(_org.S2, Period);
        Assert.NotNull(peer);
        Assert.Equal(SnapshotKinds.Plan, peer.Kind);
        Assert.Null(peer.GeneratedBy);

        var nobody = new PlanSnapshotStore(new TestContexts(_db, AccessContext.Anonymous));
        Assert.Null(await nobody.ReadPeerPlanAsync(_org.S2, Period));
    }

    [Fact]
    public async Task A_snapshot_is_a_plan_or_a_roster() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            As(_org.Super).StoreAsync(_org.S1, Period, "draft", "{}", At, null));
}
