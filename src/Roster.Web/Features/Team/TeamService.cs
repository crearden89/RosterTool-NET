// Port of app/(app)/setup/staff/page.tsx (LoadAsync) and of the server actions in
// setup/staff/actions.ts and setup/requests/actions.ts (day-off requests and fixed
// rosters, managed on Team Members). Every write is guarded first by the same
// store-scope check, in the same place, as the TypeScript action; the database then
// checks it again (D's write guard). Look-ups the TypeScript app made with its
// service-role client before a guard read past the row filters here too, so a row in
// another store is refused as out of scope rather than reported missing.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Web.Components.Ui;
using CoreRoles = Roster.Engine.Core.Roles;

namespace Roster.Web.Features.Team;

public sealed class TeamService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IRememberedStore remembered,
    IRefData refData,
    TimeProvider clock) : ITeamService
{
    public async Task<TeamPageData> LoadAsync(Guid? requestedStore, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var user = scope.User;
        await using var db = contexts.Create();
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new StoreOption(s.Id, s.Code, s.Name)).ToListAsync(ct);
        var storeId = user is { Tier: Tier.Store }
            ? user.StoreId
            : StoreScope.ResolveStoreId(requestedStore, stores.Select(s => s.Id).ToList(), remembered.StoreId);

        var sid = storeId ?? Guid.Empty;
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.StoreId == sid && e.Active)
            .Select(e => new TeamMember
            {
                Id = e.Id,
                Name = e.Name,
                Role = e.Role,
                PositionCode = e.PositionCode,
                ContractHours = e.ContractHours,
                NotionalHours = e.NotionalHours,
                Notes = e.Notes,
                SmYearBand = e.SmYearBand,
                StartDate = e.StartDate,
            })
            .ToListAsync(ct);
        var positions = (await refData.GetPositionsAsync(ct))
            .Select(p => new PositionOption(p.Code, p.Label, p.BaseRole, p.WeeklyHours, p.YearBand is { } y ? (int)y : null))
            .ToList();

        // Scoped to THIS store's (active) team, as the TypeScript page filters on its ids.
        var restricted = await db.EmployeeRestrictedTasks.AsNoTracking()
            .Where(r => r.Employee!.StoreId == sid && r.Employee.Active)
            .Select(r => new RestrictedTask(r.EmployeeId, r.LabourGroup)).ToListAsync(ct);
        var leave = await db.EmployeeLeave.AsNoTracking()
            .Where(l => l.Employee!.StoreId == sid && l.Employee.Active)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new LeaveBooking(l.Id, l.EmployeeId, l.StartDate, l.EndDate, l.LeaveType, l.DayHours)).ToListAsync(ct);
        var availability = await db.EmployeeAvailability.AsNoTracking()
            .Where(a => a.Employee!.StoreId == sid && a.Employee.Active)
            .Select(a => new AvailabilityRow(a.EmployeeId, a.DayOfWeek, a.Available, a.FromTime, a.ToTime)).ToListAsync(ct);
        // Every locked shift and cycle: the fixed-roster sheet edits them and the warning prices them.
        var fixedShifts = await db.FixedRosters.AsNoTracking()
            .Where(f => f.Employee!.StoreId == sid && f.Employee.Active)
            .OrderBy(f => f.WeekIndex).ThenBy(f => f.DayOfWeek)
            .Select(f => new FixedShift(f.Id, f.EmployeeId, f.WeekIndex, f.DayOfWeek, f.StartTime, f.EndTime)).ToListAsync(ct);
        var cycles = await db.FixedRosterCycles.AsNoTracking()
            .Where(c => c.Employee!.StoreId == sid && c.Employee.Active)
            .Select(c => new FixedRosterCycleRow(c.EmployeeId, c.CycleWeeks, c.StartDate)).ToListAsync(ct);
        // The team's day-off requests, every status. Newest first from the database, then
        // turned round: the page shows them oldest first (first in, first served).
        var requests = await db.DayOffRequests.AsNoTracking()
            .Where(r => r.Employee!.StoreId == sid && r.Employee.Active)
            .OrderByDescending(r => r.SubmittedAt).ThenByDescending(r => r.Id)
            .Select(r => new DayOffRequestRow(r.Id, r.EmployeeId, r.Date, r.SubmittedAt, r.Status, r.Notes)).ToListAsync(ct);
        requests.Reverse();

        return new TeamPageData
        {
            StoreId = storeId,
            Stores = user is { Tier: Tier.Store } ? [] : stores,
            Employees = TeamRules.SortTeam(employees),
            Positions = positions,
            Restricted = Order(restricted),
            Leave = leave,
            Availability = availability,
            Fixed = fixedShifts,
            Cycles = cycles,
            FixedPaidHours = TeamRules.FixedPaidHours(employees, fixedShifts, cycles),
            Requests = requests,
            ReadOnly = user is null || AccessMatrix.IsReadOnlyTier(user.Tier),
        };
    }

    // The restricted groups in the editor's order (the database keeps no insertion order).
    private static List<RestrictedTask> Order(List<RestrictedTask> rows) =>
        rows.OrderBy(r => r.EmployeeId).ThenBy(r => IndexOf(r.LabourGroup)).ToList();

    private static int IndexOf(string group)
    {
        var i = TeamRules.Restrictable.ToList().IndexOf(group);
        return i < 0 ? int.MaxValue : i;
    }

    // ─── setup/staff/actions.ts ──────────────────────────────────────────────

    public async Task<SaveResult> AddEmployeeAsync(Guid? storeId, string name, string positionCode, double contractHours, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        // The store the current user may write to (store tier → own; others → the argument).
        if (scope.User is not { } user) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var store = user.Tier == Tier.Store ? user.StoreId : storeId;
        if (store is null) return SaveResult.Fail("No store");
        var denied = await scope.DenyStoreWriteAsync(store, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        var pos = await db.Positions.AsNoTracking().FirstOrDefaultAsync(p => p.Code == positionCode, ct);
        if (pos is null) return SaveResult.Fail($"Unknown position '{positionCode}'");
        var role = pos.BaseRole;
        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            StoreId = store.Value,
            Name = name,
            Role = role,
            PositionCode = pos.Code,
            // JSAs are casual and carry no contract size.
            ContractHours = role == CoreRoles.JSA ? 0 : contractHours,
            SmYearBand = pos.YearBand,
        };
        db.Employees.Add(employee);
        // Default availability: available every day.
        for (var d = 0; d < 7; d++)
            db.EmployeeAvailability.Add(new EmployeeAvailability { EmployeeId = employee.Id, DayOfWeek = d, Available = true });
        return await SaveAsync(db, ct);
    }

    public async Task<SaveResult> SetAvailabilityAsync(Guid employeeId, IReadOnlyList<AvailabilityDay> days, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        var existing = await db.EmployeeAvailability.Where(a => a.EmployeeId == employeeId).ToListAsync(ct);
        foreach (var d in days)
        {
            var row = existing.FirstOrDefault(a => a.DayOfWeek == d.DayOfWeek);
            if (row is null)
            {
                row = new EmployeeAvailability { EmployeeId = employeeId, DayOfWeek = d.DayOfWeek };
                db.EmployeeAvailability.Add(row);
                existing.Add(row);
            }
            row.Available = d.Available;
            // Windows are cleared when a day is marked unavailable.
            row.FromTime = d.Available ? d.FromTime : null;
            row.ToTime = d.Available ? d.ToTime : null;
        }
        return await SaveAsync(db, ct);
    }

    public async Task<SaveResult> DeleteEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        try
        {
            var refused = await db.DeleteEmployeeAsync(employeeId, ct);
            return refused is null ? SaveResult.Success : SaveResult.Fail(refused);
        }
        catch (Exception e) when (e is DbUpdateException or ScopeViolationException)
        {
            return SaveResult.Fail(Message(e));
        }
    }

    public async Task<SaveResult> SetRestrictedTasksAsync(Guid employeeId, IReadOnlyList<string> groups, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        db.EmployeeRestrictedTasks.RemoveRange(await db.EmployeeRestrictedTasks.Where(r => r.EmployeeId == employeeId).ToListAsync(ct));
        await SaveAsync(db, ct);
        if (groups.Count == 0) return SaveResult.Success;
        foreach (var g in groups) db.EmployeeRestrictedTasks.Add(new EmployeeRestrictedTask { EmployeeId = employeeId, LabourGroup = g });
        return await SaveAsync(db, ct);
    }

    public async Task<SaveResult> AddLeaveAsync(Guid employeeId, DateOnly start, DateOnly end, string leaveType = "ANN", double? dayHours = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        var refused = TeamRules.LeaveRefusal(leaveType, dayHours);
        if (refused is not null) return SaveResult.Fail(refused);
        await using var db = contexts.Create();
        db.EmployeeLeave.Add(new EmployeeLeave
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            StartDate = start,
            EndDate = end,
            LeaveType = leaveType,
            DayHours = dayHours,
        });
        return await SaveAsync(db, ct);
    }

    public async Task<SaveResult> SetContractAndNotionalAsync(Guid employeeId, double contractHours, double? notionalHours, DateOnly? startDate = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        var refused = TeamRules.ContractRefusal(contractHours, notionalHours);
        if (refused is not null) return SaveResult.Fail(refused);
        await using var db = contexts.Create();
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null) return SaveResult.Success;
        employee.ContractHours = contractHours;
        employee.NotionalHours = notionalHours;
        // Null clears the start date, which means "experienced".
        employee.StartDate = startDate;
        return await SaveAsync(db, ct);
    }

    public async Task<SaveResult> ClearLeaveAsync(Guid leaveId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        await using var db = contexts.Create();
        // Row-id keyed: resolve the owning employee so the delete is scope-checked.
        var owner = await db.EmployeeLeave.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == leaveId).Select(l => (Guid?)l.EmployeeId).FirstOrDefaultAsync(ct);
        var denied = await scope.DenyEmployeeWriteAsync(owner ?? Guid.Empty, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        db.EmployeeLeave.RemoveRange(await db.EmployeeLeave.Where(l => l.Id == leaveId).ToListAsync(ct));
        return await SaveAsync(db, ct);
    }

    public async Task<SaveResult> SetPositionAsync(Guid employeeId, string positionCode, double? contractHours = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        var pos = await db.Positions.AsNoTracking().FirstOrDefaultAsync(p => p.Code == positionCode, ct);
        if (pos is null) return SaveResult.Fail($"Unknown position '{positionCode}'");
        var role = pos.BaseRole;
        double? contract = null;
        if (role == CoreRoles.JSA) contract = 0;
        else if (contractHours is { } ch)
        {
            if (!(ch >= 0 && ch <= 76)) return SaveResult.Fail("Contract hours must be between 0 and 76");
            contract = ch;
        }
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null) return SaveResult.Success;
        employee.PositionCode = pos.Code;
        employee.Role = role;
        employee.SmYearBand = pos.YearBand;
        if (contract is { } c) employee.ContractHours = c;
        return await SaveAsync(db, ct);
    }

    // ─── setup/requests/actions.ts ───────────────────────────────────────────

    public async Task<SaveResult> AddDayOffAsync(Guid employeeId, DateOnly date, DateTimeOffset? submittedAt, string notes, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        db.DayOffRequests.Add(new DayOffRequest
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Date = date,
            SubmittedAt = submittedAt ?? clock.GetUtcNow(),
            Notes = notes,
            Status = DayOffStatuses.Pending,
        });
        return await SaveAsync(db, ct);
    }

    // Grant EVERY pending request for a store's team in one go: a mid-way failure must
    // not leave half-granted state with no report.
    public async Task<SaveResult> GrantAllPendingAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var denied = await scope.DenyStoreWriteAsync(storeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        // The store's whole team, active or not.
        var empIds = await db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.StoreId == storeId).Select(e => e.Id).ToListAsync(ct);
        if (empIds.Count == 0) return SaveResult.Success;
        var pending = await db.DayOffRequests
            .Where(r => r.Status == DayOffStatuses.Pending && empIds.Contains(r.EmployeeId)).ToListAsync(ct);
        foreach (var r in pending) r.Status = DayOffStatuses.Granted;
        var saved = await SaveAsync(db, ct);
        if (!saved.Ok) return saved;
        // Weekend day-offs still flip their weekend-off markers.
        foreach (var r in pending) await SyncWeekendMarkerAsync(db, r.EmployeeId, r.Date, granted: true, ct);
        var synced = await SaveAsync(db, ct);
        return synced.Ok ? synced : SaveResult.Fail($"Granted, but a weekend-off switch failed to update: {synced.Error}");
    }

    public async Task<SaveResult> SetRequestStatusAsync(Guid requestId, string status, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        await using var db = contexts.Create();
        // Resolve the owning employee BEFORE the update so the write is scope-checked.
        var owner = await db.DayOffRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Id == requestId).Select(r => (Guid?)r.EmployeeId).FirstOrDefaultAsync(ct);
        var denied = await scope.DenyEmployeeWriteAsync(owner ?? Guid.Empty, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        var req = await db.DayOffRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (req is null) return SaveResult.Success;
        req.Status = status;
        var saved = await SaveAsync(db, ct);
        if (!saved.Ok) return saved;
        // Keep the weekend-off switch in step with a weekend day-off's status.
        await SyncWeekendMarkerAsync(db, req.EmployeeId, req.Date, status == DayOffStatuses.Granted, ct);
        var synced = await SaveAsync(db, ct);
        return synced.Ok ? synced : SaveResult.Fail($"Status saved, but the weekend-off switch failed to update: {synced.Error}");
    }

    public async Task<SaveResult> DeleteRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        await using var db = contexts.Create();
        var owner = await db.DayOffRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Id == requestId).Select(r => (Guid?)r.EmployeeId).FirstOrDefaultAsync(ct);
        var denied = await scope.DenyEmployeeWriteAsync(owner ?? Guid.Empty, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        db.DayOffRequests.RemoveRange(await db.DayOffRequests.Where(r => r.Id == requestId).ToListAsync(ct));
        // As the TypeScript action: the delete's own outcome is not reported.
        await SaveAsync(db, ct);
        return SaveResult.Success;
    }

    // Save an employee's whole fixed-roster cycle in a single action: 1, 2 or 4 weekly
    // templates plus the start date that anchors Week 1. Days present in `days` are
    // written; everything else is cleared, so an emptied day removes that locked shift.
    // An empty `days` removes the fixed roster entirely, cycle definition included.
    public async Task<SaveResult> SetFixedRosterCycleAsync(Guid employeeId, int cycleWeeks, DateOnly? startDate, IReadOnlyList<FixedDay> days, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        var invalid = TeamRules.CycleRefusal(cycleWeeks, startDate, days);
        if (invalid is not null) return SaveResult.Fail(invalid);
        await using var db = contexts.Create();
        // Checked when entered (owner decision R6): a fixed shift that breaks a shift rule
        // is refused here, rather than trimmed by the plan later.
        var refused = await FixedShiftRefusalAsync(db, employeeId, days, ct);
        if (refused is not null) return SaveResult.Fail(refused);
        // Anchor on the Monday of whatever date was entered - Week 1 is a week, not a day.
        var anchor = FixedCycle.MondayOf(startDate!.Value);

        db.FixedRosters.RemoveRange(await db.FixedRosters.Where(f => f.EmployeeId == employeeId).ToListAsync(ct));
        await SaveAsync(db, ct);
        var cycle = await db.FixedRosterCycles.FirstOrDefaultAsync(c => c.EmployeeId == employeeId, ct);
        if (days.Count == 0)
        {
            if (cycle is not null) db.FixedRosterCycles.Remove(cycle);
            await SaveAsync(db, ct);
            return SaveResult.Success;
        }
        if (cycle is null)
        {
            cycle = new FixedRosterCycle { EmployeeId = employeeId };
            db.FixedRosterCycles.Add(cycle);
        }
        cycle.CycleWeeks = cycleWeeks;
        cycle.StartDate = anchor;
        cycle.UpdatedAt = clock.GetUtcNow();
        var savedCycle = await SaveAsync(db, ct);
        if (!savedCycle.Ok) return savedCycle;
        foreach (var d in days)
        {
            db.FixedRosters.Add(new FixedRoster
            {
                Id = Guid.NewGuid(),
                EmployeeId = employeeId,
                WeekIndex = d.WeekIndex,
                DayOfWeek = d.DayOfWeek,
                StartTime = d.Start,
                EndTime = d.End,
            });
        }
        return await SaveAsync(db, ct);
    }

    // Remove an employee's fixed roster outright: every week's rows plus the cycle
    // definition, so nothing stale anchors a future edit.
    public async Task<SaveResult> ClearFixedRosterAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        db.FixedRosters.RemoveRange(await db.FixedRosters.Where(f => f.EmployeeId == employeeId).ToListAsync(ct));
        db.FixedRosterCycles.RemoveRange(await db.FixedRosterCycles.Where(c => c.EmployeeId == employeeId).ToListAsync(ct));
        await SaveAsync(db, ct);
        return SaveResult.Success;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Why a fixed roster can't be saved (fixedShiftRefusal): the role's break rule, the
    // store's trading week and the close-down, applied by TeamRules.FixedShiftRefusal.
    private async Task<string?> FixedShiftRefusalAsync(RosterDbContext db, Guid employeeId, IReadOnlyList<FixedDay> days, CancellationToken ct)
    {
        var emp = await db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == employeeId).Select(e => new { e.Role, e.StoreId }).FirstOrDefaultAsync(ct);
        if (emp is null) return null;
        var rules = await refData.GetBreakRulesAsync(ct);
        var week = await refData.GetTradingWeekAsync(emp.StoreId, ct);
        var overrides = await refData.GetRuleOverridesAsync(ct);
        return TeamRules.FixedShiftRefusal(emp.Role, days, rules.GetValueOrDefault(emp.Role), week, overrides);
    }

    // syncWeekendMarker: a granted Saturday or Sunday switches on that day's weekend-off
    // flag (the engine reads weekend_markers, not the requests); un-granting clears just
    // that day's flag. Tracked, so two days of one weekend in one save both land.
    private static async Task SyncWeekendMarkerAsync(RosterDbContext db, Guid employeeId, DateOnly date, bool granted, CancellationToken ct)
    {
        if (TeamRules.WeekendStartOf(date) is not { } saturday) return; // weekday - no weekend marker to touch
        var marker = db.WeekendMarkers.Local.FirstOrDefault(m => m.EmployeeId == employeeId && m.WeekendStart == saturday)
            ?? await db.WeekendMarkers.FirstOrDefaultAsync(m => m.EmployeeId == employeeId && m.WeekendStart == saturday, ct);
        var (sat, sun) = TeamRules.MergeWeekendMarker(date, granted, marker?.SatOff, marker?.SunOff);
        if (marker is null)
        {
            marker = new WeekendMarker { EmployeeId = employeeId, WeekendStart = saturday };
            db.WeekendMarkers.Add(marker);
        }
        marker.SatOff = sat;
        marker.SunOff = sun;
    }

    /// <summary>Saves, returning the database's refusal as the action's message.</summary>
    internal static async Task<SaveResult> SaveAsync(RosterDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return SaveResult.Success;
        }
        catch (Exception e) when (e is DbUpdateException or ScopeViolationException)
        {
            db.ChangeTracker.Clear();
            return SaveResult.Fail(Message(e));
        }
    }

    private static string Message(Exception e) => e.InnerException?.Message ?? e.Message;
}
