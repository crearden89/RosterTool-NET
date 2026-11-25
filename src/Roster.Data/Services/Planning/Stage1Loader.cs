// Port of src/lib/stage1/compute.ts (the reads: computeStage1 and
// phLoadReallocation's lookups) and src/lib/till/compute.ts (the reads of
// computeTillForDate). What they do with what they read is the engine's
// Stage1Compute.ComputeStage1 and TillCompute.ComputeTillForDate.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Services.Cache;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Till;

namespace Roster.Data.Services.Planning;

// Run the Till Allocation Engine over a store/date's checkout demand. Demand per
// 15-min block is derived, not uploaded directly: the day's checkout-hours budget
// (Benchmark's Checkout_Hours at the planning 90% figure) spread across the day by
// that weekday's trade curve. All inputs come from the request-cached reference
// reads - running this for every date of a fortnight costs one round of reference
// queries in total, not five queries per date.
public sealed class TillLoader(IRefData refData, IStoreDayLoader storeDays, ITradeCurveSource curves) : ITillLoader
{
    public Task<TillComputation> ComputeTillForDateAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("till.computeTillForDate", [storeId, date], async () =>
        {
            var attrs = await refData.GetStoreAttrsAsync(storeId, ct);
            var day = await storeDays.ResolveStoreDayAsync(storeId, date, ct);
            var bench = await refData.GetBenchmarkDayAsync(storeId, date, ct);
            var allocWeek = await curves.GetTradeCurveWeekAsync(storeId, ct);
            var overrides = await refData.GetSalesOverrideMapAsync(storeId, ct);
            return TillCompute.ComputeTillForDate(new TillSources
            {
                StoreId = storeId.ToString("D"),
                Date = date,
                Attrs = attrs,
                Day = day,
                Bench = bench,
                AllocRows = allocWeek.TryGetValue(Period.DowMon0(date), out var rows) ? rows : [],
                Override = overrides.TryGetValue(date, out var o) ? o : null,
            });
        });
}

// Assemble Stage 1 inputs from the request-cached reference data (benchmark 90%
// hours, trading hours, national windows) + the Till engine, and run the
// placement. Cached: computed once per (store, date) per request - the plan
// page, roster solve and cost delta all reuse the same result. Everything
// resolves from the reference data's per-store caches, so 14 dates cost one
// round of reference reads, not 14 rounds of per-date queries.
public sealed class Stage1Loader(IRosterDbContextFactory contexts, IRefData refData, ITradeCurveSource curves, ITillLoader tills) : IStage1Loader
{
    public Task<Stage1Computation> ComputeStage1Async(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("stage1.computeStage1", [storeId, date], async () =>
            Stage1Compute.ComputeStage1(await LoadStage1SourcesAsync(storeId, date, ct)));

    public async Task<Stage1Sources> LoadStage1SourcesAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var overrides = await refData.GetSalesOverrideMapAsync(storeId, ct);
        var holidays = await refData.GetHolidaySetAsync(storeId, ct);
        var realloc = await refData.GetPhReallocSetAsync(storeId, ct);
        var till = await tills.ComputeTillForDateAsync(storeId, date, ct);
        return new Stage1Sources
        {
            Date = date,
            Bench = await refData.GetBenchmarkDayAsync(storeId, date, ct),
            // A public holiday tomorrow with its load moved to the eve brings 75% of
            // that day's Load Running into this evening (phLoadReallocation).
            NextBench = await refData.GetBenchmarkDayAsync(storeId, Period.AddDaysIso(date, 1), ct),
            TradingWeek = await refData.GetTradingWeekAsync(storeId, ct),
            SpecialHours = await refData.GetSpecialHoursAsync(storeId, ct),
            LabourWindows = await refData.GetLabourWindowsAsync(ct),
            NonOpMinutes = await NonOpMinutesAsync(storeId, date, ct),
            Holidays = holidays.ToList(),
            PhReallocDates = realloc.ToList(),
            SalesOverride = overrides.TryGetValue(date, out var o) ? o : null,
            AdminTaskRules = await refData.GetAdminTasksAsync(ct),
            RegionPrefix = await refData.GetRegionPrefixAsync(storeId, ct),
            Attrs = await refData.GetStoreAttrsAsync(storeId, ct),
            ResetTaskRules = await refData.GetResetTasksAsync(ct),
            Placement = await refData.GetPlacementConfigAsync(ct),
            TradeCurveWeek = await curves.GetTradeCurveWeekAsync(storeId, ct),
            RuleOverrides = await refData.GetRuleOverridesAsync(ct),
            PosCustomers = await refData.GetPosCustomerRowsAsync(storeId, ct),
            Till = new Stage1Till
            {
                Blocks = till.Blocks.Select(b => new Stage1TillBlock
                {
                    BlockTime = b.BlockTime,
                    Total = b.Total,
                    Segment = b.Segment,
                    Mco = b.Mco,
                    McoWanted = b.McoWanted,
                }).ToList(),
                UncoveredTrading = till.UncoveredTrading,
            },
        };
    }

    // Non-Op is additive to the hours the plan must cover (not a reallocation):
    // the day's booked minutes, read for the one store-day (not cached, as in the
    // TypeScript).
    private async Task<IReadOnlyList<double>> NonOpMinutesAsync(Guid storeId, DateOnly date, CancellationToken ct)
    {
        await using var db = contexts.Create();
        var minutes = await db.NonOpEntries.AsNoTracking().Where(n => n.StoreId == storeId && n.Date == date)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Select(n => n.Minutes).ToListAsync(ct);
        return minutes.Select(m => (double)m).ToList();
    }
}

/// <summary>The POS-derived trade curve, until D3's AssembleCurveWeek lands: nothing that plans can run without it.</summary>
public sealed class PendingTradeCurveSource : ITradeCurveSource
{
    public Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetTradeCurveWeekAsync(Guid storeId, CancellationToken ct = default) =>
        throw new NotImplementedException("getTradeCurveWeek needs D3's AssembleCurveWeek (lib/csv/posAllocation).");
}
