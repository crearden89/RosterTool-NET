// New: lib/overview/compute.ts had no unit test (server-only); these cover its pure half,
// OverviewCompute.BuildOverview, on invented rollup rows.

using Roster.Engine.Overview;
using Roster.Engine.Variance;

namespace Roster.Engine.Tests.Overview;

public class OverviewComputeTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    private static RollupRow Row(string date, double forecast = 0, double actual = 0, double h90 = 0, double ah = 0, double kronos = 0,
        double nonop = 0, double custF = 0, double custA = 0, double itemsF = 0, double itemsA = 0, bool approved = false, bool generated = false) =>
        new(D(date), forecast, custF, itemsF, h90, h90 / 0.9 * 0.9, actual, itemsA, custA, ah, kronos, nonop, approved, generated);

    private static readonly RollupRow[] Rows =
    [
        Row("2026-01-05", actual: 1000, ah: 10, kronos: 11, custA: 40),
        Row("2026-11-02", forecast: 100_000, actual: 98_000, h90: 50, ah: 40, kronos: 46, nonop: 30, custF: 2600, custA: 2650, itemsF: 24_000, itemsA: 23_500, approved: true),
        Row("2026-11-03", forecast: 110_000, actual: 50_000, h90: 52, ah: 41, kronos: 47, custF: 2800, custA: 2700, itemsF: 26_000, itemsA: 25_000, generated: true),
        Row("2026-11-04", forecast: 105_000, h90: 51),
    ];

    private static OverviewData Build(string today = "2026-11-04") =>
        OverviewCompute.BuildOverview(Rows, D("2026-11-01"), 90, D(today), 4.5, 15);

    [Fact]
    public void Asks_the_rollup_for_the_year_to_date_and_the_month()
    {
        Assert.Equal((D("2026-01-01"), D("2026-11-30")), OverviewCompute.RollupRange(D("2026-11-01"), D("2026-11-04")));
        Assert.Equal((D("2025-12-01"), D("2026-01-10")), OverviewCompute.RollupRange(D("2025-12-01"), D("2026-01-10")));
    }

    [Fact]
    public void Lists_every_day_of_the_month_in_monday_weeks()
    {
        var o = Build();
        Assert.Equal(30, o.Weeks.Sum(w => w.Days.Count));
        Assert.Equal(("2026-10-26", "1–1 Nov"), (o.Weeks[0].Key, o.Weeks[0].Label));
        Assert.Equal(("2026-11-02", "2–8 Nov"), (o.Weeks[1].Key, o.Weeks[1].Label));
        Assert.Equal("30–30 Nov", o.Weeks[^1].Label);
        var sun = o.Weeks[0].Days[0];
        Assert.Equal(("Sun", true), (sun.Dow, sun.IsWeekend));
    }

    [Fact]
    public void Lights_each_day_from_its_benchmark_roster_and_actuals()
    {
        var days = Build().Weeks.SelectMany(w => w.Days).ToDictionary(d => d.Date);
        Assert.Equal((Light.Green, Light.Green), (days[D("2026-11-02")].RosterStatus, days[D("2026-11-02")].DataStatus));
        // Sales 55% under forecast: the data light goes amber.
        Assert.Equal((Light.Amber, Light.Amber), (days[D("2026-11-03")].RosterStatus, days[D("2026-11-03")].DataStatus));
        Assert.Equal((Light.Red, Light.Red), (days[D("2026-11-04")].RosterStatus, days[D("2026-11-04")].DataStatus));
        Assert.Equal((Light.None, Light.None), (days[D("2026-11-05")].RosterStatus, days[D("2026-11-05")].DataStatus));
    }

    [Fact]
    public void Grosses_up_the_actual_hours_and_takes_non_op_off_the_oe_denominator()
    {
        var day = Build().Weeks.SelectMany(w => w.Days).First(d => d.Date == D("2026-11-02"));
        Close.To(41.8, day.ActualHours100, 9);
        Assert.Equal(0.5, day.NonOpHours);
        Assert.Equal(50.5, day.TargetHours);
        Close.To(41.8 / (46 - 0.5) * 100, day.OeResult, 9);
        Close.To(23_500.0 / 46, day.Iplh, 9);
        Close.To(98_000.0 / 2650, day.AvgSpend, 9);
    }

    [Fact]
    public void Puts_the_month_to_date_row_after_yesterday()
    {
        var o = Build();
        Assert.NotNull(o.Mtd);
        Assert.Equal(D("2026-11-03"), o.Mtd!.AfterDate);
        Assert.Equal(148_000, o.Mtd.Summary.ActualSales);
        Assert.Null(Build("2026-11-01").Mtd); // nothing before the first
        Assert.Null(Build("2026-12-05").Mtd); // another month
    }

    [Fact]
    public void Opens_the_week_a_forecast_is_loaded_for()
    {
        var o = Build("2026-10-01");
        Assert.True(o.Weeks[1].PlanAllowed); // has benchmarks
        Assert.False(o.Weeks[^1].PlanAllowed); // no data, beyond today + 14
    }

    [Fact]
    public void Reads_oe_and_spend_over_the_week_month_and_year_to_today()
    {
        var o = Build();
        Close.To((41.8 + 41 * 1.045) / (46 + 47 - 0.5) * 100, o.Oe.Wtd, 9);
        Assert.Equal(o.Oe.Wtd, o.Oe.Mtd);
        Close.To((10.45 + 41.8 + 41 * 1.045) / (11 + 46 + 47 - 0.5) * 100, o.Oe.Ytd, 9);
        Close.To(148_000.0 / 5350, o.AvgSpend.Mtd, 9);
        Close.To(149_000.0 / 5390, o.AvgSpend.Ytd, 9);
    }
}
