// Port of src/lib/csv/posAllocation.ts.
// ─── Deriving the Quarter-Hour curve from POS statistics ────────────────────
// Four weeks of real trade become the standing weekly allocation curve the
// Till engine spreads each day's checkout hours across.
//
// A block's share of the day is priced on "time in motion" - the work the
// checkout actually has to do - rather than on customers alone, because an
// 80-item trolley and a 3-item basket are one customer each and very different
// amounts of till time:
//
//   with SCO   weight = items x 0.0008 + customers x 0.0006
//   MCO only   weight = items x 0.0010 + customers x 0.0009
//
// A manned-only store carries the higher per-unit weights: every item and every
// customer goes through a staffed register, where an SCO store diverts part of
// that work to the bank.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Feeds;

/// <summary>Per-unit minutes-in-motion.</summary>
public sealed record PosWeight(double Items, double Customers);

/// <param name="DayOfWeek">0 = Monday.</param>
/// <param name="BlockTime">"HH:MM:00".</param>
/// <param name="Allocation">Fraction of that weekday's total (sums to 1 per day).</param>
public sealed record DerivedAllocation(string StoreCode, int DayOfWeek, string BlockTime, double Allocation);

/// <param name="ExcludeDates"><c>"{storeCode}|{isoDate}"</c> for dates to leave out - public holidays.</param>
public sealed record DeriveOptions(IReadOnlySet<string>? ExcludeDates = null);

/// <param name="WeekdaysWithoutData">Weekdays with no usable trading date - these keep a flat curve.</param>
public sealed record DeriveReport(
    string StoreCode,
    bool HasSco,
    int DatesUsed,
    int DatesExcludedPublicHoliday,
    int DatesExcludedNoTrade,
    IReadOnlyList<int> WeekdaysWithoutData);

public sealed record DerivedCurve(IReadOnlyList<DerivedAllocation> Allocations, IReadOnlyList<DeriveReport> Reports);

public static class PosAllocation
{
    public static readonly PosWeight ScoWeights = new(Items: 0.0008, Customers: 0.0006);
    public static readonly PosWeight McoOnlyWeights = new(Items: 0.001, Customers: 0.0009);

    /// <summary>0 = Monday.</summary>
    public static int DowMon0(DateOnly date) => Period.DowMon0(date);

    /// <summary>
    /// Turn parsed POS rows into the weekly allocation curve.
    ///
    /// A store counts as having SCO when the file carries any self-checkout trade
    /// for it - the export only reports an SCO line where lanes exist, so the file
    /// is self-describing and no store attribute needs consulting.
    ///
    /// Dates are dropped when they are public holidays (trade is nothing like a
    /// normal weekday, and four weeks is too small a sample to absorb one) or when
    /// the store recorded no trade at all (a closure would otherwise drag the whole
    /// weekday's curve toward zero). Weights for the surviving dates are summed per
    /// weekday and normalised, so each weekday's blocks sum to 1.
    /// </summary>
    public static DerivedCurve DeriveAllocations(IReadOnlyList<PosQuarterHourRow> rows, DeriveOptions? options = null)
    {
        var exclude = options?.ExcludeDates ?? new HashSet<string>();
        var byStore = new OrderedDictionary<string, List<PosQuarterHourRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!byStore.TryGetValue(r.StoreCode, out var list)) byStore[r.StoreCode] = list = [];
            list.Add(r);
        }

        var allocations = new List<DerivedAllocation>();
        var reports = new List<DeriveReport>();

        foreach (var (storeCode, storeRows) in byStore)
        {
            var hasSco = storeRows.Any(r => r.ScoItems > 0 || r.ScoCustomers > 0);
            var w = hasSco ? ScoWeights : McoOnlyWeights;
            double WeightOf(PosQuarterHourRow r) =>
                (r.McoItems + r.ScoItems) * w.Items + (r.McoCustomers + r.ScoCustomers) * w.Customers;

            // Total per date first, so a no-trade date can be spotted before it is used.
            var totalByDate = new OrderedDictionary<DateOnly, double>();
            foreach (var r in storeRows)
                totalByDate[r.Date] = (totalByDate.TryGetValue(r.Date, out var t) ? t : 0) + WeightOf(r);

            var excludedPh = 0;
            var excludedNoTrade = 0;
            var usable = new HashSet<DateOnly>();
            foreach (var (date, total) in totalByDate)
            {
                if (exclude.Contains($"{storeCode}|{date.ToIso()}"))
                {
                    excludedPh++;
                    continue;
                }
                if (total <= 0)
                {
                    excludedNoTrade++;
                    continue;
                }
                usable.Add(date);
            }

            // Sum the surviving dates' weights per weekday and block.
            var byDow = new Dictionary<int, OrderedDictionary<string, double>>();
            foreach (var r in storeRows)
            {
                if (!usable.Contains(r.Date)) continue;
                var dow = DowMon0(r.Date);
                if (!byDow.TryGetValue(dow, out var blocks)) byDow[dow] = blocks = new OrderedDictionary<string, double>(StringComparer.Ordinal);
                blocks[r.BlockTime] = (blocks.TryGetValue(r.BlockTime, out var b) ? b : 0) + WeightOf(r);
            }

            var weekdaysWithoutData = new List<int>();
            for (var dow = 0; dow < 7; dow++)
            {
                byDow.TryGetValue(dow, out var blocks);
                double total = 0;
                if (blocks is not null)
                    foreach (var n in blocks.Values) total += n;
                if (blocks is null || total <= 0)
                {
                    weekdaysWithoutData.Add(dow);
                    continue;
                }
                foreach (var (blockTime, weight) in blocks)
                {
                    // Zero-weight blocks carry no share and are simply omitted, exactly as
                    // a quiet block in the old file did.
                    if (weight <= 0) continue;
                    allocations.Add(new DerivedAllocation(storeCode, dow, blockTime, weight / total));
                }
            }

            reports.Add(new DeriveReport(storeCode, hasSco, usable.Count, excludedPh, excludedNoTrade, weekdaysWithoutData));
        }

        return new DerivedCurve(allocations, reports);
    }

    // ─── Assembling the curve a store actually plans with ───────────────────────
    // The trade curve is now derived at READ time from the POS interval
    // statistics, so retuning the weights or reloading a sample re-states every
    // plan without an ingest step. The old quarter_hour_allocation table stays
    // only as the LEGACY fallback: its rows were written by the retired
    // pre-computed upload, and a store keeps them exactly until its own POS data
    // arrives. Merging is per WEEKDAY - a store whose POS sample is missing a
    // weekday (reported at upload) keeps the legacy curve for that day rather
    // than planning it with no shape at all.

    /// <summary>
    /// The store's weekly curve: weekday (0 = Monday) to that day's rows, in the order the
    /// TypeScript Map holds them (legacy weekdays first as they appear, derived weekdays
    /// replacing them in place or appended). Derived days are sorted by block time.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>> AssembleCurveWeek(
        IReadOnlyList<PosQuarterHourRow> pos, IEnumerable<QuarterHourRow> legacy, IReadOnlySet<DateOnly> holidayDates)
    {
        var byDow = new OrderedDictionary<int, IReadOnlyList<QuarterHourRow>>();
        foreach (var r in legacy)
        {
            if (!byDow.TryGetValue(r.DayOfWeek, out var arr)) byDow[r.DayOfWeek] = arr = new List<QuarterHourRow>();
            ((List<QuarterHourRow>)arr).Add(r);
        }

        if (pos.Count > 0)
        {
            // Single-store call sites: the storeCode on the rows is irrelevant here,
            // but DeriveAllocations keys its exclusions by it, so mirror whatever the
            // rows carry.
            var exclude = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in pos)
                if (holidayDates.Contains(r.Date)) exclude.Add($"{r.StoreCode}|{r.Date.ToIso()}");
            var allocations = DeriveAllocations(pos, new DeriveOptions(exclude)).Allocations;
            var derived = new OrderedDictionary<int, List<QuarterHourRow>>();
            foreach (var a in allocations)
            {
                if (!derived.TryGetValue(a.DayOfWeek, out var arr)) derived[a.DayOfWeek] = arr = [];
                arr.Add(new QuarterHourRow { DayOfWeek = a.DayOfWeek, BlockTime = a.BlockTime, Allocation = a.Allocation });
            }
            foreach (var (dow, rows) in derived)
                byDow[dow] = Js.Sort(rows, (a, b) => Js.LocaleCompare(a.BlockTime, b.BlockTime));
        }
        return byDow;
    }
}
