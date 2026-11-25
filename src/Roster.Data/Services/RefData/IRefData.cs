// Port of the public surface of src/lib/refdata.ts.

using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.RefData;

// ─── Request-cached reference data ───────────────────────────────────────────
// Every getter here loads a whole table (or a store's whole slice of one) ONCE
// per request (RequestMemo), so per-date computations - which run up to 14x per
// fortnight - resolve everything in memory instead of issuing their own
// queries. Reads are made as the caller (the store scope applies).

/// <summary>
/// The whole team for a store (active AND inactive - cost lookups may still
/// reference a deactivated employee via a locked shift). Callers filter.
/// </summary>
public sealed record EmployeeRow(
    Guid Id,
    string Name,
    string Role,
    string? PositionCode, // the pay_rates key (see Constants Positions)
    double ContractHours,
    double? SmYearBand,
    double? NotionalHours, // null = fortnight contract / 10
    bool Active);

/// <summary>A payroll position (also the pay_rates key set): active rows, by sort order.</summary>
public sealed record PositionRow(
    string Code,
    string Label,
    string BaseRole,
    double? WeeklyHours,
    double? YearBand,
    bool HasRate,
    double SortOrder);

public interface IRefData
{
    // ── Store basics ──
    /// <summary>getStoreState: the store's state ("NSW"...), or null.</summary>
    Task<string?> GetStoreStateAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>
    /// getRegionPrefix: the store's region code prefix ("501".."509") - regions are
    /// named "501 - Somewhere". Region-varying admin task schedules use it.
    /// </summary>
    Task<string?> GetRegionPrefixAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>getTradingWeek: the store's weekly trading hours.</summary>
    Task<IReadOnlyList<TradingDayRow>> GetTradingWeekAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>getSpecialHours: the store's special trading hours (date ranges).</summary>
    Task<IReadOnlyList<SpecialHoursRow>> GetSpecialHoursAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>getStoreAttrs: the store's attributes, or null.</summary>
    Task<StoreAttrsRow?> GetStoreAttrsAsync(Guid storeId, CancellationToken ct = default);

    // ── Public holidays (3-tier calendar, resolved in memory) ──
    /// <summary>getHolidaySet: national, the store's state's, and the store's local holidays.</summary>
    Task<IReadOnlySet<DateOnly>> GetHolidaySetAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>isPublicHoliday, from the holiday set (no per-date query).</summary>
    Task<bool> IsPublicHolidayAsync(Guid storeId, DateOnly date, CancellationToken ct = default);

    // ── School terms ──
    /// <summary>getSchoolCalendar: a state's terms and exception days (none for a null state).</summary>
    Task<SchoolCalendar> GetSchoolCalendarAsync(string? state, CancellationToken ct = default);

    /// <summary>isSchoolDayFor: a weekday inside a term of the store's state that is not an exception day.</summary>
    Task<bool> IsSchoolDayForAsync(Guid storeId, DateOnly date, CancellationToken ct = default);

    // ── Rules ──
    /// <summary>getBreakRules: the per-role break and work rules, by role.</summary>
    Task<IReadOnlyDictionary<string, BreakRule>> GetBreakRulesAsync(CancellationToken ct = default);

    /// <summary>
    /// getRolePayRules: the per-role halves of the pay rules. A null threshold means the
    /// rule does not apply to that role at all (the salaried SM/ASM).
    /// </summary>
    Task<IReadOnlyDictionary<string, Roster.Engine.Pay.RolePayRule>> GetRolePayRulesAsync(CancellationToken ct = default);

    /// <summary>
    /// getRuleOverrides: the Rostering Rules OVERRIDES (key → value); the defaults live
    /// in the engine. Unknown keys are kept and ignored by the engine.
    /// </summary>
    Task<IReadOnlyDictionary<string, double>> GetRuleOverridesAsync(CancellationToken ct = default);

    /// <summary>getLabourWindows: every labour-group time window, by weekday (Mon = 0).</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<WindowRule>>> GetLabourWindowsAsync(CancellationToken ct = default);

    /// <summary>getResetTasks: the active reset task catalogue, by sort order.</summary>
    Task<IReadOnlyList<ResetTaskRule>> GetResetTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// getPlacementConfig: Process → Planning Order's rules (normalised so every group
    /// has one), settings, step order and the groups with a SAVED rule.
    /// </summary>
    Task<PlacementConfig> GetPlacementConfigAsync(CancellationToken ct = default);

    /// <summary>getAdminTasks: the active admin task catalogue, by sort order then name (mapAdminTaskRow).</summary>
    Task<IReadOnlyList<AdminTaskRule>> GetAdminTasksAsync(CancellationToken ct = default);

    // ── Team ──
    /// <summary>getEmployees: the store's whole team, active and inactive.</summary>
    Task<IReadOnlyList<EmployeeRow>> GetEmployeesAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>getPositions: the active payroll positions, by sort order.</summary>
    Task<IReadOnlyList<PositionRow>> GetPositionsAsync(CancellationToken ct = default);

    // ── Planning inputs ──
    /// <summary>getPhReallocSet: the store's dates with public-holiday load reallocation on.</summary>
    Task<IReadOnlySet<DateOnly>> GetPhReallocSetAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>getBenchmarkDay: the store-day's benchmark row, or null.</summary>
    Task<BenchmarkDayRow?> GetBenchmarkDayAsync(Guid storeId, DateOnly date, CancellationToken ct = default);

    /// <summary>getSalesOverrideMap: the store's Sales-page overrides, by date.</summary>
    Task<IReadOnlyDictionary<DateOnly, SalesOverrideRow>> GetSalesOverrideMapAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>
    /// getQuarterHourWeek: the legacy quarter_hour_allocation curve, by weekday (Mon = 0),
    /// each day sorted by block time.
    /// </summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetQuarterHourWeekAsync(Guid storeId, CancellationToken ct = default);

    /// <summary>
    /// getPosCustomerRows: the POS interval statistics' customers (MCO + SCO) by date and
    /// quarter-hour, in date then time order.
    /// </summary>
    Task<IReadOnlyList<PosCustomerRow>> GetPosCustomerRowsAsync(Guid storeId, CancellationToken ct = default);
}
