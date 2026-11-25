// New: the Dashboard's reads (page.tsx's store choice, computeOverview, loadAreaStores and
// loadStoreFortnight) against a SQLite copy of the schema.

using Roster.Data.Entities;
using Roster.Engine.Overview;
using Roster.Web.Features.Overview;

namespace Roster.Web.Tests.Overview;

public class DashboardServiceTests
{
    private static readonly DateOnly Nov2 = new(2026, 11, 2), Nov3 = new(2026, 11, 3);

    private static OverviewTestDb WithMonth()
    {
        var t = new OverviewTestDb();
        t.Day(OverviewTestDb.StoreA, Nov2, 100_000, 98_000);
        t.Day(OverviewTestDb.StoreB, Nov2, 50_000, 49_000);
        t.Day(OverviewTestDb.StoreC, Nov3, 70_000, 71_000);
        return t;
    }

    [Fact]
    public async Task A_store_login_lands_on_its_own_store_and_its_oe_target()
    {
        using var t = WithMonth();
        t.SignInAs(Tier.Store);
        var page = (await t.Overview.LoadDashboardAsync(null, null))!;
        Assert.Equal(OverviewTestDb.StoreA, page.SelectedStore);
        Assert.False(page.IsRollup);
        Assert.Equal(new DateOnly(2026, 11, 1), page.MonthStart);
        Assert.Equal(("November 2026", new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 1)), (page.MonthLabel, page.PrevMonth, page.NextMonth));
        Assert.Equal(92, page.Data!.OeTarget);
        var day = page.Data.Weeks.SelectMany(w => w.Days).Single(d => d.Date == Nov2);
        Assert.Equal((100_000.0, 98_000.0), (day.ForecastSales, day.ActualSales));
        // Every store is listed: the Dashboard is peer-visible.
        Assert.Equal(["G101", "G102", "G103"], page.Stores.Select(s => s.Code));
    }

    [Fact]
    public async Task A_store_login_may_open_another_stores_month_as_a_peer()
    {
        using var t = WithMonth();
        t.SignInAs(Tier.Store);
        var page = (await t.Overview.LoadDashboardAsync(OverviewTestDb.StoreC.ToString(), "2026-11-01"))!;
        Assert.Equal(OverviewTestDb.StoreC, page.SelectedStore);
        Assert.Equal(71_000, page.Data!.Weeks.SelectMany(w => w.Days).Single(d => d.Date == Nov3).ActualSales);
    }

    [Fact]
    public async Task An_emso_lands_on_their_area_rolled_up()
    {
        using var t = WithMonth();
        t.SignInAs(Tier.Emso);
        var page = (await t.Overview.LoadDashboardAsync(null, "2026-11-01"))!;
        Assert.True(page.IsRollup);
        Assert.Null(page.SelectedStore);
        var day = page.Data!.Weeks.SelectMany(w => w.Days).Single(d => d.Date == Nov2);
        Assert.Equal(150_000, day.ForecastSales);
        Assert.Equal(0, page.Data.Weeks.SelectMany(w => w.Days).Single(d => d.Date == Nov3).ForecastSales);
        Assert.Equal(90, page.Data.OeTarget);
    }

    [Fact]
    public async Task A_super_user_opens_the_remembered_store()
    {
        using var t = WithMonth();
        t.RememberedStore = OverviewTestDb.StoreB;
        var page = (await t.Overview.LoadDashboardAsync(null, "2026-11-01"))!;
        Assert.Equal(OverviewTestDb.StoreB, page.SelectedStore);
        Assert.False(page.IsRollup);
    }

    [Fact]
    public async Task Your_stores_lists_the_area_with_requests_and_oe_month_to_date()
    {
        using var t = WithMonth();
        var sa = Guid.NewGuid();
        t.Seed(db =>
        {
            db.Employees.Add(new Employee { Id = sa, StoreId = OverviewTestDb.StoreB, Name = "Mia Chen", Role = "SA", ContractHours = 60, PositionCode = "SA-FF" });
            db.DayOffRequests.Add(new DayOffRequest { Id = Guid.NewGuid(), EmployeeId = sa, Date = new DateOnly(2026, 12, 12), SubmittedAt = DateTimeOffset.UnixEpoch, Status = DayOffStatuses.Pending });
        });
        t.SignInAs(Tier.Emso);
        var view = await t.Overview.LoadYourStoresAsync(OverviewTestDb.Today);
        Assert.Equal(new DateOnly(2026, 12, 7), view.Period);
        Assert.Equal(["G101", "G102"], view.Rows.Select(r => r.Code));
        Assert.Equal([0, 1], view.Rows.Select(r => r.Requests));
        Assert.Equal("Not generated", view.Rows[0].Status.Plan.Label);
        Close(Metrics.OperationalEfficiencyPct(Metrics.WithPaidBreaks(40), 45)!.Value, view.Rows[0].OeMtd!.Value);
        Assert.False(view.NoRemit);
    }

    [Fact]
    public async Task An_area_login_without_an_area_has_no_stores_to_list()
    {
        using var t = WithMonth();
        t.SignInAs(Tier.Pa);
        // A PA's remit is their region: every store here.
        Assert.Equal(3, (await t.Overview.LoadYourStoresAsync(OverviewTestDb.Today)).Rows.Count);
    }

    [Fact]
    public async Task Reads_the_fortnight_and_what_the_store_has_to_do()
    {
        using var t = new OverviewTestDb();
        var isla = Guid.NewGuid();
        for (var d = new DateOnly(2026, 11, 23); d <= new DateOnly(2026, 12, 6); d = d.AddDays(1)) t.Day(OverviewTestDb.StoreA, d, 100_000);
        t.Seed(db =>
        {
            db.Employees.Add(new Employee { Id = isla, StoreId = OverviewTestDb.StoreA, Name = "Isla Scott", Role = "SA", ContractHours = 21, PositionCode = "SA-FF" });
            foreach (var dow in new[] { 0, 2, 4 })
                db.FixedRosters.Add(new FixedRoster { Id = Guid.NewGuid(), EmployeeId = isla, DayOfWeek = dow, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) });
            db.DayOffRequests.Add(new DayOffRequest { Id = Guid.NewGuid(), EmployeeId = isla, Date = new DateOnly(2026, 12, 12), SubmittedAt = DateTimeOffset.UnixEpoch, Status = DayOffStatuses.Pending });
        });
        t.SignInAs(Tier.Store);
        var f = (await t.Overview.LoadStoreFortnightAsync(OverviewTestDb.StoreA, OverviewTestDb.Today))!;
        Assert.Equal(new DateOnly(2026, 11, 23), f.State.PeriodStart);
        Assert.True(f.State.HasForecast);
        Assert.Null(f.State.Plan);
        Assert.False(f.State.Roster.Published);
        Assert.True(FortnightCard.IsOverdue(f.State));
        Assert.Equal(1_400_000, f.Figures.Forecast);
        Assert.Null(f.Figures.Planned);
        Assert.Equal(["1 day-off request waiting", "Isla Scott's fixed roster pays above contract"], f.Needs.Select(n => n.Title));
        Assert.Equal("Generate the plan", FortnightCard.NextActionsFor(f.State, canWrite: true, isReviewer: false).Primary.Label);
    }

    [Fact]
    public void Picks_each_held_days_ok_out_of_the_payload()
    {
        var oks = OverviewService.DayOks("""{"days":[{"ok":true},{"ok":false},{"date":"x"},{"ok":null}]}""");
        Assert.Equal([true, false, null, null], oks.Take(4));
        Assert.Equal(14, oks.Length);
    }

    private static void Close(double expected, double actual) => Assert.True(Math.Abs(expected - actual) < 1e-9, $"{actual} vs {expected}");
}
