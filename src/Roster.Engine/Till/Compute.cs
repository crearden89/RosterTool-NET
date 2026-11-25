// Port of the pure part of src/lib/till/compute.ts (computeTillForDate). Its
// reads - the store's checkout attributes, the resolved store day, the
// benchmark day, the trade curve week and the sales overrides - are the data
// layer's; it hands them to ComputeTillForDate here.

using System.Text.Json.Serialization;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Till;

/// <summary>A block's tills, with its time and its share of the day.</summary>
public sealed record TillBlockRow : BlockResult
{
    [JsonPropertyOrder(-2)]
    public string BlockTime { get; set; } = ""; // HH:MM:SS
    // That block's share of the day from the Quarter-Hour pattern. Carried
    // through so the Till Preview screen can show the derivation
    // (segment = target x allocation x 4) rather than only its result.
    [JsonPropertyOrder(-1)]
    public double Allocation { get; set; }

    public TillBlockRow() { }

    public TillBlockRow(string blockTime, double allocation, BlockResult r) : base(r)
    {
        BlockTime = blockTime;
        Allocation = allocation;
    }
}

public sealed record TillComputation
{
    public string StoreId { get; init; } = "";
    public DateOnly Date { get; init; }
    public CheckoutAttrs? Attrs { get; init; }
    public List<TillBlockRow> Blocks { get; init; } = new();
    public double DailyCheckoutHours { get; init; }
    // Hours the doors are open: every one of their quarter-hours has a till.
    public double TradingHours { get; init; }
    // Trading quarter-hours (minutes) the trading pattern has no share for -
    // planned with one till all the same (owner decision 18), and flagged.
    public List<double> UncoveredTrading { get; init; } = new();
    public bool HasData { get; init; }
    // ── Reference figures, for explanation only - nothing plans off these ──
    // The Benchmark file's checkout hours for the day, before and after the
    // 90% planning transform. `BenchmarkHours90` is the TARGET the engine's
    // allocation is measured against; the two differ because the tier table
    // rounds each block to whole tills (and the SCO credit can suppress them),
    // so the day's planned hours are not expected to match it exactly.
    public double BenchmarkHours100 { get; init; }
    public double BenchmarkHours90 { get; init; }
    // Sales-override rescale actually applied (1 = none, 0 = closed).
    public double OverrideFactor { get; init; }
}

/// <summary>
/// Everything computeTillForDate read for one store-day, as the data layer loads
/// it (lib/refdata's getters, request-cached per store).
/// </summary>
public sealed record TillSources
{
    public string StoreId { get; init; } = "";
    public DateOnly Date { get; init; }
    /// <summary>getStoreAttrs(store); null when the store has no attributes row.</summary>
    public StoreAttrsRow? Attrs { get; init; }
    /// <summary>resolveStoreDay(store, date): the doors-open window, special-hours aware.</summary>
    public StoreDay Day { get; init; } = StoreDay.ClosedDay;
    /// <summary>getBenchmarkDay(store, date).</summary>
    public BenchmarkDayRow? Bench { get; init; }
    /// <summary>getTradeCurveWeek(store).get(DowMon0(date)): that weekday's curve (empty when none).</summary>
    public IReadOnlyList<QuarterHourRow> AllocRows { get; init; } = [];
    /// <summary>getSalesOverrideMap(store).get(date).</summary>
    public SalesOverrideRow? Override { get; init; }
}

public static class TillCompute
{
    // Segment must be in TILL-UNITS per block (the engine's tier table - 1.2,
    // 2.1, ... - is calibrated to concurrent-till demand). checkout_hours_90 x
    // allocation gives HOURS of checkout work in the 15-min block; one till
    // supplies 0.25h per block, so convert: till-units = hours / 0.25. Without
    // this the segment ran 4x too small and every block collapsed to 1 till.
    private const double HoursToTillUnits = 60.0 / 15;

    /// <summary>
    /// The sales-override rescale (spec 4.3 / 11.6): that date's demand is
    /// multiplied by this before the engine runs. A closed day zeroes all planning.
    /// </summary>
    public static double OverrideFactor(SalesOverrideRow? @override, BenchmarkDayRow? bench)
    {
        double factor = 1;
        if (@override is not null)
        {
            if (@override.Closed) factor = 0;
            else if (@override.Method == "pct" && @override.Pct is not null) factor = @override.Pct.Value / 100;
            else if (@override.Method == "value" && @override.Value is not null)
            {
                var @base = bench is not null ? bench.SalesForecast : 0;
                factor = @base > 0 ? @override.Value.Value / @base : 1;
            }
        }
        return factor;
    }

    /// <summary>
    /// Run the Till Allocation Engine over a store/date's checkout demand. Demand
    /// per 15-min block is derived, not uploaded directly: the day's total
    /// checkout-hours budget (Benchmark's Checkout_Hours, at the planning 90%
    /// figure) is spread across the day using that weekday's Allocation curve
    /// (derived per weekday from the POS interval statistics, legacy table as
    /// fallback - a standing per-store weekly pattern, not a per-date one).
    /// Segment(block) = checkout_hours_90 x allocation(block).
    /// </summary>
    public static TillComputation ComputeTillForDate(TillSources src)
    {
        // The store's trading window (open → close), special-hours aware: a till is
        // staffed whenever the doors are open (owner decision 18).
        var day = src.Day;
        var bench = src.Bench;
        var allocRows = src.AllocRows;
        var attrRow = src.Attrs;
        var tradingHoursSpan = day.Closed ? 0 : (day.TradingClose - day.TradingOpen) / 60;

        var factor = OverrideFactor(src.Override, bench);

        var attrs = attrRow is null
            ? null
            : new CheckoutAttrs
            {
                HasSco = attrRow.HasSco,
                IsNewScoModel = attrRow.IsNewScoModel,
                McoRegisters = attrRow.McoRegisters,
                ScoLanes = attrRow.ScoLanes,
                ScoEfficiency = attrRow.ScoEfficiency,
                FirstStep = attrRow.FirstStep,
                ScoShare = attrRow.ScoShare,
            };
        var checkoutHours90 = bench is not null ? bench.CheckoutHours90 : 0;

        // One block per trading quarter-hour - only those carry checkout demand, so
        // a reduced or closed special-hours date sheds the out-of-hours demand - and
        // EVERY trading quarter-hour, with at least one till, even where the
        // pattern has no share for it or the demand rounds to none (owner decision
        // 18; review RC-38: a trading quarter-hour could be planned with no till).
        var blocks = new List<TillBlockRow>();
        var uncoveredTrading = new List<double>();
        if (attrs is not null && bench is not null && allocRows.Count > 0 && factor > 0 && !day.Closed)
        {
            var share = new JsMap<double, double>(allocRows.Select(a => (Grid.TimeToMin(a.BlockTime), a.Allocation)));
            for (var m = day.TradingOpen; m < day.TradingClose; m += 15)
            {
                var found = share.TryGet(m, out var allocation);
                if (!found) uncoveredTrading.Add(m);
                var a = found ? allocation : 0;
                var r = TillEngine.LiftToOneTill(TillEngine.ComputeBlock(checkoutHours90 * a * HoursToTillUnits * factor, attrs), attrs);
                blocks.Add(new TillBlockRow($"{Grid.MinToTime(m)}:00", a, r));
            }
        }

        // NO budget cap. The tier table rounds each block up whenever the segment
        // carries a remainder of 0.1+, which lifts the day's till hours above the
        // benchmark's checkout figure (G242 17/08: 20.25h planned vs a 14.28h
        // budget). Trimming that back down - which is what used to happen here -
        // understaffed the registers against the tool this replaces. Checkout now
        // keeps its rounded hours and Stage 1 funds the excess by borrowing from
        // the other labour groups (Till/Borrow.cs), as the old tool did.

        return new TillComputation
        {
            StoreId = src.StoreId,
            Date = src.Date,
            Attrs = attrs,
            Blocks = blocks,
            DailyCheckoutHours = TillEngine.DailyCheckoutHours(blocks),
            TradingHours = tradingHoursSpan,
            UncoveredTrading = uncoveredTrading,
            HasData = blocks.Count > 0,
            BenchmarkHours100 = bench is not null ? bench.CheckoutHours100 : 0,
            BenchmarkHours90 = checkoutHours90,
            OverrideFactor = factor,
        };
    }
}
