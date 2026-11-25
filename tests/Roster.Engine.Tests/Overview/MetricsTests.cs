// Port of src/lib/overview/metrics.test.ts.

using Roster.Engine.Overview;
using static Roster.Engine.Overview.Metrics;

namespace Roster.Engine.Tests.Overview;

public class MetricsTests
{
    // G999 on 2026-08-04. Its labour-category columns summed to 47.8764h of actual
    // hours at 100%, Kronos recorded 53.80h paid, 0.30h of that was non-op, and the
    // store scanned 12,257 items.
    private const double Actual100 = 47.8764;
    private const double Kronos = 53.8;
    private const double NonOp = 0.3;
    private const double ItemsScanned = 12257;

    // ── operational efficiency ──

    [Fact]
    public void Oe_is_actual_hours_at_100_over_kronos_net_of_non_op()
    {
        // 47.8764 / (53.80 − 0.30)
        Close.To(89.49, OperationalEfficiencyPct(Actual100, Kronos, NonOp), 2);
    }

    [Fact]
    public void Oe_never_applies_the_90_percent_planning_factor()
    {
        // The old behaviour divided the numerator by 0.9, reading 11.1% high.
        Assert.False(Math.Abs(99.43 - OperationalEfficiencyPct(Actual100, Kronos, NonOp)!.Value) < 0.05);
    }

    [Fact]
    public void Oe_credits_non_op_back_to_the_store()
    {
        var without = OperationalEfficiencyPct(Actual100, Kronos, 0)!.Value;
        var withNonOp = OperationalEfficiencyPct(Actual100, Kronos, 5)!.Value;
        Assert.True(withNonOp > without);
    }

    [Fact]
    public void Oe_defaults_non_op_to_zero()
    {
        Close.To(OperationalEfficiencyPct(Actual100, Kronos, 0)!.Value, OperationalEfficiencyPct(Actual100, Kronos));
    }

    [Fact]
    public void Oe_reads_over_100_when_the_work_was_worth_more_than_the_hours_paid()
    {
        Assert.True(OperationalEfficiencyPct(60, 50) > 100);
    }

    [Fact]
    public void Oe_is_null_rather_than_zero_when_either_side_is_missing()
    {
        Assert.Null(OperationalEfficiencyPct(Actual100, 0));
        Assert.Null(OperationalEfficiencyPct(0, Kronos));
        // Non-op swallowing the whole day leaves nothing to divide by.
        Assert.Null(OperationalEfficiencyPct(Actual100, Kronos, Kronos));
    }

    // ── items per labour hour ──

    [Fact]
    public void Iplh_divides_by_the_full_kronos_figure()
    {
        Close.To(227.8, ItemsPerLabourHour(ItemsScanned, Kronos), 1);
        // Against the actual hours at 100% it read 256 - overstated by an eighth.
        Close.To(256.0, ItemsPerLabourHour(ItemsScanned, Actual100), 1);
    }

    [Fact]
    public void Iplh_is_null_rather_than_zero_when_either_side_is_missing()
    {
        Assert.Null(ItemsPerLabourHour(ItemsScanned, 0));
        Assert.Null(ItemsPerLabourHour(0, Kronos));
    }

    // ── average spend ──

    [Fact]
    public void Average_spend_divides_sales_by_customers()
    {
        Close.To(25, AverageSpend(45000, 1800));
    }

    [Fact]
    public void Average_spend_is_null_rather_than_zero_when_either_side_is_missing()
    {
        Assert.Null(AverageSpend(45000, 0));
        Assert.Null(AverageSpend(0, 1800));
    }

    // ── hours to OE target ──
    // G999 on 04/08 against a 90% target: 47.8764/0.9 + 0.30 = 53.50h allowed
    // against 53.80h paid, so the day spent 0.30h more than target permits.

    [Fact]
    public void Hours_to_target_is_positive_when_the_store_spent_more_than_target_allows()
    {
        Close.To(0.3, HoursToOeTarget(Actual100, Kronos, 90, NonOp), 2);
        Assert.True(OperationalEfficiencyPct(Actual100, Kronos, NonOp) < 90);
    }

    [Fact]
    public void Hours_to_target_is_negative_when_the_store_is_ahead_of_target()
    {
        Assert.True(HoursToOeTarget(Actual100, 50, 90, NonOp) < 0);
        Assert.True(OperationalEfficiencyPct(Actual100, 50, NonOp) > 90);
    }

    [Fact]
    public void Hours_to_target_is_exactly_zero_on_target()
    {
        var allowed = Actual100 / 0.9 + NonOp;
        Close.To(0, HoursToOeTarget(Actual100, allowed, 90, NonOp), 6);
    }

    [Fact]
    public void Hours_to_target_lets_non_op_through_unpenalised()
    {
        var @base = HoursToOeTarget(Actual100, Kronos, 90, NonOp)!.Value;
        Close.To(@base, HoursToOeTarget(Actual100, Kronos + 1, 90, NonOp + 1), 6);
    }

    [Fact]
    public void Hours_to_target_is_null_when_a_side_is_missing_or_the_target_unset()
    {
        Assert.Null(HoursToOeTarget(0, Kronos, 90));
        Assert.Null(HoursToOeTarget(Actual100, 0, 90));
        Assert.Null(HoursToOeTarget(Actual100, Kronos, 0));
    }

    // ── paid break factor ──
    // G999 on 2026-08-09: 38.0511h on task, 44.20h Kronos, no non-op.
    private const double Worked = 38.0511;
    private const double Kronos0908 = 44.2;

    [Fact]
    public void Grosses_hours_on_task_up_to_paid_hours()
    {
        Close.To(104.5, WithPaidBreaks(100, 4.5), 6);
        Close.To(39.7634, WithPaidBreaks(Worked, 4.5), 4);
    }

    [Fact]
    public void Defaults_to_4_5_percent()
    {
        Assert.Equal(4.5, DefaultPaidBreakFactorPct);
        Assert.Equal("paidBreakFactorPct", PaidBreakFactorKey);
        Close.To(WithPaidBreaks(Worked, 4.5), WithPaidBreaks(Worked), 9);
    }

    [Fact]
    public void Lifts_oe_by_the_factor()
    {
        // Hours on task alone read 86.1%; with paid breaks added back, 90.0%.
        Close.To(86.09, OperationalEfficiencyPct(Worked, Kronos0908), 2);
        Close.To(89.96, OperationalEfficiencyPct(WithPaidBreaks(Worked, 4.5), Kronos0908), 2);
    }

    [Fact]
    public void Treats_a_zero_negative_or_missing_factor_as_no_allowance()
    {
        Close.To(Worked, WithPaidBreaks(Worked, 0), 9);
        Close.To(Worked, WithPaidBreaks(Worked, -5), 9);
        Close.To(Worked, WithPaidBreaks(Worked, double.NaN), 9);
    }

    [Fact]
    public void Leaves_an_empty_day_empty()
    {
        Assert.Equal(0, WithPaidBreaks(0, 4.5));
        Assert.Equal(0, WithPaidBreaks(-3, 4.5));
    }
}
