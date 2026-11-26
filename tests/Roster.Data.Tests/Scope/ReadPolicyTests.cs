// New: the SELECT policies (visible_store_ids() and the per-table policies of 0001-0045,
// with plan_snapshots and daily_loads tightened to the visible stores by the owner), as
// global query filters, checked tier by tier.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Scope;

public sealed class ReadPolicyTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly TestOrg _org;

    public ReadPolicyTests() => _org = TestOrg.Create(_db);

    public void Dispose() => _db.Dispose();

    public static TheoryData<string> Identities => ["super", "store", "emso", "pa", "director", "kiosk", "anonymous", "system", "store-without-store", "emso-without-area"];

    private AccessContext Who(string who) => who switch
    {
        "super" => AccessContext.ForUser(_org.Super),
        "store" => AccessContext.ForUser(_org.StoreS1),
        "emso" => AccessContext.ForUser(_org.EmsoA1),
        "pa" => AccessContext.ForUser(_org.PaR1),
        "director" => AccessContext.ForUser(_org.DirectorD1),
        "kiosk" => AccessContext.ForKiosk(_org.S3),
        "system" => AccessContext.System("test"),
        // Rows the tier_scope constraint keeps out of the table, but a session could still
        // carry one: they must see nothing.
        "store-without-store" => AccessContext.ForUser(_org.StoreS1 with { StoreId = null }),
        "emso-without-area" => AccessContext.ForUser(_org.EmsoA1 with { AreaId = null }),
        _ => AccessContext.Anonymous,
    };

    /// <summary>visible_store_ids() for each identity.</summary>
    private Guid[] Visible(string who) => who switch
    {
        "super" or "pa" or "director" or "system" => _org.AllStores,
        "store" => [_org.S1],
        "emso" => [_org.S1, _org.S2],
        "kiosk" => [_org.S3],
        _ => [],
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void Store_scoped_tables_show_the_visible_stores_only(string who)
    {
        using var ctx = _db.As(Who(who));
        var expected = Visible(who).OrderBy(x => x).ToArray();
        Assert.Equal(expected, ctx.Stores.Select(s => s.Id).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.Employees.Select(e => e.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.BenchmarkDays.Select(b => b.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.GeneratedShifts.Select(s => s.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.DayPlans.Select(s => s.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.StoreWalkNotes.Select(s => s.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
    }

    /// <summary>A held plan, a held roster and a day's truck for every store.</summary>
    private void SeedHeldSnapshotsAndDailyLoads()
    {
        using var sys = _db.System();
        var ambient = sys.LoadTypes.Single(l => l.Name == "Ambient").Id;
        var period = new DateOnly(2026, 8, 3);
        foreach (var s in _org.AllStores)
        {
            sys.PlanSnapshots.Add(new PlanSnapshot { StoreId = s, PeriodStart = period, Kind = SnapshotKinds.Plan, Payload = "{\"days\":[]}" });
            sys.PlanSnapshots.Add(new PlanSnapshot { StoreId = s, PeriodStart = period, Kind = SnapshotKinds.Roster, Payload = "{\"byDate\":[]}" });
            sys.DailyLoads.Add(new DailyLoad { StoreId = s, Date = period, LoadTypeId = ambient, Qty = 6 });
        }
        sys.SaveChanges();
    }

    // The owner tightened plan_snapshots and daily_loads from "any signed-in user" to the
    // visible stores, super users keeping every store: a store login no longer reads another
    // store's held plans, rosters or truck directly, and a kiosk reads its own store's.
    [Theory]
    [MemberData(nameof(Identities))]
    public void Held_plans_rosters_and_daily_loads_show_the_visible_stores_only(string who)
    {
        SeedHeldSnapshotsAndDailyLoads();
        using var ctx = _db.As(Who(who));
        var expected = Visible(who).OrderBy(x => x).ToArray();
        Assert.Equal(expected, ctx.PlanSnapshots.Where(p => p.Kind == SnapshotKinds.Plan).Select(p => p.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.PlanSnapshots.Where(p => p.Kind == SnapshotKinds.Roster).Select(p => p.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expected, ctx.DailyLoads.Select(l => l.StoreId).AsEnumerable().OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_store_login_asking_for_another_stores_held_plan_or_truck_by_id_gets_nothing()
    {
        SeedHeldSnapshotsAndDailyLoads();
        using var ctx = _db.As(Who("store"));
        Assert.Empty(ctx.PlanSnapshots.Where(p => p.StoreId == _org.S2));
        Assert.Empty(ctx.DailyLoads.Where(l => l.StoreId == _org.S2));
        Assert.Equal(2, ctx.PlanSnapshots.Count(p => p.StoreId == _org.S1));
        Assert.Single(ctx.DailyLoads.Where(l => l.StoreId == _org.S1));
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public void Employee_and_shift_owned_rows_follow_their_owners_store(string who)
    {
        using var ctx = _db.As(Who(who));
        var expectedEmployees = Visible(who).Select(s => _org.EmployeeOf[s]).OrderBy(x => x).ToArray();
        var expectedShifts = Visible(who).Select(s => _org.ShiftOf[s]).OrderBy(x => x).ToArray();
        Assert.Equal(expectedEmployees, ctx.EmployeeLeave.Select(l => l.EmployeeId).AsEnumerable().OrderBy(x => x).ToArray());
        Assert.Equal(expectedShifts, ctx.ShiftTaskBlocks.Select(b => b.ShiftId).AsEnumerable().OrderBy(x => x).ToArray());
    }

    [Theory]
    [InlineData("super", 1)]
    [InlineData("store", 1)]
    [InlineData("pa", 1)]
    [InlineData("kiosk", 1)]
    [InlineData("anonymous", 0)]
    public void Shared_configuration_is_read_by_anyone_signed_in(string who, int expected)
    {
        using var ctx = _db.As(Who(who));
        Assert.Equal(expected, ctx.LoadTypes.Count());
        Assert.Equal(expected * 2, ctx.DirectorGroups.Count());
        Assert.Equal(expected * 3, ctx.Regions.Count());
    }

    [Theory]
    [InlineData("super", 5, 5)]
    [InlineData("store", 1, 1)]
    [InlineData("emso", 1, 1)]
    [InlineData("pa", 1, 1)]
    [InlineData("director", 1, 1)]
    [InlineData("kiosk", 0, 0)]
    [InlineData("anonymous", 0, 0)]
    public void Users_and_notifications_are_your_own_unless_you_are_a_super_user(string who, int users, int notifications)
    {
        using var ctx = _db.As(Who(who));
        Assert.Equal(users, ctx.AppUsers.Count());
        Assert.Equal(notifications, ctx.Notifications.Count());
        if (users == 1) Assert.Equal(Who(who).User!.Id, ctx.AppUsers.Single().Id);
    }

    [Theory]
    [InlineData("super", 1, 1, 0, 0)]
    [InlineData("store", 0, 0, 0, 0)]
    [InlineData("pa", 0, 0, 0, 0)]
    [InlineData("kiosk", 0, 0, 0, 1)]
    [InlineData("system", 1, 1, 1, 4)]
    public void Server_only_tables_stay_with_the_server(string who, int batches, int tokens, int cronRuns, int assignments)
    {
        using var ctx = _db.As(Who(who));
        Assert.Equal(batches, ctx.UploadBatches.Count());
        Assert.Equal(tokens, ctx.FeedTokens.Count());
        Assert.Equal(cronRuns, ctx.CronRuns.Count());
        Assert.Equal(assignments, ctx.LoadAssignments.Count());
    }

    [Fact]
    public void Ignoring_the_filters_is_how_the_peer_gate_reads_another_store()
    {
        using var ctx = _db.As(Who("store"));
        Assert.Single(ctx.Employees);
        Assert.Equal(4, ctx.Employees.IgnoreQueryFilters().Count());
    }
}
