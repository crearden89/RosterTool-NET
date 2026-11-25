namespace Roster.Data.Entities;

// ─── Plan and roster state ────────────────────────────────────────────────────
// plan_snapshots: 0003 + 0005 (version). day_plans, planned_blocks, generated_shifts,
// shift_task_blocks, sales_overrides, roster_audit and the non-op tables were created
// directly in the live database (0004/0008/0049 write them, 0018 reshapes non-op).

public static class DayStatuses
{
    public const string None = "none", Generated = "generated", Approved = "approved";
    public static readonly string[] All = [None, Generated, Approved];
}

/// <summary>A store-day's plan and roster status. roster_status is the source of truth for "approved".</summary>
public class DayPlan
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public string PlanStatus { get; set; } = DayStatuses.None;
    public string RosterStatus { get; set; } = DayStatuses.None;
    public DateTimeOffset? PlanGeneratedAt { get; set; }
    public DateTimeOffset? RosterGeneratedAt { get; set; }
    /// <summary>Public-holiday load-running reallocation switched on for this day.</summary>
    public bool PhLoadReallocated { get; set; }
}

/// <summary>An approved plan's Stage 1 headcount per labour group per 15-minute block.</summary>
public class PlannedBlock
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public string LabourGroup { get; set; } = "";
    public TimeOnly BlockTime { get; set; }
    public int Headcount { get; set; }
}

public static class SnapshotKinds
{
    public const string Plan = "plan", Roster = "roster";
}

/// <summary>
/// A held plan or roster: the engine's output as JSON text, with an optimistic-concurrency
/// version. Read and written through Snapshots/PlanSnapshotStore.
/// </summary>
public class PlanSnapshot
{
    public Guid StoreId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public string Kind { get; set; } = SnapshotKinds.Plan;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset GeneratedAt { get; set; }
    public Guid? GeneratedBy { get; set; }
    public int Version { get; set; } = 1;
}

/// <summary>A Sales-page adjustment: a percentage, a new sales figure or "closed".</summary>
public class SalesOverride
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public bool Closed { get; set; }
    public string? Method { get; set; }
    public decimal? Pct { get; set; }
    public decimal? Value { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
}

public class NonOpCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Sort { get; set; }
}

public class NonOpSubcategory
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = "";
    public int? MinMinutes { get; set; }
    public int? MaxMinutes { get; set; }
    /// <summary>Weekdays (0 = Monday) the item may be booked on; a JSON array (Postgres int[]).</summary>
    public List<int> DaysAllowed { get; set; } = [0, 1, 2, 3, 4, 5, 6];
    /// <summary>Pre-filled time when adding an entry (0018).</summary>
    public int? DefaultMinutes { get; set; }
    /// <summary>Approval note for items with no fixed time (0018).</summary>
    public string? Note { get; set; }
    public NonOpCategory? Category { get; set; }
}

/// <summary>Booked time off the floor (training, meetings): hours the day must cover.</summary>
public class NonOpEntry
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public Guid SubcategoryId { get; set; }
    public Guid? EmployeeId { get; set; }
    public int Minutes { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Who approved or withdrew what, when. detail is JSON text.</summary>
public class RosterAudit
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly? Date { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A published (approved) roster shift. Written by Services/RosterApprovalService.</summary>
public class GeneratedShift
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string Role { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    /// <summary>Total time off the floor (not the unpaid part).</summary>
    public int BreakMinutes { get; set; }
    public bool Locked { get; set; }
    public Guid? LockedBy { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<ShiftTaskBlock> Blocks { get; set; } = [];
}

/// <summary>One painted 15-minute tile of a published shift, with its sub-task label (0049).</summary>
public class ShiftTaskBlock
{
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public TimeOnly BlockTime { get; set; }
    public string LabourGroup { get; set; } = "";
    public string? SubTask { get; set; }
}
