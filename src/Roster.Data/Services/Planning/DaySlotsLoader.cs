// Port of the reads of src/lib/stage2/planSlots.ts: fixedShiftsFor,
// dayCheckOptions, teamHasTieredManager and computeDaySlots. The shaping,
// trial painting and checking are the engine's PlanSlots.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Cache;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Data.Services.Planning;

// What the day checker holds a store's plan day to: its floor-cover rules, each
// slot type's longest legal shift - the same figures the shaper cuts to - and
// whether it is a school day (a junior's 5-hour cap and 16:00 start, R10).
public sealed class DayCheckOptionsLoader(IRefData refData) : IDayCheckOptionsLoader
{
    public async Task<DayCheckOptions> DayCheckOptionsAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var rules = await refData.GetBreakRulesAsync(ct);
        var schoolDay = await refData.IsSchoolDayForAsync(storeId, date, ct);
        var ruleOverrides = await refData.GetRuleOverridesAsync(ct);
        return PlanSlots.DayCheckOptions(rules, schoolDay, ruleOverrides);
    }
}

// The day's PLAN SLOTS - the single source of shift structure for BOTH the Task
// view and the fortnight roster (the roster fills these slots with real names;
// it never re-derives shapes). Cached: one shape per (store, date) per request,
// so the two surfaces can never disagree.
public sealed class DaySlotsLoader(IRosterDbContextFactory contexts, IRefData refData, IStage1Loader stage1) : IDaySlotsLoader
{
    public Task<DaySlots> ComputeDaySlotsAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("planSlots.computeDaySlots", [storeId, date], async () =>
        {
            var s1 = await stage1.ComputeStage1Async(storeId, date, ct);
            if (!s1.HasData) return new DaySlots(null, s1);
            var rules = await refData.GetBreakRulesAsync(ct);
            var schoolDay = await refData.IsSchoolDayForAsync(storeId, date, ct);
            var fixedShifts = await FixedShiftsForAsync(storeId, date, ct);
            var ruleOverrides = await refData.GetRuleOverridesAsync(ct);
            var priority = await refData.GetTaskPriorityAsync(ct);
            // Whether the store's active team has a manager owed a store assistant's
            // tiered breaks (an RSM), who the roster may put in a manager slot.
            var team = await refData.GetEmployeesAsync(storeId, ct);
            return PlanSlots.ComputeDaySlots(new DaySlotsSources
            {
                Stage1 = s1,
                BreakRules = rules,
                IsSchoolDay = schoolDay,
                FixedShifts = fixedShifts,
                RuleOverrides = ruleOverrides,
                Priority = priority,
                TeamHasTieredManager = PlanSlots.HasTieredManager(team.Select(e => (e.Role, e.Active))),
            });
        });

    // The fixed-roster shifts that will ACTUALLY be worked on `date`: a standing
    // day-of-week arrangement, minus anyone the day removes (approved leave, a
    // weekend-off marker, a granted day-off). The plan reserves these so the rest
    // of the curve is cut around commitments the roster has no choice but to
    // honour - see ShaperInput.FixedShifts.
    internal async Task<IReadOnlyList<FixedShift>> FixedShiftsForAsync(Guid storeId, DateOnly date, CancellationToken ct)
    {
        var dow = Period.DowMon0(date);
        await using var db = contexts.Create();
        var allRows = await db.FixedRosters.AsNoTracking()
            .Where(f => f.DayOfWeek == dow && f.Employee!.StoreId == storeId && f.Employee.Active)
            .OrderBy(f => f.Employee!.CreatedAt).ThenBy(f => f.EmployeeId).ThenBy(f => f.WeekIndex)
            .Select(f => new { f.EmployeeId, f.WeekIndex, f.StartTime, f.EndTime, f.Employee!.Role })
            .ToListAsync(ct);
        if (allRows.Count == 0) return [];

        // Keep only the row belonging to the cycle week `date` falls in - a 2/4-week
        // cycle stores this day-of-week once per template week.
        var empIds = allRows.Select(r => r.EmployeeId).Distinct().ToList();
        var cycles = await db.FixedRosterCycles.AsNoTracking().Where(c => empIds.Contains(c.EmployeeId))
            .Select(c => new { c.EmployeeId, c.CycleWeeks, c.StartDate }).ToListAsync(ct);
        var cycleWeekFor = FixedCycle.MakeCycleResolver(cycles.Select(c => new CycleRow(c.EmployeeId.ToString("D"), c.CycleWeeks, c.StartDate)));
        var rows = allRows.Where(r => r.WeekIndex == cycleWeekFor(r.EmployeeId.ToString("D"), date)).ToList();
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.EmployeeId).ToList();
        // Saturday anchors its own weekend row; Sunday reads the Saturday before it.
        var weekendStart = dow == 6 ? Period.AddDaysIso(date, -1) : date;
        var leave = await db.EmployeeLeave.AsNoTracking()
            .Where(l => ids.Contains(l.EmployeeId) && l.StartDate <= date && l.EndDate >= date)
            .Select(l => l.EmployeeId).ToListAsync(ct);
        var markers = dow >= 5
            ? await db.WeekendMarkers.AsNoTracking().Where(m => ids.Contains(m.EmployeeId) && m.WeekendStart == weekendStart)
                .Select(m => new { m.EmployeeId, m.SatOff, m.SunOff }).ToListAsync(ct)
            : [];
        var dayOffs = await db.DayOffRequests.AsNoTracking()
            .Where(d => ids.Contains(d.EmployeeId) && d.Status == DayOffStatuses.Granted && d.Date == date)
            .Select(d => d.EmployeeId).ToListAsync(ct);

        var away = new HashSet<Guid>(leave);
        away.UnionWith(dayOffs);
        away.UnionWith(markers.Where(m => dow == 5 ? m.SatOff : m.SunOff).Select(m => m.EmployeeId));
        return rows.Where(r => !away.Contains(r.EmployeeId))
            .Select(r => new FixedShift(RefDataReader.Min(r.StartTime), RefDataReader.Min(r.EndTime), PlanSlots.SlotRoleForEmployee(r.Role)))
            .ToList();
    }
}
