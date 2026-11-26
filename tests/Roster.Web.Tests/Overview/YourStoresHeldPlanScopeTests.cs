// New: "Your stores" reads the held plans and rosters of the stores its viewer can see, now
// that the owner tightened plan_snapshots to the visible stores (migration 0050). On SQL
// Server the plans' day results are read with raw SQL, which the row filters do not reach,
// so the same check runs on both databases.

using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Overview;
using Roster.Web.Tests.Infrastructure;
using static Roster.Web.Tests.Overview.OverviewTestDb;

namespace Roster.Web.Tests.Overview;

public abstract class YourStoresHeldPlanScope(OverviewTestDb t) : IDisposable
{
    private static readonly DateOnly Period = new(2026, 12, 7);

    public void Dispose() => t.Dispose();

    // The page reaches this with the viewer's remit only (LoadYourStoresAsync); asked for any
    // stores, it must still read no held plan the viewer cannot see.
    private OverviewService Service => (OverviewService)t.Overview;

    protected async Task ReadsTheVisibleStoresHeldPlansOnly()
    {
        t.Seed(db =>
        {
            foreach (var store in new[] { StoreA, StoreC })
            {
                db.PlanSnapshots.Add(new PlanSnapshot
                {
                    StoreId = store, PeriodStart = Period, Kind = SnapshotKinds.Plan, Version = 2,
                    Payload = "{\"days\":[{\"date\":\"2026-12-07\",\"ok\":true},{\"date\":\"2026-12-08\",\"ok\":false}]}",
                });
                db.PlanSnapshots.Add(new PlanSnapshot { StoreId = store, PeriodStart = Period, Kind = SnapshotKinds.Roster, Payload = "{\"byDate\":[]}" });
            }
        });
        IReadOnlyList<(Guid, string, string)> stores = [(StoreA, "G101", "Northside"), (StoreC, "G103", "Southgate")];

        // The north area's EMSO sees store A, not store C in the south.
        t.SignInAs(Tier.Emso);
        var rows = await Service.LoadAreaStoresAsync(stores, Period, Today);
        Assert.Equal(("Generated", 1), (rows[0].Status.Plan.Label, rows[0].Status.ErrorDays));
        Assert.Equal(("Not generated", (int?)null), (rows[1].Status.Plan.Label, rows[1].Status.ErrorDays));

        // A super user sees both.
        t.SignInAs(Tier.SuperUser);
        rows = await Service.LoadAreaStoresAsync(stores, Period, Today);
        Assert.Equal([("Generated", 1), ("Generated", 1)], rows.Select(r => (r.Status.Plan.Label, r.Status.ErrorDays ?? -1)));
    }
}

public sealed class YourStoresHeldPlanScopeTests() : YourStoresHeldPlanScope(new OverviewTestDb())
{
    [Fact]
    public Task Reads_the_visible_stores_held_plans_only() => ReadsTheVisibleStoresHeldPlansOnly();
}

[Trait("Category", "SqlServer")]
public sealed class YourStoresHeldPlanScopeSqlServerTests() : YourStoresHeldPlanScope(OverviewTestDb.OnSqlServer())
{
    [SqlServerFact]
    public Task Reads_the_visible_stores_held_plans_only_on_sql_server() => ReadsTheVisibleStoresHeldPlansOnly();
}
