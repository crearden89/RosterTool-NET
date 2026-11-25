// Port of src/lib/overview/monthView.test.ts.

using Roster.Engine.Overview;
using Roster.Engine.Variance;
using static Roster.Engine.Overview.MonthView;

namespace Roster.Engine.Tests.Overview;

public class MonthViewTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    private static DayRow Day(string date, Summary? s = null, bool isWeekend = false, bool isToday = false) =>
        DayRow.From(s ?? new Summary(), D(date), "Mon", isWeekend, isToday, Light.None, Light.None);

    private static readonly WeekGroup Week = new(
        "2026-11-09",
        "9–15 Nov",
        D("2026-11-09"),
        true,
        [
            Day("2026-11-14", new Summary { ForecastSales = 144_694, ActualSales = 141_857 }, isWeekend: true),
            Day("2026-11-15", new Summary { ForecastSales = 119_836, ActualSales = 117_486 }, isWeekend: true, isToday: true),
            Day("2026-11-16", new Summary { ForecastSales = 92_965 }),
        ],
        new Summary());

    private static readonly OverviewData Nov = new(
        [Week],
        D("2026-11-01"),
        90,
        new PeriodKpis(98.46, 98.52, 97.1),
        new PeriodKpis(37.5, 37.51, 36.2),
        new MtdRow(D("2026-11-14"), new Summary { ForecastSales = 1_577_917, ActualSales = 1_546_977, KronosHours = 698.31, TargetHours = 671.45 }),
        D("2026-11-15"));

    private static Dictionary<MonthCardKey, MonthCard> Cards(OverviewData d, string month) => MonthCards(d, month).ToDictionary(c => c.Key);

    [Fact]
    public void Month_to_date_cards_are_the_mtd_row_and_the_months_oe_and_spend()
    {
        var cards = Cards(Nov, "November");
        Assert.Equal("Sales · month to date", cards[MonthCardKey.Sales].Label);
        Assert.Equal("$1,546,977", cards[MonthCardKey.Sales].Value);
        Assert.Equal("-2.0% against forecast ($1,577,917)", cards[MonthCardKey.Sales].Sub);
        Assert.Equal("98.5%", cards[MonthCardKey.Oe].Value);
        Assert.Equal("Target 90% · WTD 98.5%", cards[MonthCardKey.Oe].Sub);
        Assert.Equal("$37.51", cards[MonthCardKey.Spend].Value);
        Assert.Equal("WTD $37.50 · YTD $36.20", cards[MonthCardKey.Spend].Sub);
        // One decimal, as the MTD row writes hours.
        Assert.Equal("698.3 h", cards[MonthCardKey.Hours].Value);
        Assert.Equal("against 671.5 h target", cards[MonthCardKey.Hours].Sub);
    }

    [Fact]
    public void Cards_total_a_past_months_days()
    {
        var oct = Nov with
        {
            MonthStart = D("2026-10-01"),
            Mtd = null,
            Weeks =
            [
                Week with
                {
                    Days =
                    [
                        Day("2026-10-30", new Summary { ForecastSales = 100, ActualSales = 110, ActualCustomers = 4, KronosHours = 10, TargetHours = 9, ActualHours100 = 9 }),
                        Day("2026-10-31", new Summary { ForecastSales = 100, ActualSales = 90, ActualCustomers = 2, KronosHours = 10, TargetHours = 11, ActualHours100 = 9 }),
                    ],
                },
            ],
        };
        var cards = Cards(oct, "October");
        Assert.Equal(("Sales · October", "$200", "0.0% against forecast ($200)"), (cards[MonthCardKey.Sales].Label, cards[MonthCardKey.Sales].Value, cards[MonthCardKey.Sales].Sub));
        Assert.Equal(("Operational efficiency · October", "90.0%", "Target 90%"), (cards[MonthCardKey.Oe].Label, cards[MonthCardKey.Oe].Value, cards[MonthCardKey.Oe].Sub));
        Assert.Equal("$33.33", cards[MonthCardKey.Spend].Value);
        Assert.Equal(("20.0 h", "against 20.0 h target"), (cards[MonthCardKey.Hours].Value, cards[MonthCardKey.Hours].Sub));
    }

    [Fact]
    public void Cards_have_nothing_yet_for_a_month_still_to_come()
    {
        var dec = Nov with { MonthStart = D("2026-12-01"), Mtd = null, Weeks = [Week with { Days = [Day("2026-12-01", new Summary { ForecastSales = 90_000 })] }] };
        var cards = MonthCards(dec, "December");
        Assert.Equal(["-", "-", "-", "-"], cards.Select(c => c.Value));
        Assert.Equal("No sales yet", cards[0].Sub);
    }

    // ── the month as bars ──

    [Fact]
    public void Rounds_the_scale_up_to_a_tidy_figure()
    {
        Assert.Equal(200_000, NiceCeiling(144_694));
        Assert.Equal(200_000, NiceCeiling(119_836));
        Assert.Equal(100_000, NiceCeiling(95_000));
        Assert.Equal(250, NiceCeiling(240));
        Assert.Equal(0, NiceCeiling(0));
    }

    [Fact]
    public void Draws_each_day_against_the_months_scale()
    {
        var (top, days) = MonthBars(Nov);
        Assert.Equal(200_000, top);
        Assert.Equal((14, true, false), (days[0].Day, days[0].Weekend, days[0].Today));
        Close.To(141_857.0 / 200_000, days[0].Actual, 9);
        Assert.Equal((15, true), (days[1].Day, days[1].Today));
        Assert.Equal((0.0, 0.0), (days[2].Actual, days[2].ActualSales));
        Close.To(92_965.0 / 200_000, days[2].Forecast, 9);
    }

    [Fact]
    public void Draws_nothing_for_an_empty_month()
    {
        var (top, days) = MonthBars(Nov with { Weeks = [Week with { Days = [Day("2026-11-16")] }] });
        Assert.Equal(0, top);
        Assert.Equal((0.0, 0.0), (days[0].Actual, days[0].Forecast));
    }

    // ── the chart's gridlines ──

    [Fact]
    public void Gridlines_label_nothing_half_and_the_top_in_short_money()
    {
        Assert.Equal([new MonthTick(0, "$0"), new MonthTick(0.5, "$100k"), new MonthTick(1, "$200k")], MonthTicks(200_000));
        Assert.Equal(["$0", "$1.3m", "$2.5m"], MonthTicks(2_500_000).Select(t => t.Label));
        Assert.Equal("$250", MoneyShort(250));
        Assert.Equal("$12.5k", MoneyShort(12_500));
        Assert.Empty(MonthTicks(0));
    }
}
