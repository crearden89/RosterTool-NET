// New: the pure part of src/lib/till/compute.ts (computeTillForDate), which has no
// TypeScript test of its own: blocks per trading quarter-hour, one till at least,
// the sales override and the uncovered quarter-hours.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Till;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.Till;

public class ComputeTests
{
    private static readonly StoreAttrsRow Attrs = new()
    {
        HasSco = true, IsNewScoModel = true, McoRegisters = 6, ScoLanes = 4, ScoEfficiency = 0.45, FirstStep = 1.2, ScoShare = 0.66,
    };

    private static TillSources Day(SalesOverrideRow? @override = null, IReadOnlyList<QuarterHourRow>? curve = null) => new()
    {
        StoreId = "s1",
        Date = ParseIso("2026-08-17"),
        Attrs = Attrs,
        Day = new StoreDay(false, 8 * 60, 9 * 60, 6 * 60, 9 * 60 + 30),
        Bench = new BenchmarkDayRow { SalesForecast = 1000, CheckoutHours90 = 4, CheckoutHours100 = 3.6 },
        // 08:00-09:00 shares; 08:45 has none.
        AllocRows = curve ??
        [
            new QuarterHourRow { BlockTime = "08:00:00", Allocation = 0.1 },
            new QuarterHourRow { BlockTime = "08:15:00", Allocation = 0.25 },
            new QuarterHourRow { BlockTime = "08:30:00", Allocation = 0.5 },
        ],
        Override = @override,
    };

    [Fact]
    public void Plans_every_trading_quarter_hour_with_at_least_one_till()
    {
        var t = TillCompute.ComputeTillForDate(Day());
        Assert.Equal(["08:00:00", "08:15:00", "08:30:00", "08:45:00"], t.Blocks.Select(b => b.BlockTime));
        // segment = 4h x share x 4 till-units per hour.
        Assert.Equal([1.6, 4, 8, 0], t.Blocks.Select(b => b.Segment));
        Assert.Equal([0.1, 0.25, 0.5, 0], t.Blocks.Select(b => b.Allocation));
        Assert.All(t.Blocks, b => Assert.True(b.Total >= 1));
        Assert.Equal([8 * 60 + 45], t.UncoveredTrading);
        Assert.Equal(1, t.TradingHours);
        Assert.Equal(t.Blocks.Sum(b => b.Total) * 0.25, t.DailyCheckoutHours);
        Assert.True(t.HasData);
        Assert.Equal((4.0, 3.6, 1.0), (t.BenchmarkHours90, t.BenchmarkHours100, t.OverrideFactor));
    }

    [Fact]
    public void Rescales_by_the_sales_override_and_zeroes_a_closed_day()
    {
        var half = TillCompute.ComputeTillForDate(Day(new SalesOverrideRow { Method = "pct", Pct = 50 }));
        Assert.Equal(0.5, half.OverrideFactor);
        Assert.Equal([0.8, 2, 4, 0], half.Blocks.Select(b => b.Segment));
        Assert.Equal(1.5, TillCompute.ComputeTillForDate(Day(new SalesOverrideRow { Method = "value", Value = 1500 })).OverrideFactor);
        var closed = TillCompute.ComputeTillForDate(Day(new SalesOverrideRow { Closed = true }));
        Assert.Equal(0, closed.OverrideFactor);
        Assert.Empty(closed.Blocks);
        Assert.False(closed.HasData);
    }

    [Fact]
    public void Has_no_data_without_a_curve_attributes_or_an_open_door()
    {
        Assert.False(TillCompute.ComputeTillForDate(Day(curve: [])).HasData);
        Assert.False(TillCompute.ComputeTillForDate(Day() with { Attrs = null }).HasData);
        var shut = TillCompute.ComputeTillForDate(Day() with { Day = StoreDay.ClosedDay });
        Assert.False(shut.HasData);
        Assert.Equal(0, shut.TradingHours);
    }
}
