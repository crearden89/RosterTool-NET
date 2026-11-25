// Port of the reads of src/lib/stage2/compute.ts (computeRosterFortnight,
// computeRoster). Building the fortnight from what was read is the engine's
// RosterCompute.ComputeRosterFortnight.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Data.Services.Rostering;

public interface IRosterLoader
{
    /// <summary>
    /// computeRosterFortnight: real employees assigned across the whole fortnight at once, so
    /// cross-day rules hold. <paramref name="heldPlan"/>: build from the store's HELD plan (the
    /// one its review approved) - its shifts are the slots to fill and its painted tiles the
    /// work. Without it each day's slots are solved afresh (the analysis pages). Request-cached.
    /// </summary>
    Task<FortnightRoster> ComputeRosterFortnightAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<HeldPlanDay>? heldPlan = null, CancellationToken ct = default);

    /// <summary>computeRoster: the fresh fortnight solve for the containing period, projected to one date.</summary>
    Task<RosterComputation> ComputeRosterAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}

// ─── Fortnight roster (the real Stage 2) ─────────────────────────────────────
// Bulk-reads all employee-level inputs (availability, leave, restricted tasks,
// fixed rosters, break rules, weekend-off markers and granted day-off
// requests) once, derives per-day demand from Stage 1, then runs the fortnight
// engine. Returns one RosterComputation per date.
public sealed class RosterLoader(IRosterDbContextFactory contexts, IRefData refData, IDaySlotsLoader daySlots) : IRosterLoader
{
    public Task<FortnightRoster> ComputeRosterFortnightAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<HeldPlanDay>? heldPlan = null, CancellationToken ct = default) =>
        RequestMemo.Cached("stage2.computeRosterFortnight", [storeId, periodStart, heldPlan], async () =>
            RosterCompute.ComputeRosterFortnight(await LoadFortnightSourcesAsync(storeId, periodStart, heldPlan, ct)));

    public async Task<RosterComputation> ComputeRosterAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var fortnight = await ComputeRosterFortnightAsync(storeId, Period.PeriodStartFor(date), null, ct);
        return fortnight.ByDate.TryGet(date, out var day) ? day : RosterCompute.Empty();
    }

    /// <summary>What computeRosterFortnight read, as the engine takes it.</summary>
    public async Task<FortnightSources> LoadFortnightSourcesAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<HeldPlanDay>? heldPlan, CancellationToken ct)
    {
        var dates = Period.PeriodDatesFrom(periodStart);
        var first = dates[0];
        var last = dates[13];
        var saturdays = dates.Where(d => Period.DowMon0(d) == 5).ToList();
        var state = await refData.GetStoreStateAsync(storeId, ct);
        var allEmps = await refData.GetEmployeesAsync(storeId, ct);
        // Only the active team's rows are read (the engine rosters the active team).
        var inEmp = allEmps.Where(e => e.Active).Select(e => e.Id).ToList();

        await using var db = contexts.Create();
        var avail = await db.EmployeeAvailability.AsNoTracking().Where(a => inEmp.Contains(a.EmployeeId))
            .OrderBy(a => a.EmployeeId).ThenBy(a => a.DayOfWeek).ToListAsync(ct);
        var leave = await db.EmployeeLeave.AsNoTracking()
            .Where(l => inEmp.Contains(l.EmployeeId) && l.StartDate <= last && l.EndDate >= first)
            .OrderBy(l => l.StartDate).ThenBy(l => l.Id).ToListAsync(ct);
        var restr = await db.EmployeeRestrictedTasks.AsNoTracking().Where(r => inEmp.Contains(r.EmployeeId))
            .OrderBy(r => r.EmployeeId).ThenBy(r => r.LabourGroup).ToListAsync(ct);
        var fixedRows = await db.FixedRosters.AsNoTracking().Where(f => inEmp.Contains(f.EmployeeId))
            .OrderBy(f => f.EmployeeId).ThenBy(f => f.WeekIndex).ThenBy(f => f.DayOfWeek).ToListAsync(ct);
        var cycles = await db.FixedRosterCycles.AsNoTracking().Where(c => inEmp.Contains(c.EmployeeId)).ToListAsync(ct);
        var markers = saturdays.Count > 0
            ? await db.WeekendMarkers.AsNoTracking().Where(m => inEmp.Contains(m.EmployeeId) && saturdays.Contains(m.WeekendStart)).ToListAsync(ct)
            : [];
        var dayOffs = await db.DayOffRequests.AsNoTracking()
            .Where(d => inEmp.Contains(d.EmployeeId) && d.Status == DayOffStatuses.Granted && d.Date >= first && d.Date <= last)
            .OrderBy(d => d.Date).ThenBy(d => d.Id).ToListAsync(ct);
        // Manually-locked shifts are immovable anchors (spec §6.6).
        var locked = await db.GeneratedShifts.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.Locked && s.Date >= first && s.Date <= last)
            .OrderBy(s => s.Date).ThenBy(s => s.StartTime).ThenBy(s => s.Id).ToListAsync(ct);

        // The day's PLAN SLOTS (shared with the Task view) - the roster fills
        // these; it never re-derives shift shapes.
        var slots = new List<DaySlots>();
        foreach (var d in dates)
        {
            var held = heldPlan?.FirstOrDefault(h => h.Date == d);
            slots.Add(held is not null ? HeldPlan.SlotsFromHeldPlan(held) : await daySlots.ComputeDaySlotsAsync(storeId, d, ct));
        }

        static string Id(Guid g) => g.ToString("D");
        return new FortnightSources
        {
            PeriodStart = periodStart,
            Employees = allEmps.Select(e => new FortnightEmployeeRow
            {
                Id = Id(e.Id), Name = e.Name, Role = e.Role, ContractHours = e.ContractHours, NotionalHours = e.NotionalHours, Active = e.Active,
            }).ToList(),
            Availability = avail.Select(a => new AvailabilityRow
            {
                EmployeeId = Id(a.EmployeeId), DayOfWeek = a.DayOfWeek, Available = a.Available, FromTime = a.FromTime, ToTime = a.ToTime,
            }).ToList(),
            Leave = leave.Select(l => new LeaveRow { EmployeeId = Id(l.EmployeeId), StartDate = l.StartDate, EndDate = l.EndDate, DayHours = l.DayHours }).ToList(),
            Restricted = restr.Select(r => new RestrictedTaskRow { EmployeeId = Id(r.EmployeeId), LabourGroup = r.LabourGroup }).ToList(),
            FixedRosters = fixedRows.Select(f => new FixedRosterRow
            {
                EmployeeId = Id(f.EmployeeId), WeekIndex = f.WeekIndex, DayOfWeek = f.DayOfWeek, StartTime = f.StartTime, EndTime = f.EndTime,
            }).ToList(),
            FixedCycles = cycles.Select(c => new CycleRow(Id(c.EmployeeId), c.CycleWeeks, c.StartDate)).ToList(),
            BreakRules = await refData.GetBreakRulesAsync(ct),
            WeekendMarkers = markers.Select(m => new WeekendMarkerRow { EmployeeId = Id(m.EmployeeId), WeekendStart = m.WeekendStart, SatOff = m.SatOff, SunOff = m.SunOff }).ToList(),
            DayOffs = dayOffs.Select(d => new DayOffRow { EmployeeId = Id(d.EmployeeId), Date = d.Date }).ToList(),
            LockedShifts = locked.Select(s => new LockedShiftRow { EmployeeId = Id(s.EmployeeId), Date = s.Date, StartTime = s.StartTime, EndTime = s.EndTime }).ToList(),
            SchoolCalendar = await refData.GetSchoolCalendarAsync(state, ct),
            TaskPriority = await refData.GetTaskPriorityAsync(ct),
            Holidays = (await refData.GetHolidaySetAsync(storeId, ct)).ToList(),
            RuleOverrides = await refData.GetRuleOverridesAsync(ct),
            RosterOrder = await refData.GetRosterOrderAsync(ct),
            DaySlots = slots,
            HeldPlan = heldPlan,
        };
    }
}
