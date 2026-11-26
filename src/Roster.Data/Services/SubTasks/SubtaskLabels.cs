// Port of the reads of src/lib/subtasks/day.ts (labelPlanDay, withDayLabels,
// labelRosterDay), src/lib/subtasks/stored.ts (publishedSubtaskRuns) and
// refdata's getLoadBoardAverages. The labelling is the engine's SubTasks.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Scope;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Held;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.SubTasks;

namespace Roster.Data.Services.SubTasks;

// ─── Sub-tasks: label one planned day ────────────────────────────────────────
// The context the labels need - the load board's average day for the date's
// weekday, the share settings and the Reset tasks' saved mappings - read once
// per request and handed to the pure labeller. Used when a plan or a roster is
// generated, when a held plan or roster from before sub-tasks is shown, when an
// edit is saved, and when a published roster is printed. Labels only: nothing
// that plans or counts hours reads them.
public interface ISubtaskLabels
{
    /// <summary>
    /// getLoadBoardAverages: what arrives on an average day of each weekday, by Load Running
    /// sub-task, from the load types and the store's pallet counts over the eight weeks
    /// before the fortnight. An unreadable load board is no history.
    /// </summary>
    Task<LoadBoardAverages> GetLoadBoardAveragesAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>The labels' context for a store-day (labelPlanDay's reads).</summary>
    Task<LabelContext> LabelContextAsync(Guid storeId, DateOnly date, ResetSource? stage1, CancellationToken ct = default);

    /// <summary>labelPlanDay: every Reset, Load Running, Cleaning and Specials tile labelled.</summary>
    Task<List<PlanShift>> LabelPlanDayAsync(Guid storeId, DateOnly date, ResetSource? stage1, IReadOnlyList<PlanShift> shifts, CancellationToken ct = default);

    /// <summary>
    /// withDayLabels: a roster day held from before sub-tasks, labelled as it is read the way
    /// a roster is labelled when it is built (the held plan's labels on the tiles that come
    /// unchanged from it, the rules for the rest); a day with every tile labelled is
    /// returned itself.
    /// </summary>
    Task<IReadOnlyList<Shift>> WithDayLabelsAsync(
        Guid storeId, DateOnly date, ResetSource? stage1, IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> shifts, CancellationToken ct = default);

    /// <summary>labelRosterDay: a roster day labelled from its held plan (the plan's labels carry over, on every day).</summary>
    Task<List<Shift>> LabelRosterDayAsync(
        Guid storeId, DateOnly date, ResetSource? stage1, IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> roster, CancellationToken ct = default);

}

public sealed class SubtaskLabels(IRosterDbContextFactory contexts, IRefData refData) : ISubtaskLabels
{
    public Task<LoadBoardAverages> GetLoadBoardAveragesAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getLoadBoardAverages", [storeId, periodStart], async () =>
        {
            var (types, rows) = await ReadLoadBoardAsync(storeId, periodStart, ct);
            return LoadBoard.LoadBoardAverages(types, rows, periodStart);
        });

    /// <summary>
    /// The load types and the store's daily loads the load board averages. Ids become text
    /// here, after the read: SQL Server (and SQLite) would print a uniqueidentifier in upper
    /// case inside the query, unlike every other id the app writes as text.
    /// </summary>
    internal async Task<(List<LoadTypeRow> Types, List<DailyLoadRow> Rows)> ReadLoadBoardAsync(
        Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        var types = (await db.LoadTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                .Select(t => new { t.Id, t.Name, t.MinutesPerPallet, t.Priority, t.SubTask })
                .ToListAsync(ct))
            .Select(t => new LoadTypeRow { Id = t.Id.ToString(), Name = t.Name, MinutesPerPallet = t.MinutesPerPallet, Priority = t.Priority, SubTask = t.SubTask })
            .ToList();
        List<DailyLoadRow> rows;
        try
        {
            var from = LoadBoard.LoadHistoryFrom(periodStart);
            rows = (await db.DailyLoads.AsNoTracking()
                .Where(d => d.StoreId == storeId && d.Date >= from && d.Date <= periodStart)
                .OrderBy(d => d.Date).ThenBy(d => d.LoadTypeId)
                .Select(d => new { d.Date, d.LoadTypeId, d.Qty })
                .ToListAsync(ct))
                .Select(d => new DailyLoadRow { Date = d.Date, LoadTypeId = d.LoadTypeId.ToString(), Qty = d.Qty })
                .ToList();
        }
        catch (Exception e) when (e is DbUpdateException or System.Data.Common.DbException)
        {
            rows = []; // a label must never stop a plan: an unreadable load board is no history
        }
        return (types, rows);
    }

    public async Task<LabelContext> LabelContextAsync(Guid storeId, DateOnly date, ResetSource? stage1, CancellationToken ct = default)
    {
        var load = await GetLoadBoardAveragesAsync(storeId, Period.PeriodStartFor(date), ct);
        var overrides = await refData.GetRuleOverridesAsync(ct);
        IReadOnlyList<ResetTaskRule> resetTasks;
        try
        {
            resetTasks = await refData.GetResetTasksAsync(ct);
        }
        catch (Exception e) when (e is DbUpdateException or System.Data.Common.DbException)
        {
            resetTasks = []; // an unreadable catalogue is "by name"
        }
        return Day.ContextFor(date, stage1, load, RosteringRules.Resolve(overrides), resetTasks.Select(t => (t.Name, t.SubTask)));
    }

    public async Task<List<PlanShift>> LabelPlanDayAsync(Guid storeId, DateOnly date, ResetSource? stage1, IReadOnlyList<PlanShift> shifts, CancellationToken ct = default) =>
        Day.LabelPlanDay(shifts, await LabelContextAsync(storeId, date, stage1, ct));

    public async Task<IReadOnlyList<Shift>> WithDayLabelsAsync(
        Guid storeId, DateOnly date, ResetSource? stage1, IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> shifts, CancellationToken ct = default) =>
        Label.NeedsLabels(shifts) ? Day.WithDayLabels(plan, shifts, await LabelContextAsync(storeId, date, stage1, ct)) : shifts;

    public async Task<List<Shift>> LabelRosterDayAsync(
        Guid storeId, DateOnly date, ResetSource? stage1, IReadOnlyList<PlanShift> plan, IReadOnlyList<Shift> roster, CancellationToken ct = default) =>
        Day.LabelRosterDay(plan, roster, await LabelContextAsync(storeId, date, stage1, ct));
}

/// <summary>subtasks/stored: the sub-tasks of a published roster, for the printed roster.</summary>
public interface IPublishedSubtasks
{
    /// <summary>
    /// publishedSubtaskRuns: each published shift's sub-task codes in the order they are
    /// worked, keyed "employeeId:yyyy-mm-dd" - what the printed roster shows under the shift
    /// times. Rows published before sub-tasks are labelled when read; nothing is written back.
    /// Reads as <paramref name="access"/> (default: the current identity).
    /// </summary>
    Task<JsMap<string, List<string>>> PublishedSubtaskRunsAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<DateOnly> dates, AccessContext? access = null, CancellationToken ct = default);
}

public sealed class PublishedSubtasks(IRosterDbContextFactory contexts, ISubtaskLabels labels, IHeldPlans heldPlans) : IPublishedSubtasks
{
    // ─── Sub-tasks on a PUBLISHED roster (the printed roster) ────────────────
    // A published roster's tiles are rows of shift_task_blocks; since 0049 each
    // carries its sub-task. Rows published before that have none and are labelled
    // here when read, the way a roster is labelled when it is built: the held
    // plan's labels on the tiles that come unchanged from it, the normal rules for
    // the rest (Reset from the held plan's Stage 1 record). Nothing is written
    // back.
    public async Task<JsMap<string, List<string>>> PublishedSubtaskRunsAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<DateOnly> dates, AccessContext? access = null, CancellationToken ct = default)
    {
        var list = dates.ToList();
        await using var db = access is null ? contexts.Create() : contexts.Create(access);
        var shifts = await db.GeneratedShifts.AsNoTracking().Where(s => s.StoreId == storeId && list.Contains(s.Date))
            .OrderBy(s => s.Date).ThenBy(s => s.StartTime).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.EmployeeId, s.Date, s.StartTime, s.EndTime }).ToListAsync(ct);
        var ids = shifts.Select(s => s.Id).ToList();
        var tiles = await db.ShiftTaskBlocks.AsNoTracking().Where(t => ids.Contains(t.ShiftId))
            .OrderBy(t => t.ShiftId).ThenBy(t => t.BlockTime)
            .Select(t => new { t.ShiftId, t.BlockTime, t.LabourGroup, t.SubTask }).ToListAsync(ct);
        PlanSnapshot? plan;
        try
        {
            plan = await heldPlans.ReadPlanAsync(storeId, periodStart, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            plan = null; // a label must never stop the roster printing
        }
        var heldOf = new Dictionary<DateOnly, PlanSnapshotDay>();
        foreach (var d in plan?.Days ?? []) heldOf[d.Date] = d;
        var contextsByDate = new Dictionary<DateOnly, LabelContext>();
        foreach (var date in shifts.Select(s => s.Date).Distinct())
            contextsByDate[date] = await labels.LabelContextAsync(storeId, date, heldOf.TryGetValue(date, out var held) ? ResetSource.From(held.Result) : null, ct);
        return Stored.PublishedSubtaskRuns(
            shifts.Select(s => new StoredShift(s.Id.ToString("D"), s.EmployeeId.ToString("D"), s.Date, RefDataReader.Hms(s.StartTime), RefDataReader.Hms(s.EndTime))).ToList(),
            tiles.Select(t => new StoredTile(t.ShiftId.ToString("D"), RefDataReader.Hms(t.BlockTime), t.LabourGroup, t.SubTask)).ToList(),
            date => contextsByDate[date],
            // The held plan's shifts for the date, whose labels carry to the tiles that come unchanged from them.
            date => heldOf.TryGetValue(date, out var held) ? (held.Plan?.Shifts ?? []).Select(CarryShift.Of).ToList() : []);
    }
}
