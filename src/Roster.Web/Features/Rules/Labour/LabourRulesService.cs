// Port of src/app/(app)/admin/rules/labour/actions.ts and the reads in its page.tsx.

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Overview;
using Roster.Engine.Planning.Stage1;

namespace Roster.Web.Features.Rules.Labour;

public sealed class LabourRulesService(IRosterDbContextFactory contexts, IStoreScope scope, TimeProvider clock) : ILabourRulesService
{
    public async Task<LabourRulesData> LoadAsync(CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        var windows = await db.LabourGroupRules.AsNoTracking().OrderBy(r => r.DayOfWeek).ToListAsync(cancellationToken);
        var tasks = await db.AdminTasks.AsNoTracking().Where(t => t.Active).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(cancellationToken);
        var priority = await db.LabourGroupPriority.AsNoTracking().OrderBy(p => p.Priority).Select(p => p.LabourGroup).ToListAsync(cancellationToken);
        var regions = await db.Regions.AsNoTracking().OrderBy(r => r.Name).Select(r => r.Name).ToListAsync(cancellationToken);
        var resets = await db.ResetTasks.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(cancellationToken);
        var values = await db.RosteringRules.AsNoTracking().Select(r => new { r.Key, r.Value }).ToListAsync(cancellationToken);
        var overrides = new Dictionary<string, double>();
        foreach (var r in values) overrides[r.Key] = r.Value;

        return new LabourRulesData(
            windows.Select(r => new LabourWindowRow(r.Id, r.LabourGroup, r.DayOfWeek, Hm(r.WindowStart), Hm(r.WindowEnd), r.WindowHours)).ToList(),
            tasks.Select(t => LabourPage.ToTaskRow(t.Id, InputOf(t), t.SortOrder)).ToList(),
            LabourPage.RegionPrefixes(regions),
            LabourPage.WindowGroups,
            LabourPage.PriorityGroups(priority),
            overrides.TryGetValue(Metrics.PaidBreakFactorKey, out var pct) ? pct : Metrics.DefaultPaidBreakFactorPct,
            LoadStart.LoadStartRulesFrom(overrides),
            resets.Select(r => new ResetTaskRow(r.Id, r.Name, r.SharePct, r.FixedMinutes,
                r.WindowStartKind, r.WindowStartTime is { } s ? Hm(s) : null, r.WindowStartOffsetMin,
                r.WindowEndKind, r.WindowEndTime is { } e ? Hm(e) : null, r.WindowEndOffsetMin,
                r.Placement, r.Active, r.SubTask)).ToList());
    }

    public async Task<SaveResult> AddWindowAsync(string labourGroup, int dayOfWeek, string windowStart, string windowEnd, double? windowHours = null, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (LabourRulesChecks.CheckQuota(windowHours) is { } error) return SaveResult.Fail(error);
        if ((TimeError(windowStart) ?? TimeError(windowEnd)) is { } timeError) return SaveResult.Fail(timeError);
        await using var db = contexts.Create();
        db.LabourGroupRules.Add(new LabourGroupRule
        {
            LabourGroup = labourGroup,
            DayOfWeek = dayOfWeek,
            WindowStart = ParseTime(windowStart)!.Value,
            WindowEnd = ParseTime(windowEnd)!.Value,
            WindowHours = windowHours,
        });
        return await SaveAsync(db, cancellationToken);
    }

    // Edit an existing window's day/times (and hours quota) in place.
    public async Task<SaveResult> UpdateWindowAsync(Guid id, int dayOfWeek, string windowStart, string windowEnd, double? windowHours = null, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (LabourRulesChecks.CheckWindowUpdate(windowStart, windowEnd, windowHours) is { } error) return SaveResult.Fail(error);
        if ((TimeError(windowStart) ?? TimeError(windowEnd)) is { } timeError) return SaveResult.Fail(timeError);
        await using var db = contexts.Create();
        var row = await db.LabourGroupRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null) return SaveResult.Success;
        row.DayOfWeek = dayOfWeek;
        row.WindowStart = ParseTime(windowStart)!.Value;
        row.WindowEnd = ParseTime(windowEnd)!.Value;
        row.WindowHours = windowHours;
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> DeleteWindowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        db.LabourGroupRules.RemoveRange(await db.LabourGroupRules.Where(r => r.Id == id).ToListAsync(cancellationToken));
        return await SaveAsync(db, cancellationToken);
    }

    // Remove every window for one labour group in a single action.
    public async Task<SaveResult> ClearWindowsAsync(string labourGroup, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        db.LabourGroupRules.RemoveRange(await db.LabourGroupRules.Where(r => r.LabourGroup == labourGroup).ToListAsync(cancellationToken));
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> AddAdminTaskAsync(AdminTaskInput input, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        var (row, error) = LabourRulesChecks.CleanTask(input);
        if (row is null) return SaveResult.Fail(error!);
        if ((TimeError(row.WindowStartTime) ?? TimeError(row.WindowEndTime)) is { } timeError) return SaveResult.Fail(timeError);
        await using var db = contexts.Create();
        var last = await db.AdminTasks.OrderByDescending(t => t.SortOrder).Select(t => (int?)t.SortOrder).FirstOrDefaultAsync(cancellationToken);
        var task = new AdminTask { SortOrder = (last ?? 0) + 10 };
        Apply(task, row);
        db.AdminTasks.Add(task);
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> UpdateAdminTaskAsync(Guid id, AdminTaskInput input, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        var (row, error) = LabourRulesChecks.CleanTask(input);
        if (row is null) return SaveResult.Fail(error!);
        if ((TimeError(row.WindowStartTime) ?? TimeError(row.WindowEndTime)) is { } timeError) return SaveResult.Fail(timeError);
        await using var db = contexts.Create();
        var task = await db.AdminTasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null) return SaveResult.Success;
        Apply(task, row);
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> DeleteAdminTaskAsync(Guid id, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        db.AdminTasks.RemoveRange(await db.AdminTasks.Where(t => t.Id == id).ToListAsync(cancellationToken));
        return await SaveAsync(db, cancellationToken);
    }

    // Persist the overlap / assignment priority order (index 0 = highest), one group at a time.
    public async Task<SaveResult> SavePriorityOrderAsync(IReadOnlyList<string> orderedGroups, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        for (var i = 0; i < orderedGroups.Count; i++)
        {
            var row = await db.LabourGroupPriority.FirstOrDefaultAsync(p => p.LabourGroup == orderedGroups[i], cancellationToken);
            if (row is null) db.LabourGroupPriority.Add(row = new LabourGroupPriority { LabourGroup = orderedGroups[i] });
            row.Priority = i;
            if (await SaveAsync(db, cancellationToken) is { Ok: false } failed) return failed;
        }
        return SaveResult.Success;
    }

    /// <summary>
    /// The paid-break allowance added to actual hours @100% before OE is measured. Stored
    /// as a rostering_rules override so the value has one home; the Dashboard reads it back
    /// at page load, so a change re-states history rather than only affecting days
    /// uploaded after it.
    /// </summary>
    public async Task<SaveResult> SaveBreakFactorAsync(double pct, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (LabourRulesChecks.CheckBreakFactor(pct) is { } error) return SaveResult.Fail(error);
        await using var db = contexts.Create();
        await UpsertRuleAsync(db, Metrics.PaidBreakFactorKey, pct, LabourRulesChecks.BreakFactorNote, cancellationToken);
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> SaveResetTaskAsync(Guid? id, ResetTaskInput input, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        var (record, error) = LabourRulesChecks.ResetTaskRecordFor(input);
        if (record is null) return SaveResult.Fail(error!);
        if ((TimeError(record.WindowStartTime) ?? TimeError(record.WindowEndTime)) is { } timeError) return SaveResult.Fail(timeError);
        await using var db = contexts.Create();
        ResetTask? task;
        if (id is { } existing)
        {
            task = await db.ResetTasks.FirstOrDefaultAsync(t => t.Id == existing, cancellationToken);
            if (task is null) return SaveResult.Success;
        }
        else db.ResetTasks.Add(task = new ResetTask());
        task.Name = record.Name;
        task.SharePct = record.SharePct;
        task.FixedMinutes = record.FixedMinutes;
        task.WindowStartKind = record.WindowStartKind;
        task.WindowStartTime = ParseTime(record.WindowStartTime);
        task.WindowStartOffsetMin = (int)record.WindowStartOffsetMin;
        task.WindowEndKind = record.WindowEndKind;
        task.WindowEndTime = ParseTime(record.WindowEndTime);
        task.WindowEndOffsetMin = (int)record.WindowEndOffsetMin;
        task.Placement = record.Placement;
        task.Active = record.Active;
        task.SubTask = record.SubTask;
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> DeleteResetTaskAsync(Guid id, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        db.ResetTasks.RemoveRange(await db.ResetTasks.Where(t => t.Id == id).ToListAsync(cancellationToken));
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> SaveLoadStartRulesAsync(LoadStartInput input, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (LabourRulesChecks.CheckLoadStart(input) is { } error) return SaveResult.Fail(error);
        await using var db = contexts.Create();
        foreach (var (key, value) in LabourRulesChecks.LoadStartRows(input))
            await UpsertRuleAsync(db, key, value, LabourRulesChecks.LoadStartNote, cancellationToken);
        return await SaveAsync(db, cancellationToken);
    }

    private async Task UpsertRuleAsync(RosterDbContext db, string key, double value, string note, CancellationToken cancellationToken)
    {
        var row = await db.RosteringRules.FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (row is null) db.RosteringRules.Add(row = new RosteringRule { Key = key });
        row.Value = value;
        row.Note = note;
        row.UpdatedAt = clock.GetUtcNow();
    }

    private static void Apply(AdminTask task, AdminTaskRecord row)
    {
        task.Name = row.Name;
        task.ScheduleType = row.ScheduleType;
        task.DayOfWeek = row.DayOfWeek;
        task.NthWeek = row.NthWeek;
        task.RegionDays = row.RegionDays is { } days
            ? JsonSerializer.Serialize(days.OrderBy(e => e.Key.Length).ThenBy(e => e.Key, StringComparer.Ordinal).ToDictionary(e => e.Key, e => e.Value))
            : null;
        task.DurationType = row.DurationType;
        task.FixedMinutes = row.FixedMinutes;
        task.MinutesPerUnit = row.MinutesPerUnit;
        task.MinMinutes = row.MinMinutes;
        task.WindowStartKind = row.WindowStartKind;
        task.WindowStartTime = ParseTime(row.WindowStartTime);
        task.WindowStartOffsetMin = (int)row.WindowStartOffsetMin;
        task.WindowEndKind = row.WindowEndKind;
        task.WindowEndTime = ParseTime(row.WindowEndTime);
        task.WindowEndOffsetMin = (int)row.WindowEndOffsetMin;
    }

    /// <summary>An admin_tasks row as the catalogue's form shows it.</summary>
    internal static AdminTaskInput InputOf(AdminTask t) => new()
    {
        Name = t.Name,
        ScheduleType = t.ScheduleType,
        DayOfWeek = t.DayOfWeek,
        NthWeek = t.NthWeek,
        RegionDays = RegionDaysOf(t.RegionDays),
        DurationType = t.DurationType,
        FixedMinutes = t.FixedMinutes,
        MinutesPerUnit = t.MinutesPerUnit,
        MinMinutes = t.MinMinutes,
        WindowStartKind = t.WindowStartKind,
        WindowStartTime = t.WindowStartTime is { } s ? Hm(s) : null,
        WindowStartOffsetMin = t.WindowStartOffsetMin,
        WindowEndKind = t.WindowEndKind,
        WindowEndTime = t.WindowEndTime is { } e ? Hm(e) : null,
        WindowEndOffsetMin = t.WindowEndOffsetMin,
    };

    // region_days: a JSON object of region prefix → weekday, or null.
    private static IReadOnlyDictionary<string, int>? RegionDaysOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var days = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
        return days?.ToDictionary(e => e.Key, e => (int)e.Value);
    }

    private static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"];

    private static TimeOnly? ParseTime(string? text) =>
        text is not null && TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    // What the database said of a time it could not read.
    private static string? TimeError(string? text) =>
        text is null || ParseTime(text) is not null ? null : $"invalid input syntax for type time: \"{text}\"";

    private static async Task<SaveResult> SaveAsync(RosterDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return SaveResult.Success;
        }
        catch (Exception ex) when (SuperUserGuard.IsWriteFailure(ex))
        {
            return SaveResult.Fail(SuperUserGuard.MessageOf(ex));
        }
    }
}
