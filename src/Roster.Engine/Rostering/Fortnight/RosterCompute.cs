// Port of src/lib/stage2/compute.ts (its pure part: everything after the reads).
// ─── Fortnight roster (the real Stage 2) ─────────────────────────────────────
// Assigns real employees across a whole 14-day period at once so cross-day rules
// can be honoured. The data layer bulk-reads all employee-level inputs
// (availability, leave, restricted tasks, fixed rosters, break rules, weekend-off
// markers and granted day-off requests) once, with each day's plan slots, and
// hands them over as FortnightSources; this derives each employee's day, runs
// the fortnight engine and returns one RosterComputation per date.

using System.Text.Json.Serialization;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

/// <summary>An employees row (getEmployees): every row, active or not - the compute keeps the active ones.</summary>
public sealed record FortnightEmployeeRow
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = Roles.SA;
    [JsonPropertyName("contract_hours")] public double ContractHours { get; init; }
    [JsonPropertyName("notional_hours")] public double? NotionalHours { get; init; }
    [JsonPropertyName("active")] public bool Active { get; init; } = true;
}

/// <summary>employee_availability: one weekday's standing availability.</summary>
public sealed record AvailabilityRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    /// <summary>Monday = 0.</summary>
    [JsonPropertyName("day_of_week")] public int DayOfWeek { get; init; }
    [JsonPropertyName("available")] public bool Available { get; init; } = true;
    [JsonPropertyName("from_time")] public TimeOnly? FromTime { get; init; }
    [JsonPropertyName("to_time")] public TimeOnly? ToTime { get; init; }
}

/// <summary>employee_leave overlapping the period.</summary>
public sealed record LeaveRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    [JsonPropertyName("start_date")] public DateOnly StartDate { get; init; }
    [JsonPropertyName("end_date")] public DateOnly EndDate { get; init; }
    /// <summary>Hours credited per leave weekday; null = a full contract day.</summary>
    [JsonPropertyName("day_hours")] public double? DayHours { get; init; }
}

/// <summary>employee_restricted_tasks: a task group the employee may not do.</summary>
public sealed record RestrictedTaskRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    [JsonPropertyName("labour_group")] public string LabourGroup { get; init; } = "";
}

/// <summary>fixed_rosters: one weekday of one week of an employee's fixed-roster cycle.</summary>
public sealed record FixedRosterRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    [JsonPropertyName("week_index")] public int WeekIndex { get; init; }
    /// <summary>Monday = 0.</summary>
    [JsonPropertyName("day_of_week")] public int DayOfWeek { get; init; }
    [JsonPropertyName("start_time")] public TimeOnly StartTime { get; init; }
    [JsonPropertyName("end_time")] public TimeOnly EndTime { get; init; }
}

/// <summary>weekend_markers for the period's Saturdays.</summary>
public sealed record WeekendMarkerRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    /// <summary>The weekend's Saturday.</summary>
    [JsonPropertyName("weekend_start")] public DateOnly WeekendStart { get; init; }
    [JsonPropertyName("sat_off")] public bool SatOff { get; init; }
    [JsonPropertyName("sun_off")] public bool SunOff { get; init; }
}

/// <summary>A GRANTED day_off_requests row in the period.</summary>
public sealed record DayOffRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    [JsonPropertyName("date")] public DateOnly Date { get; init; }
}

/// <summary>A locked generated_shifts row in the period (a manual pin).</summary>
public sealed record LockedShiftRow
{
    [JsonPropertyName("employee_id")] public string EmployeeId { get; init; } = "";
    [JsonPropertyName("date")] public DateOnly Date { get; init; }
    [JsonPropertyName("start_time")] public TimeOnly StartTime { get; init; }
    [JsonPropertyName("end_time")] public TimeOnly EndTime { get; init; }
}

/// <summary>
/// Everything computeRosterFortnight reads, already read: the data layer fills
/// this (one property per read, in the TypeScript's order) and calls
/// <see cref="RosterCompute.ComputeRosterFortnight"/>.
/// </summary>
public sealed record FortnightSources
{
    /// <summary>The period's Monday; the fortnight is its 14 dates.</summary>
    public DateOnly PeriodStart { get; init; }
    /// <summary>getEmployees(store): every row; inactive ones are left out here.</summary>
    public IReadOnlyList<FortnightEmployeeRow> Employees { get; init; } = [];
    public IReadOnlyList<AvailabilityRow> Availability { get; init; } = [];
    /// <summary>Leave overlapping the period.</summary>
    public IReadOnlyList<LeaveRow> Leave { get; init; } = [];
    public IReadOnlyList<RestrictedTaskRow> Restricted { get; init; } = [];
    public IReadOnlyList<FixedRosterRow> FixedRosters { get; init; } = [];
    public IReadOnlyList<CycleRow> FixedCycles { get; init; } = [];
    /// <summary>getBreakRules(): one per role.</summary>
    public IReadOnlyDictionary<string, BreakRule> BreakRules { get; init; } = new Dictionary<string, BreakRule>();
    /// <summary>The markers of the period's Saturdays.</summary>
    public IReadOnlyList<WeekendMarkerRow> WeekendMarkers { get; init; } = [];
    /// <summary>Granted day-off requests in the period.</summary>
    public IReadOnlyList<DayOffRow> DayOffs { get; init; } = [];
    /// <summary>The store's locked generated shifts in the period.</summary>
    public IReadOnlyList<LockedShiftRow> LockedShifts { get; init; } = [];
    /// <summary>getSchoolCalendar(state).</summary>
    public SchoolCalendar SchoolCalendar { get; init; } = SchoolCalendar.None;
    /// <summary>getTaskPriority().</summary>
    public IReadOnlyList<string> TaskPriority { get; init; } = TaskGroups.TaskPriority;
    /// <summary>getHolidaySet(store): the store's public holidays.</summary>
    public IReadOnlyCollection<DateOnly> Holidays { get; init; } = [];
    /// <summary>getRuleOverrides(): the rostering_rules overrides.</summary>
    public IReadOnlyDictionary<string, double>? RuleOverrides { get; init; }
    /// <summary>getRosterOrder(): null = the default order.</summary>
    public IReadOnlyList<OrderStep>? RosterOrder { get; init; }
    /// <summary>
    /// One per date, in date order: the day's PLAN SLOTS - computeDaySlots for the
    /// date, or <see cref="HeldPlan.SlotsFromHeldPlan"/> for a day of the held plan.
    /// The roster fills these; it never re-derives shift shapes.
    /// </summary>
    public IReadOnlyList<DaySlots> DaySlots { get; init; } = [];
    /// <summary>
    /// Build from the store's HELD plan (the one its review approved) - its shifts
    /// are the slots to fill and its painted tiles the work, manual edits included.
    /// Null: the day's slots are solved afresh, as the analysis pages and the test
    /// harness use it.
    /// </summary>
    public IReadOnlyList<HeldPlanDay>? HeldPlan { get; init; }
}

/// <summary>A day of the roster (Stage2Result plus the day's state).</summary>
public sealed record RosterComputation
{
    /// <summary>The roster's shifts (FShift instances).</summary>
    public List<Shift> Shifts { get; init; } = new();
    public List<string> Flags { get; init; } = new();
    public bool HasData { get; init; }
    // Deliberately closed day (special trading hours / Sales override) - an
    // empty roster that is correct, not a failure.
    public bool Closed { get; init; }
    public double TradingOpen { get; init; }
    public double TradingClose { get; init; }
}

/// <summary>The fortnight's roster, one RosterComputation per date.</summary>
public sealed record FortnightRoster
{
    public JsMap<DateOnly, RosterComputation> ByDate { get; init; } = new();
    /// <summary>Period-level (e.g. under-contract employees).</summary>
    public List<string> FortnightFlags { get; init; } = new();
    /// <summary>Where each person landed against contract - the basis for make-up pay.</summary>
    public List<ContractOutcome> Outcomes { get; init; } = new();
}

public static class RosterCompute
{
    /// <summary>The roster of a date nobody has data for (EMPTY).</summary>
    public static RosterComputation Empty() => new()
    {
        Shifts = new(),
        Flags = new(),
        HasData = false,
        Closed = false,
        TradingOpen = 0,
        TradingClose = 0,
    };

    private static double Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>
    /// computeRosterFortnight after its reads: each employee's day, the fortnight
    /// engine, and the day's plan flags in front of the roster's own.
    /// </summary>
    public static FortnightRoster ComputeRosterFortnight(FortnightSources src)
    {
        var dates = Period.PeriodDatesFrom(src.PeriodStart);
        var result = FortnightEngine.RunFortnightStage2(BuildFortnightInput(src));

        var out_ = new JsMap<DateOnly, RosterComputation>();
        for (var di = 0; di < dates.Count; di++)
        {
            var date = dates[di];
            var shaped = src.DaySlots[di].Shaped;
            var s1 = src.DaySlots[di].Stage1;
            var dr = result.ByDate.Get(date.ToIso());
            out_.Set(date, new RosterComputation
            {
                Shifts = dr is null ? new List<Shift>() : dr.Shifts.Cast<Shift>().ToList(),
                // Plan-structure flags (manager gap, slivers, safety floor) surface on
                // the roster day too - same day, same structure.
                Flags = [.. shaped?.Flags ?? [], .. dr?.Flags ?? []],
                HasData = s1.HasData,
                Closed = s1.Closed,
                TradingOpen = s1.TradingOpen,
                TradingClose = s1.TradingClose,
            });
        }
        return new FortnightRoster { ByDate = out_, FortnightFlags = result.Flags, Outcomes = result.Outcomes };
    }

    /// <summary>
    /// The fortnight engine's input as computeRosterFortnight builds it from the
    /// reads: the active team, and each date's context with every employee's
    /// availability (leave, weekend-off markers, granted day-offs, fixed rosters
    /// and locked shifts collapsed into one DayAvail).
    /// </summary>
    public static FortnightInput BuildFortnightInput(FortnightSources src)
    {
        var dates = Period.PeriodDatesFrom(src.PeriodStart);
        if (src.DaySlots.Count != dates.Count)
            throw new ArgumentException($"FortnightSources.DaySlots has {src.DaySlots.Count} days; the period has {dates.Count}.");
        var emps = src.Employees.Where(e => e.Active).ToList();

        // Lookup maps keyed for O(1) per (employee, date) resolution.
        var availByKey = new JsMap<string, AvailabilityRow>(src.Availability.Select(a => ($"{a.EmployeeId}:{a.DayOfWeek}", a)));
        var leaveByEmp = new JsMap<string, List<LeaveRow>>();
        foreach (var l in src.Leave)
        {
            var arr = leaveByEmp.GetOr(l.EmployeeId, new List<LeaveRow>());
            arr.Add(l);
            leaveByEmp.Set(l.EmployeeId, arr);
        }
        var restrByEmp = new JsMap<string, List<string>>();
        foreach (var r in src.Restricted)
        {
            var arr = restrByEmp.GetOr(r.EmployeeId, new List<string>());
            arr.Add(r.LabourGroup);
            restrByEmp.Set(r.EmployeeId, arr);
        }
        // Fixed rosters are cycles of 1/2/4 weekly templates; which template a date
        // uses depends on the employee's cycle anchor (1-week cycles always week 0).
        var fixedByKey = new JsMap<string, FixedRosterRow>(src.FixedRosters.Select(f => ($"{f.EmployeeId}:{f.WeekIndex}:{f.DayOfWeek}", f)));
        var cycleWeekFor = FixedCycle.MakeCycleResolver(src.FixedCycles);
        var markerByKey = new JsMap<string, WeekendMarkerRow>(src.WeekendMarkers.Select(m => ($"{m.EmployeeId}:{m.WeekendStart.ToIso()}", m)));
        var dayOffSet = new JsSet<string>(src.DayOffs.Select(d => $"{d.EmployeeId}:{d.Date.ToIso()}"));
        // Manually-locked shifts are immovable anchors (spec §6.6): Stage 2 pins them
        // exactly and rebuilds the rest of the roster around them, taking precedence
        // over a day-of-week fixed roster.
        var lockedByKey = new JsMap<string, MinuteWindow>(src.LockedShifts.Select(s =>
            ($"{s.EmployeeId}:{s.Date.ToIso()}", new MinuteWindow(Minutes(s.StartTime), Minutes(s.EndTime)))));
        var holidaySet = new HashSet<DateOnly>(src.Holidays);

        var employees = emps.Select(e => new FortnightEmp
        {
            Id = e.Id,
            Name = e.Name,
            Role = e.Role,
            ContractHours = e.ContractHours,
            Restricted = restrByEmp.Get(e.Id) ?? [],
            NotionalHours = e.NotionalHours,
        }).ToList();

        DayAvail AvailFor(string empId, DateOnly date)
        {
            var dow = Period.DowMon0(date);
            var a = availByKey.Get($"{empId}:{dow}");
            var leaveRow = (leaveByEmp.Get(empId) ?? []).Find(l => l.StartDate <= date && date <= l.EndDate);
            var onLeave = leaveRow is not null;
            var weekendOff = false;
            if (dow == 5) weekendOff = markerByKey.Get($"{empId}:{date.ToIso()}")?.SatOff == true;
            else if (dow == 6) weekendOff = markerByKey.Get($"{empId}:{Period.AddDaysIso(date, -1).ToIso()}")?.SunOff == true;
            var dayOff = dayOffSet.Has($"{empId}:{date.ToIso()}");
            var locked = lockedByKey.Get($"{empId}:{date.ToIso()}");
            var fx = fixedByKey.Get($"{empId}:{cycleWeekFor(empId, date)}:{dow}");
            var fixedSpan = locked ?? (fx is not null ? new MinuteWindow(Minutes(fx.StartTime), Minutes(fx.EndTime)) : null);
            // A locked shift is honoured even if the person is otherwise marked off that
            // day - the manager chose to keep it.
            return new DayAvail
            {
                OnLeave = onLeave,
                LeaveDayHours = leaveRow?.DayHours,
                Available = locked is not null || ((a is null || a.Available) && !onLeave && !weekendOff && !dayOff),
                FromMin = a?.FromTime is { } from ? Minutes(from) : null,
                ToMin = a?.ToTime is { } to ? Minutes(to) : null,
                Fixed = fixedSpan,
            };
        }

        var days = dates.Select((date, di) =>
        {
            var slots = src.DaySlots[di];
            var shaped = slots.Shaped;
            var s1 = slots.Stage1;
            var avail = new JsMap<string, DayAvail>(employees.Select(e => (e.Id, AvailFor(e.Id, date))));
            return new DayContext
            {
                Date = date.ToIso(),
                WeekIndex = di < 7 ? 0 : 1,
                IsSchoolDay = src.SchoolCalendar.IsSchoolDay(date),
                Closed = !s1.HasData || shaped is null,
                PublicHoliday = holidaySet.Contains(date),
                StaffOpen = s1.StaffOpen,
                StaffClose = s1.StaffClose,
                TradingOpen = s1.TradingOpen,
                TradingClose = s1.TradingClose,
                Slots = shaped?.Slots ?? [],
                DemandByBlock = shaped?.DemandByBlock ?? [],
                Avail = avail,
                AllowedGroups = s1.HasData ? Stage1Compute.AllowedGroupsFromStage1(s1) : null,
                TaskWindows = s1.HasData ? Stage1Compute.TaskWindowsFromStage1(s1) : null,
                UnitWindows = s1.HasData ? Stage1Compute.UnitWindowsFromStage1(s1) : null,
                AdminDeadline = s1.HasData ? Stage1Compute.AdminDeadlineFromStage1(s1) : null,
                HeldShifts = HeldPlan.HeldShiftsOf(src.HeldPlan?.FirstOrDefault(h => h.Date == date)),
                BreakTimeAddedMin = Js.Round(BreakTime.BreakTimeAdded(shaped?.Flags) * 60),
            };
        }).ToList();

        return new FortnightInput
        {
            Days = days,
            Employees = employees,
            BreakRules = src.BreakRules,
            TaskPriority = [.. src.TaskPriority],
            Rules = src.RuleOverrides,
            Order = src.RosterOrder,
        };
    }
}
