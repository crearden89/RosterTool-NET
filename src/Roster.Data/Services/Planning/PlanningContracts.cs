// Port of the public surface of the loaders around the planning engine:
// src/lib/storeDay.ts, src/lib/stage1/compute.ts, src/lib/stage2/planSlots.ts
// (dayCheckOptions), src/lib/stage1/planShifts.ts and src/lib/stage1/planPeriod.ts.

using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.Planning;

/// <summary>resolveStoreDay's loader: the store's real staffed window on a date.</summary>
public interface IStoreDayLoader
{
    /// <summary>
    /// resolveStoreDay: special trading hours over the weekly pattern, the close-down
    /// setting applied (Core StoreDays.ResolveStoreDay over the request-cached reads).
    /// </summary>
    Task<StoreDay> ResolveStoreDayAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}

/// <summary>computeStage1's loader: everything a day plan needs, read and run through Stage 1.</summary>
public interface IStage1Loader
{
    /// <summary>
    /// computeStage1: the store-day's Stage 1 placement (request-cached per store and
    /// date: the plan page, the roster solve and the cost delta reuse one result).
    /// </summary>
    Task<Stage1Computation> ComputeStage1Async(Guid storeId, DateOnly date, CancellationToken ct = default);

    /// <summary>The store-day's Stage 1 inputs as read from the database (what ComputeStage1Async runs on).</summary>
    Task<Stage1Sources> LoadStage1SourcesAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}

/// <summary>The day checker's options for a store-day (planSlots' dayCheckOptions).</summary>
public interface IDayCheckOptionsLoader
{
    /// <summary>
    /// dayCheckOptions: what the day checker holds a store's plan day to - its floor-cover
    /// rules, each slot type's longest legal shift and whether it is a school day.
    /// </summary>
    Task<DayCheckOptions> DayCheckOptionsAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}

/// <summary>computePlanShifts's loader: the Task view's painted, labelled and checked day.</summary>
public interface IPlanShiftsLoader
{
    /// <summary>computePlanShifts: the day's plan slots painted with their tasks, labelled and checked.</summary>
    Task<PlanShiftComputation> ComputePlanShiftsAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}

/// <summary>Which fortnight a store plans (stage1/planPeriod).</summary>
public sealed record PlanningPeriod(DateOnly PeriodStart, IReadOnlyList<DateOnly> OverduePeriods);

public interface IPlanPeriods
{
    /// <summary>
    /// getPlanPeriodDates: the 14 dates of a planning fortnight - <paramref name="periodStart"/>'s,
    /// or the one stores should be planning now (the one after today's).
    /// </summary>
    IReadOnlyList<DateOnly> GetPlanPeriodDates(DateOnly? periodStart = null);

    /// <summary>
    /// resolvePlanningPeriod: the fortnight the Plan screen opens on - the earliest grid
    /// period from the current one to the planning one that still has un-approved
    /// benchmarked days - and the earlier periods that are behind. <paramref name="asPeer"/>
    /// reads as the system (a store outside the caller's scope; the caller hides the banner).
    /// </summary>
    Task<PlanningPeriod> ResolvePlanningPeriodAsync(Guid storeId, bool asPeer = false, CancellationToken ct = default);
}
