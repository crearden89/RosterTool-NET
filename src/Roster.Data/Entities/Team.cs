namespace Roster.Data.Entities;

// ─── Team members ─────────────────────────────────────────────────────────────
// employees and its child tables were created directly in the live database; the
// migrations add to them (0009 notional_hours + leave types, 0020 position_code,
// 0025 fixed-roster cycles, 0030 start_date, 0033 fortnightly contracts, 0036 ukg_id).
// Shapes not recorded in a migration are read from the TypeScript app.

/// <summary>The six base roles the engines reason about.</summary>
public static class Roles
{
    public const string Sm = "SM", Asm = "ASM", Dsm = "DSM", Rsm = "RSM", Sa = "SA", Jsa = "JSA";
    public static readonly string[] All = [Sm, Asm, Dsm, Rsm, Sa, Jsa];
    public static readonly string[] Managers = [Sm, Asm, Dsm, Rsm];
}

/// <summary>Leave types (0009): all credit toward contract except UNP.</summary>
public static class LeaveTypes
{
    public const string Annual = "ANN", LongService = "LS", Bereavement = "BRV", Sick = "SCK", Carers = "CAR", Unpaid = "UNP";
    public static readonly string[] All = [Annual, LongService, Bereavement, Sick, Carers, Unpaid];
}

/// <summary>A payroll position (0020): the pay_rates key, resolving to a base role.</summary>
public class Position
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string BaseRole { get; set; } = "";
    /// <summary>Contract size the position implies, WEEKLY. Null = set per person.</summary>
    public double? WeeklyHours { get; set; }
    public int? YearBand { get; set; }
    /// <summary>False only for RSM (paid the SA rate plus the sole-manager allowance).</summary>
    public bool HasRate { get; set; } = true;
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}

public class Employee
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    /// <summary>As UKG writes it, e.g. "4-Nye, Harper"; the rank prefix orders the list.</summary>
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Sa;
    /// <summary>Hours per FORTNIGHT (0033).</summary>
    public double ContractHours { get; set; }
    /// <summary>Tenure band for SM (1/2/3/4/7) and ASM (1/2).</summary>
    public int? SmYearBand { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Value of one leave / public-holiday day; null = fortnight contract / 10 (0009).</summary>
    public double? NotionalHours { get; set; }
    public string? PositionCode { get; set; }
    /// <summary>Hire date; null = experienced (no probation allowance) (0030).</summary>
    public DateOnly? StartDate { get; set; }
    /// <summary>UKG employee id; unique per store when present (0036).</summary>
    public string? UkgId { get; set; }

    public Store? Store { get; set; }
    public Position? Position { get; set; }
}

/// <summary>Weekly availability: available all day, a window, or unavailable.</summary>
public class EmployeeAvailability
{
    public Guid EmployeeId { get; set; }
    public int DayOfWeek { get; set; }
    public bool Available { get; set; } = true;
    public TimeOnly? FromTime { get; set; }
    public TimeOnly? ToTime { get; set; }
    public Employee? Employee { get; set; }
}

public class EmployeeLeave
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string LeaveType { get; set; } = LeaveTypes.Annual;
    /// <summary>Hours credited per leave weekday; null = a full contract day.</summary>
    public double? DayHours { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Employee? Employee { get; set; }
}

/// <summary>A labour group this person may not be given.</summary>
public class EmployeeRestrictedTask
{
    public Guid EmployeeId { get; set; }
    public string LabourGroup { get; set; } = "";
    public Employee? Employee { get; set; }
}

public static class DayOffStatuses
{
    public const string Pending = "pending", Granted = "granted", Declined = "declined";
    public static readonly string[] All = [Pending, Granted, Declined];
}

public class DayOffRequest
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = DayOffStatuses.Pending;
    public Employee? Employee { get; set; }
}

/// <summary>Weekend-off switches, keyed on the weekend's Saturday.</summary>
public class WeekendMarker
{
    public Guid EmployeeId { get; set; }
    public DateOnly WeekendStart { get; set; }
    public bool SatOff { get; set; }
    public bool SunOff { get; set; }
    public Employee? Employee { get; set; }
}

/// <summary>One day of a fixed-roster template; week_index 0..3 within its cycle (0025).</summary>
public class FixedRoster
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public int DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int WeekIndex { get; set; }
    public Employee? Employee { get; set; }
}

/// <summary>A fixed roster's cycle: 1, 2 or 4 weeks from a Monday (0025).</summary>
public class FixedRosterCycle
{
    public Guid EmployeeId { get; set; }
    public int CycleWeeks { get; set; } = 1;
    public DateOnly StartDate { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Employee? Employee { get; set; }
}
