// Port of the database functions in supabase/migrations: approve_roster_day
// (0049, the live body), overview_rollup (0027), leave_period_ends (0037),
// is_public_holiday (0007) and bump_cron_run (0043).

using System.Text.Json;

namespace Roster.Data.Services.Functions;

/// <summary>One task tile of a published shift (approve_roster_day's p_shifts[].blocks[]).</summary>
public sealed record RosterDayTile(TimeOnly BlockTime, string LabourGroup, string? SubTask = null);

/// <summary>One shift of a published day (approve_roster_day's p_shifts[]).</summary>
public sealed record RosterDayShift(
    Guid EmployeeId,
    string EmployeeName,
    string Role,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int BreakMinutes,
    bool Locked,
    IReadOnlyList<RosterDayTile> Blocks);

/// <summary>One date of overview_rollup: the in-scope stores' totals for the day.</summary>
public sealed record OverviewRollupRow
{
    public DateOnly Date { get; init; }
    public double ForecastSales { get; init; }
    public double CustomerCount { get; init; }
    public double BenchItems { get; init; }
    public double Hours90 { get; init; }
    public double Hours100 { get; init; }
    public double ActualSales { get; init; }
    public double ActualItems { get; init; }
    public double ActualCustomers { get; init; }
    public double ActualHours100 { get; init; }
    public double KronosHours { get; init; }
    public double NonopMinutes { get; init; }
    public bool AnyApproved { get; init; }
    public bool AnyGenerated { get; init; }
}

/// <summary>The SQL functions of the TypeScript app's database, as a service.</summary>
public interface IDatabaseFunctions
{
    /// <summary>
    /// approve_roster_day: publish one store-day's roster atomically - the day's
    /// published shifts (locked ones included) and their tiles are replaced by
    /// <paramref name="shifts"/>, and the day's roster is marked approved LAST, so
    /// a failure leaves the day cleanly un-approved. Runs as the system (the
    /// service role): callers authorise first.
    /// </summary>
    Task ApproveRosterDayAsync(Guid storeId, DateOnly date, IReadOnlyList<RosterDayShift>? shifts, CancellationToken ct = default);

    /// <summary>
    /// overview_rollup: per-date totals across <paramref name="storeIds"/> from
    /// <paramref name="from"/> to <paramref name="to"/>, at most one row per date,
    /// ordered by date. Reads as the caller (SECURITY INVOKER: stores outside the
    /// caller's scope count as nothing); <paramref name="asPeer"/> reads as the
    /// system - the Dashboard's deliberate peer path. Null columns count as 0.
    /// </summary>
    Task<IReadOnlyList<OverviewRollupRow>> OverviewRollupAsync(
        IReadOnlyList<Guid> storeIds, DateOnly from, DateOnly to, bool asPeer = false, CancellationToken ct = default);

    /// <summary>
    /// leave_period_ends: the distinct pay-period ends held in the leave-liability
    /// snapshots, newest first - every store's, or one store's. Reads as the system.
    /// </summary>
    Task<IReadOnlyList<DateOnly>> LeavePeriodEndsAsync(Guid? storeId = null, CancellationToken ct = default);

    /// <summary>is_public_holiday: a national, state (the store's) or local public holiday.</summary>
    Task<bool> IsPublicHolidayAsync(Guid storeId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// bump_cron_run: record that a scheduled job completed - upsert its heartbeat,
    /// counting the run, with what it returned (serialised as JSON; null = none).
    /// Runs as the system.
    /// </summary>
    Task BumpCronRunAsync(string job, JsonElement? result = null, CancellationToken ct = default);
}
