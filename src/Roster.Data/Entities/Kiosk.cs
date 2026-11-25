namespace Roster.Data.Entities;

// ─── Load Planner and Floor Display (the no-login kiosk pages) ────────────────
// Postgres: 0002 load_types + daily_loads, 0017 load_assignments, 0032 tenure-band
// pallet minutes, 0034 store_walk_notes, 0035 admin_task_checks, 0038 load priority,
// 0049 load sub-task.

public static class LoadSubTasks
{
    public static readonly string[] All =
        ["produce", "long_life", "freezer", "ambient", "bread", "short_life", "freeze_to_thaw", "ambient_last"];
}

/// <summary>Pallet time standards, edited by a super user (Admin → Load Types).</summary>
public class LoadType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Experienced (over 90 days) minutes per pallet; &gt; 0.</summary>
    public double MinutesPerPallet { get; set; }
    public string Color { get; set; } = "#2563eb";
    public int SortOrder { get; set; }
    public double? MinutesPerPalletLt90 { get; set; }
    public double? MinutesPerPalletLt60 { get; set; }
    public double? MinutesPerPalletLt30 { get; set; }
    /// <summary>Allocation order (lower first); 100 = unranked (0038).</summary>
    public int Priority { get; set; } = 100;
    public string? SubTask { get; set; }
}

/// <summary>The day's truck contents (pallets per load type), entered on the kiosk.</summary>
public class DailyLoad
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public Guid LoadTypeId { get; set; }
    /// <summary>0-99.</summary>
    public int Qty { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Which load category a crew member owned on a day (manual = pinned by the SM).</summary>
public class LoadAssignment
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>Deliberately not a foreign key (0017): history outlives the team list.</summary>
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string LoadType { get; set; } = "";
    public bool Manual { get; set; }
}

/// <summary>Something spotted on a store walk; carries over until completed (0034).</summary>
public class StoreWalkNote
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    /// <summary>1-500 characters after trimming.</summary>
    public string Note { get; set; } = "";
    public Guid? AssignedEmployeeId { get; set; }
    /// <summary>Denormalised on purpose: the note outlives today's roster.</summary>
    public string? AssignedName { get; set; }
    /// <summary>The store's own date.</summary>
    public DateOnly RaisedDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A tick against an admin task on the Floor Display, keyed by task NAME (0035).</summary>
public class AdminTaskCheck
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public string TaskName { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; }
}
