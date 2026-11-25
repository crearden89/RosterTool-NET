// New: the Team Members page's view models and action inputs - the rows
// app/(app)/setup/staff/page.tsx selects and hands to StaffClient, and the
// arguments of the actions in setup/staff/actions.ts and setup/requests/actions.ts.

using Roster.Web.Components.Ui;

namespace Roster.Web.Features.Team;

/// <summary>
/// What every action returns (the TypeScript <c>{ ok, error }</c>): a refusal is a
/// message the page shows inline, never an exception.
/// </summary>
public sealed record SaveResult(bool Ok, string? Error = null)
{
    public static SaveResult Success { get; } = new(true);

    public static SaveResult Fail(string error) => new(false, error);
}

/// <summary>A team member as the page shows them (page.tsx's employees select).</summary>
public sealed record TeamMember
{
    public required Guid Id { get; init; }

    /// <summary>As UKG writes it, e.g. "4-Nye, Harper": the rank prefix orders the list.</summary>
    public required string Name { get; init; }

    /// <summary>The base role the engines reason about (SM, ASM, DSM, RSM, SA, JSA).</summary>
    public required string Role { get; init; }

    /// <summary>The payroll position (the pay_rates key); null for rows that predate positions.</summary>
    public string? PositionCode { get; init; }

    /// <summary>Hours per FORTNIGHT.</summary>
    public double ContractHours { get; init; }

    /// <summary>The value of one day toward contract; null = the default (fortnight contract / 10).</summary>
    public double? NotionalHours { get; init; }

    public string? Notes { get; init; }

    /// <summary>Tenure band for SM (1/2/3/4/7) and ASM (1/2).</summary>
    public int? SmYearBand { get; init; }

    /// <summary>
    /// Date they joined. Null for staff who predate the field - treated as
    /// experienced, never as new starters.
    /// </summary>
    public DateOnly? StartDate { get; init; }
}

/// <summary>A payroll position in the position pickers (refdata getPositions).</summary>
public sealed record PositionOption(string Code, string Label, string BaseRole, double? WeeklyHours, int? YearBand);

/// <summary>A labour group a person may not be given (employee_restricted_tasks).</summary>
public sealed record RestrictedTask(Guid EmployeeId, string LabourGroup);

/// <summary>A leave booking (employee_leave). DayHours null = a full contract day (the notional).</summary>
public sealed record LeaveBooking(Guid Id, Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string LeaveType, double? DayHours);

/// <summary>
/// One weekday of a person's availability (employee_availability), Monday = 0. A day
/// with no row, or an open one, is available all day; a window is part of the day;
/// Available = false is a day they can't work.
/// </summary>
public sealed record AvailabilityRow(Guid EmployeeId, int DayOfWeek, bool Available, TimeOnly? FromTime, TimeOnly? ToTime);

/// <summary>One locked shift of a fixed roster (fixed_rosters): week 0..3 of its cycle, Monday = 0.</summary>
public sealed record FixedShift(Guid Id, Guid EmployeeId, int WeekIndex, int DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

/// <summary>A fixed roster's cycle (fixed_roster_cycles): 1, 2 or 4 weeks from the Monday StartDate.</summary>
public sealed record FixedRosterCycleRow(Guid EmployeeId, int CycleWeeks, DateOnly StartDate);

/// <summary>A day-off request (day_off_requests): pending, granted or declined.</summary>
public sealed record DayOffRequestRow(Guid Id, Guid EmployeeId, DateOnly Date, DateTimeOffset SubmittedAt, string Status, string? Notes);

/// <summary>Everything the Team Members page shows: what page.tsx loads for StaffClient.</summary>
public sealed record TeamPageData
{
    /// <summary>The store the page is on; null when the person has no store in scope.</summary>
    public required Guid? StoreId { get; init; }

    /// <summary>The store picker's stores; empty for a Store-tier user (they have one store).</summary>
    public required IReadOnlyList<StoreOption> Stores { get; init; }

    /// <summary>The store's active team, in rank order (<see cref="TeamRules.SortTeam"/>).</summary>
    public required IReadOnlyList<TeamMember> Employees { get; init; }

    public required IReadOnlyList<PositionOption> Positions { get; init; }
    public required IReadOnlyList<RestrictedTask> Restricted { get; init; }
    public required IReadOnlyList<LeaveBooking> Leave { get; init; }
    public required IReadOnlyList<AvailabilityRow> Availability { get; init; }

    /// <summary>The team's locked shifts, every week of every cycle.</summary>
    public required IReadOnlyList<FixedShift> Fixed { get; init; }

    public required IReadOnlyList<FixedRosterCycleRow> Cycles { get; init; }

    /// <summary>
    /// The fortnightly paid hours of each person's fixed roster
    /// (<see cref="TeamRules.FixedPaidHours"/>), for the over-contract warnings.
    /// </summary>
    public required IReadOnlyDictionary<Guid, double> FixedPaidHours { get; init; }

    /// <summary>The team's day-off requests, every status, oldest first (first in, first served).</summary>
    public required IReadOnlyList<DayOffRequestRow> Requests { get; init; }

    /// <summary>PA and DSO: the page shows, and changes nothing.</summary>
    public required bool ReadOnly { get; init; }
}

/// <summary>
/// One day of <see cref="ITeamService.SetAvailabilityAsync"/> (actions.ts AvailabilityDay):
/// available all day (no window), a window, or unavailable.
/// </summary>
public sealed record AvailabilityDay(int DayOfWeek, bool Available, TimeOnly? FromTime, TimeOnly? ToTime);

/// <summary>One locked shift of <see cref="ITeamService.SetFixedRosterCycleAsync"/>: week 0..3, Monday = 0.</summary>
public sealed record FixedDay(int WeekIndex, int DayOfWeek, TimeOnly Start, TimeOnly End);
