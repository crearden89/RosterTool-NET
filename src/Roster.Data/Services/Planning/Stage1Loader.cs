// Port of src/lib/stage1/compute.ts (the reads: computeStage1 and
// phLoadReallocation's lookups). Everything computeStage1 does with what it
// read is the engine's Stage1Compute.ComputeStage1.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Services.Cache;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.Planning;

/// <summary>
/// The two reads Stage 1 shares with the till and the plan page that belong to
/// other agents' code: the till engine's result for the day (E3's TillCompute)
/// and the trade curve derived from the POS statistics (D3's AssembleCurveWeek).
/// </summary>
public interface IStage1Feeds
{
    /// <summary>getTradeCurveWeek: the store's trade curve by weekday (Mon = 0), POS-derived with the legacy fallback.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetTradeCurveWeekAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>What Stage 1 reads of computeTillForDate's result for the store-day.</summary>
    Task<Stage1Till> TillForStage1Async(Guid storeId, DateOnly date, CancellationToken ct = default);
}

// Assemble Stage 1 inputs from the request-cached reference data (benchmark 90%
// hours, trading hours, national windows) + the Till engine, and run the
// placement. Cached: computed once per (store, date) per request - the plan
// page, roster solve and cost delta all reuse the same result. Everything
// resolves from the reference data's per-store caches, so 14 dates cost one
// round of reference reads, not 14 rounds of per-date queries.
public sealed class Stage1Loader(IRosterDbContextFactory contexts, IRefData refData, IStage1Feeds feeds) : IStage1Loader
{
    public Task<Stage1Computation> ComputeStage1Async(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("stage1.computeStage1", [storeId, date], async () =>
            Stage1Compute.ComputeStage1(await LoadStage1SourcesAsync(storeId, date, ct)));

    public async Task<Stage1Sources> LoadStage1SourcesAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var overrides = await refData.GetSalesOverrideMapAsync(storeId, ct);
        var holidays = await refData.GetHolidaySetAsync(storeId, ct);
        var realloc = await refData.GetPhReallocSetAsync(storeId, ct);
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
            TradeCurveWeek = await feeds.GetTradeCurveWeekAsync(storeId, ct),
            RuleOverrides = await refData.GetRuleOverridesAsync(ct),
            PosCustomers = await refData.GetPosCustomerRowsAsync(storeId, ct),
            Till = await feeds.TillForStage1Async(storeId, date, ct),
        };
    }

    // Non-Op is additive to the hours the plan must cover (not a reallocation):
    // the day's booked minutes, read for the one store-day (not cached, as in the
    // TypeScript - an edit on the Non-Op page shows at the next generate).
    private async Task<IReadOnlyList<double>> NonOpMinutesAsync(Guid storeId, DateOnly date, CancellationToken ct)
    {
        await using var db = contexts.Create();
        var minutes = await db.NonOpEntries.AsNoTracking().Where(n => n.StoreId == storeId && n.Date == date)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Select(n => n.Minutes).ToListAsync(ct);
        return minutes.Select(m => (double)m).ToList();
    }
}

/// <summary>
/// The till and the POS-derived curve, until their engine and feed halves land
/// (E3's TillCompute, D3's AssembleCurveWeek): Stage 1 cannot be computed without them.
/// </summary>
public sealed class PendingStage1Feeds : IStage1Feeds
{
    public Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetTradeCurveWeekAsync(Guid storeId, CancellationToken ct = default) =>
        throw new NotImplementedException("getTradeCurveWeek needs D3's AssembleCurveWeek (lib/csv/posAllocation).");

    public Task<Stage1Till> TillForStage1Async(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        throw new NotImplementedException("computeTillForDate needs E3's TillCompute (lib/till/compute).");
}
