// New: SalesClient.tsx's figure helpers against the values JavaScript gives
// (computed in Node: $S/agents/p6/sales-vectors.js).

using Roster.Web.Features.Sales;

namespace Roster.Web.Tests.Sales;

public class SalesFiguresTests
{
    private static AdjustmentRow Pct(double pct) => new() { Method = "pct", Pct = pct };
    private static AdjustmentRow Value(double value) => new() { Method = "value", Value = value };

    [Theory]
    [InlineData(10000, "pct", 105, "+5%")]
    [InlineData(10000, "pct", 90, "−10%")]
    [InlineData(10000, "value", 10000, "Note")]
    [InlineData(10000, "value", 10001, "±0%")]
    [InlineData(0, "value", 500, "Set")]
    [InlineData(0, "pct", 105, "Note")]
    [InlineData(10000, "pct", 102.5, "+3%")]
    [InlineData(10000, "pct", 99.5, "±0%")]
    [InlineData(12345.67, "pct", 97.5, "−3%")]
    [InlineData(8000, "value", 9234.5, "+15%")]
    public void The_pill_reads_as_javascript_writes_it(double forecast, string method, double figure, string expected)
    {
        var o = method == "pct" ? Pct(figure) : Value(figure);
        Assert.Equal(expected, SalesFigures.AdjustmentPill(forecast, o));
    }

    [Fact]
    public void A_closed_day_and_a_note_only_day_have_their_own_pills_and_no_row_none()
    {
        Assert.Equal("Closed", SalesFigures.AdjustmentPill(10000, new AdjustmentRow { Closed = true }));
        Assert.Equal("Note", SalesFigures.AdjustmentPill(10000, new AdjustmentRow { Method = null, Notes = "Road works" }));
        Assert.Null(SalesFigures.AdjustmentPill(10000, null));
    }

    [Theory]
    [InlineData(0, "$0", "$0")]
    [InlineData(999.5, "$1,000", "$1,000")]
    [InlineData(1000, "$1,000", "$1k")]
    [InlineData(98499.5, "$98,500", "$98k")]
    [InlineData(1234567.89, "$1,234,568", "$1235k")]
    [InlineData(12345.5, "$12,346", "$12k")]
    public void Money_reads_as_javascript_writes_it(double n, string money0, string moneyK)
    {
        Assert.Equal(money0, SalesFigures.Money0(n));
        Assert.Equal(moneyK, SalesFigures.MoneyK(n));
    }

    [Fact]
    public void Adjusted_figure_is_zero_when_closed_rescaled_by_a_percentage_or_the_new_value()
    {
        Assert.Equal(0, SalesFigures.AdjustedFor(10000, new AdjustmentRow { Closed = true, Method = "pct", Pct = 120 }));
        Assert.Equal(10500, SalesFigures.AdjustedFor(10000, Pct(105)));
        Assert.Equal(52000, SalesFigures.AdjustedFor(10000, Value(52000)));
        Assert.Null(SalesFigures.AdjustedFor(10000, new AdjustmentRow { Method = "pct", Pct = null }));
        Assert.Null(SalesFigures.AdjustedFor(10000, null));
    }

    [Fact]
    public void The_fortnight_splits_into_ISO_weeks_and_an_empty_adjustment_is_none()
    {
        var start = new DateOnly(2026, 11, 25); // a Wednesday: weeks of 5, 7 and 2 days
        var dates = Enumerable.Range(0, 14).Select(start.AddDays).ToList();
        var data = new SalesPageData(
            Guid.NewGuid(), [], start, dates,
            [new SalesForecastDay(start, 10000)],
            [
                new SalesOverrideRow(start, false, "pct", 100, null, "", ""),
                new SalesOverrideRow(start.AddDays(1), false, "pct", 110, null, null, "Promo"),
            ],
            dates.Select(d => new AvgSalesDay(d, null)).ToList(),
            null,
            false);
        var days = SalesFigures.Days(data);
        Assert.Null(days[0].Override); // 100% and no reason: nothing on the chart
        Assert.NotNull(SalesFigures.SavedOverrideFor(data, start)); // but the dialog still gets it
        Assert.Equal("Promo", days[1].Note);
        Assert.Equal([5, 7, 2], SalesFigures.Weeks(days).Select(w => w.Days.Count).ToArray());
        Assert.True(days[3].Weekend); // Saturday 28 Nov
    }
}
