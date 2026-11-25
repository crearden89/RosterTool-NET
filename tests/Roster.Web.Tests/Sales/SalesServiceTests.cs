// New: the Sales page's reads and actions (page.tsx, actions.ts) on the data layer's model.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Sales;

namespace Roster.Web.Tests.Sales;

public class SalesServiceTests : IDisposable
{
    private readonly PageDb _db = new();
    private readonly Guid _store;
    private readonly Guid _other;
    private readonly Guid _area;

    public SalesServiceTests()
    {
        var (area, ids) = _db.SeedStores("G101", "G102");
        _area = area;
        _store = ids[0];
        _other = ids[1];
    }

    private SalesService Service() => new(_db, _db.Scope, _db, _db.Snapshots, TimeProvider.System);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public async Task A_store_login_gets_its_own_store_from_its_first_benchmark_date_with_the_12_week_average()
    {
        _db.Add(
            new BenchmarkDay { StoreId = _store, Date = D(11, 23), SalesForecast = 12000 },
            new BenchmarkDay { StoreId = _store, Date = D(11, 24), SalesForecast = 9000 },
            new ActualsDay { StoreId = _store, Date = D(11, 16), ActualSales = 11000 },
            new ActualsDay { StoreId = _store, Date = D(11, 9), ActualSales = 10000 });
        _db.User(Tier.Store, storeId: _store);

        var data = await Service().LoadAsync(_other, null);

        Assert.Equal(_store, data.StoreId); // ?store= cannot move a store login
        Assert.Empty(data.Stores);
        Assert.Equal(D(11, 23), data.StartDate);
        Assert.Equal(14, data.Dates.Count);
        Assert.Equal(10500, data.AvgSales[0].Avg); // the Mondays 16 and 9 Nov
        Assert.Null(data.AvgSales[1].Avg);
        Assert.False(data.ReadOnly);
        Assert.NotNull(data.Summary);
        Assert.Null(data.Summary!.Benchmark.Hours); // no plan held yet
    }

    [Fact]
    public async Task Saving_an_adjustment_keeps_the_note_unapproves_the_day_and_audits_it()
    {
        _db.Add(new DayPlan { StoreId = _store, Date = D(11, 24), PlanStatus = DayStatuses.Approved, RosterStatus = DayStatuses.Approved });
        _db.User(Tier.Store, storeId: _store);

        var res = await Service().SaveOverrideAsync(new SaveOverrideInput(_store, D(11, 24), false, "pct", 105, null, "Road works"));

        Assert.True(res.Ok);
        await using var db = _db.System();
        var row = await db.SalesOverrides.SingleAsync();
        Assert.Equal((105.0, "Road works", "Road works"), (row.Pct!.Value, row.Reason, row.Notes));
        var day = await db.DayPlans.SingleAsync();
        Assert.Equal((DayStatuses.None, DayStatuses.None), (day.PlanStatus, day.RosterStatus));
        var audit = await db.RosterAudit.SingleAsync();
        Assert.Equal("sales_override", audit.Action);
        Assert.Equal("{\"closed\":false,\"method\":\"pct\",\"pct\":105,\"value\":null}", audit.Detail);
    }

    [Fact]
    public async Task Back_at_100_percent_with_no_reason_clears_the_day_and_only_a_changing_one_unapproves()
    {
        _db.Add(
            new SalesOverride { StoreId = _store, Date = D(11, 24), Method = "pct", Pct = 100, Notes = "" },
            new SalesOverride { StoreId = _store, Date = D(11, 25), Method = "pct", Pct = 90, Notes = "Storm" },
            new DayPlan { StoreId = _store, Date = D(11, 24), PlanStatus = DayStatuses.Approved },
            new DayPlan { StoreId = _store, Date = D(11, 25), PlanStatus = DayStatuses.Approved });
        _db.User(Tier.Emso, areaId: _area);

        Assert.True((await Service().SaveOverrideAsync(new SaveOverrideInput(_store, D(11, 24), false, "pct", 100, null, "  "))).Ok);
        Assert.True((await Service().ResetOverrideAsync(_store, D(11, 25))).Ok);

        await using var db = _db.System();
        Assert.Empty(await db.SalesOverrides.ToListAsync());
        var days = await db.DayPlans.OrderBy(d => d.Date).ToListAsync();
        Assert.Equal(DayStatuses.Approved, days[0].PlanStatus); // the empty one changed nothing the plan used
        Assert.Equal(DayStatuses.None, days[1].PlanStatus);
        Assert.Equal(2, await db.RosterAudit.CountAsync());
    }

    [Fact]
    public async Task A_read_only_tier_is_refused_and_sees_the_page_read_only()
    {
        _db.User(Tier.Pa);
        var res = await Service().SaveOverrideAsync(new SaveOverrideInput(_store, D(11, 24), true, null, null, null, "Closed"));
        Assert.Equal(ScopeMessages.DeniedReadOnly, res.Error);
        var data = await Service().LoadAsync(_other, D(11, 23));
        Assert.True(data.ReadOnly);
        Assert.Equal(_other, data.StoreId);
        Assert.Equal(2, data.Stores.Count);
    }

    [Fact]
    public void Benchmark_hours_sum_the_open_days_plan_figure_else_their_stage1_total()
    {
        const string payload = """
        {"days":[
          {"date":"2026-11-23","result":{"hasData":true,"closed":false,"totalBenchmark":40},"plan":{"hours":{"benchmark":41.5}}},
          {"date":"2026-11-24","result":{"hasData":true,"closed":false,"totalBenchmark":38.25},"plan":null},
          {"date":"2026-11-25","result":{"hasData":true,"closed":true,"totalBenchmark":30},"plan":null},
          {"date":"2026-11-26","result":{"hasData":false,"closed":false,"totalBenchmark":30},"plan":null},
          {"date":"2026-12-25","result":{"hasData":true,"closed":false,"totalBenchmark":99},"plan":null}
        ]}
        """;
        var dates = Enumerable.Range(0, 14).Select(i => D(11, 23).AddDays(i)).ToList();
        Assert.Equal(79.75, SalesService.BenchmarkHours(payload, dates));
    }

    public void Dispose() => _db.Dispose();
}
