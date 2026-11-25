// Port of src/app/(app)/plan/sales/adjustment.test.ts.

using Roster.Web.Features.Sales;

namespace Roster.Web.Tests.Sales;

public class AdjustmentTests
{
    private static AdjustmentRow Row(
        bool closed = false, string? method = "pct", double? pct = 100, double? value = null, string? reason = "", string? notes = "") =>
        new() { Closed = closed, Method = method, Pct = pct, Value = value, Reason = reason, Notes = notes };

    [Fact]
    public void ChangesSales_is_true_for_a_closed_day_a_percentage_other_than_100_and_a_new_value()
    {
        Assert.True(Adjustment.ChangesSales(Row(closed: true)));
        Assert.True(Adjustment.ChangesSales(Row(pct: 105)));
        Assert.True(Adjustment.ChangesSales(Row(pct: 90)));
        Assert.True(Adjustment.ChangesSales(Row(method: "value", pct: null, value: 52000)));
    }

    [Fact]
    public void ChangesSales_is_false_for_100_percent_or_no_figure_at_all_as_the_planner_reads_them()
    {
        Assert.False(Adjustment.ChangesSales(Row()));
        Assert.False(Adjustment.ChangesSales(Row(pct: null)));
        Assert.False(Adjustment.ChangesSales(Row(method: "value", pct: null, value: null)));
        Assert.False(Adjustment.ChangesSales(Row(method: null, pct: null)));
    }

    [Fact]
    public void IsEmptyAdjustment_is_what_setting_a_day_back_to_100_percent_with_no_reason_left_behind()
    {
        Assert.True(Adjustment.IsEmptyAdjustment(Row()));
        Assert.True(Adjustment.IsEmptyAdjustment(Row(notes: "  ", reason: null)));
    }

    [Fact]
    public void IsEmptyAdjustment_is_not_a_day_with_a_reason_or_a_changed_figure()
    {
        Assert.False(Adjustment.IsEmptyAdjustment(Row(notes: "Road works")));
        Assert.False(Adjustment.IsEmptyAdjustment(Row(notes: null, reason: "Road works")));
        Assert.False(Adjustment.IsEmptyAdjustment(Row(pct: 105)));
        Assert.False(Adjustment.IsEmptyAdjustment(Row(closed: true)));
    }
}
