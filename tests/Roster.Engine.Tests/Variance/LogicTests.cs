// New: the TypeScript app has no tests for src/lib/variance/logic.ts; these pin
// its behaviour, case by case, as the TypeScript code computes it.

using Roster.Engine.Variance;

namespace Roster.Engine.Tests.Variance;

public class LogicTests
{
    [Fact]
    public void A_blank_side_is_skipped()
    {
        Assert.Null(VarianceLogic.CompareMetric("Retail Sales", null, 100, 70));
        Assert.Null(VarianceLogic.CompareMetric("Retail Sales", 100, null, 70));
        Assert.Null(VarianceLogic.CompareMetric("Retail Sales", null, null, 70));
    }

    [Fact]
    public void Both_zero_is_skipped_and_a_zero_benchmark_against_a_non_zero_actual_is_Undefined()
    {
        Assert.Null(VarianceLogic.CompareMetric("Total Items", 0, 0, 70));
        Assert.Equal(
            new VarianceFlag("Total Items", 5, 0, null, "Actual is non-zero while benchmark is zero."),
            VarianceLogic.CompareMetric("Total Items", 5, 0, 70));
        // Undefined regardless of the threshold.
        Assert.NotNull(VarianceLogic.CompareMetric("Total Items", -1, 0, 500));
    }

    [Fact]
    public void Within_the_threshold_is_not_flagged_and_exactly_at_it_is_not_either()
    {
        Assert.Null(VarianceLogic.CompareMetric("Retail Sales", 150, 100, 70));
        Assert.Null(VarianceLogic.CompareMetric("Retail Sales", 170, 100, 70)); // exactly 70% - not beyond
        Assert.Null(VarianceLogic.CompareMetric("Retail Sales", 50, 100, 50));
    }

    [Fact]
    public void Beyond_the_threshold_is_flagged_above_or_below_with_the_percentage_to_one_place()
    {
        var above = VarianceLogic.CompareMetric("Retail Sales", 180, 100, 70)!;
        Assert.Equal("Retail Sales", above.Metric);
        Assert.Equal(180, above.Actual);
        Assert.Equal(100, above.Benchmark);
        Assert.Equal(80.0, above.VariancePct!.Value, 9);
        Assert.Equal("Actual is 80.0% above benchmark.", above.Reason);

        var below = VarianceLogic.CompareMetric("Load Running", 10, 40, 20)!;
        Assert.Equal(-75, below.VariancePct);
        Assert.Equal("Actual is 75.0% below benchmark.", below.Reason);
    }

    [Fact]
    public void The_reason_rounds_the_way_toFixed_does()
    {
        // (1.2345 - 1) / 1 * 100 = 23.449999999999996 → "23.4" (toFixed reads the exact binary value).
        Assert.Equal("Actual is 23.4% above benchmark.", VarianceLogic.CompareMetric("x", 1.2345, 1, 20)!.Reason);
        // 1/3 below → 66.66…% → "66.7".
        Assert.Equal("Actual is 66.7% below benchmark.", VarianceLogic.CompareMetric("x", 1, 3, 20)!.Reason);
    }

    [Fact]
    public void A_negative_benchmark_is_compared_as_the_script_does()
    {
        // (−50 − −100) / −100 = −0.5 → 50% below.
        var f = VarianceLogic.CompareMetric("x", -50, -100, 20)!;
        Assert.Equal(-50, f.VariancePct);
        Assert.Equal("Actual is 50.0% below benchmark.", f.Reason);
    }

    [Fact]
    public void Not_a_number_is_flagged_below_as_JavaScript_does()
    {
        var f = VarianceLogic.CompareMetric("x", double.NaN, 10, 20)!;
        Assert.True(double.IsNaN(f.VariancePct!.Value));
        Assert.Equal("Actual is NaN% below benchmark.", f.Reason);
    }

    private static readonly MetricPair[] Within =
    [
        new(1000, 1000),
        new(510, 500),
        new(95, 100),
    ];

    [Fact]
    public void Data_light_is_none_without_a_benchmark_and_red_without_actuals()
    {
        Assert.Equal(Light.None, VarianceLogic.DataLight(false, true, Within, 15));
        Assert.Equal(Light.None, VarianceLogic.DataLight(false, false, Within, 15));
        Assert.Equal(Light.Red, VarianceLogic.DataLight(true, false, Within, 15));
    }

    [Fact]
    public void Data_light_is_green_when_every_metric_is_present_and_within_the_threshold()
    {
        Assert.Equal(Light.Green, VarianceLogic.DataLight(true, true, Within, 15));
        Assert.Equal(Light.Green, VarianceLogic.DataLight(true, true, [], 15));
        // Exactly at the threshold is still green.
        Assert.Equal(Light.Green, VarianceLogic.DataLight(true, true, [new(115, 100)], 15));
    }

    [Fact]
    public void Data_light_is_amber_when_a_metric_is_missing_zero_or_outside_the_threshold()
    {
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [new(null, 100)], 15));
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [new(0, 100)], 15));
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [new(100, null)], 15));
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [new(100, 0)], 15));
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [new(116, 100)], 15));
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [new(84, 100)], 15));
        Assert.Equal(Light.Amber, VarianceLogic.DataLight(true, true, [.. Within, new(1, 100)], 15));
    }

    [Fact]
    public void Data_light_on_a_negative_benchmark_never_turns_amber_on_the_threshold()
    {
        // |a − b| / b is negative when b is: the TypeScript check never exceeds the threshold.
        Assert.Equal(Light.Green, VarianceLogic.DataLight(true, true, [new(500, -100)], 15));
    }

    [Fact]
    public void Light_keys_are_the_TypeScript_strings()
    {
        Assert.Equal("red", Light.Red.Key());
        Assert.Equal("amber", Light.Amber.Key());
        Assert.Equal("green", Light.Green.Key());
        Assert.Equal("none", Light.None.Key());
    }
}
