namespace Roster.Data.Entities;

// ─── Super-user configuration read by the engines ─────────────────────────────
// Postgres: 0010 rostering_rules, 0011 roster_order_steps, 0012 break_rules pay columns,
// 0019 admin_tasks, 0021 plan_placement_*, 0022 variance_settings, 0030 probation_buffers,
// 0038/0041/0049 reset_tasks, 0042 step_order. break_rules, labour_group_rules and
// labour_group_priority were created directly in the live database.

/// <summary>An OVERRIDE of one engine rule; the defaults live in the engine.</summary>
public class RosteringRule
{
    public string Key { get; set; } = "";
    public decimal Value { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class RosterStepTypes
{
    public const string AllocateSm = "ALLOCATE_SM", AllocateAsm = "ALLOCATE_ASM", AllocateDsm = "ALLOCATE_DSM",
        FillFloor = "FILL_FLOOR", ShuffleJsa = "SHUFFLE_JSA", ContractCheck = "CONTRACT_CHECK";
    public static readonly string[] All = [AllocateSm, AllocateAsm, AllocateDsm, FillFloor, ShuffleJsa, ContractCheck];
}

/// <summary>The configured roster phase order. An EMPTY table means the shipped default.</summary>
public class RosterOrderStep
{
    public Guid Id { get; set; }
    public int Position { get; set; }
    public string StepType { get; set; } = "";
    /// <summary>Weekday sequence (0 = Monday) a filling phase visits; a JSON array, or null.</summary>
    public List<int>? DayOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class BreakTypes
{
    public const string Flat30Unpaid = "flat_30_unpaid", Tiered = "tiered";
}

/// <summary>Per-role shift, break and pay rules (Rules → Break &amp; Work Rules).</summary>
public class BreakRule
{
    public string Role { get; set; } = "";
    public decimal MinShiftHours { get; set; }
    /// <summary>Caps PAID hours.</summary>
    public decimal MaxShiftHours { get; set; }
    public decimal GapHours { get; set; }
    public string BreakType { get; set; } = BreakTypes.Tiered;
    public decimal? WeeklyCap { get; set; }
    public int? ConsecDayCap { get; set; }
    public decimal? Ot15AfterHours { get; set; }
    public decimal? Ot19AfterHours { get; set; }
    /// <summary>Overtime after this many paid hours in a fortnight (0012).</summary>
    public decimal? OtPeriodAfterHours { get; set; }
    /// <summary>A break shorter than this puts the next shift on double time (0012).</summary>
    public decimal? DoubleTimeUnderGapHours { get; set; }
}

/// <summary>A time window a labour group may be placed in, per weekday, with an optional hours quota.</summary>
public class LabourGroupRule
{
    public Guid Id { get; set; }
    public string LabourGroup { get; set; } = "";
    public short DayOfWeek { get; set; }
    public TimeOnly WindowStart { get; set; }
    public TimeOnly WindowEnd { get; set; }
    /// <summary>Target hours for this window (0038); null = no quota.</summary>
    public decimal? WindowHours { get; set; }
}

/// <summary>Who picks a task up when several groups are live in the same block.</summary>
public class LabourGroupPriority
{
    public string LabourGroup { get; set; } = "";
    public int Priority { get; set; }
}

public static class WindowKinds
{
    public const string None = "none", Clock = "clock", Open = "open", Close = "close";
    public static readonly string[] All = [None, Clock, Open, Close];
}

/// <summary>A named administration task: schedule, duration formula and window (0019).</summary>
public class AdminTask
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>daily, weekly, monthly_nth or planning_week.</summary>
    public string ScheduleType { get; set; } = "daily";
    public short? DayOfWeek { get; set; }
    public short? NthWeek { get; set; }
    /// <summary>Region-varying weekday keyed by the region's 3-digit prefix, as JSON text, e.g. {"501":0}.</summary>
    public string? RegionDays { get; set; }
    /// <summary>fixed, per_employee, per_register or till_calibration.</summary>
    public string DurationType { get; set; } = "fixed";
    public decimal? FixedMinutes { get; set; }
    public decimal? MinutesPerUnit { get; set; }
    public decimal? MinMinutes { get; set; }
    public string WindowStartKind { get; set; } = WindowKinds.None;
    public TimeOnly? WindowStartTime { get; set; }
    public int WindowStartOffsetMin { get; set; }
    public string WindowEndKind { get; set; } = WindowKinds.None;
    public TimeOnly? WindowEndTime { get; set; }
    public int WindowEndOffsetMin { get; set; }
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>A named reset job funded out of the day's reset benchmark (0038).</summary>
public class ResetTask
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public decimal? SharePct { get; set; }
    public decimal? FixedMinutes { get; set; }
    public string WindowStartKind { get; set; } = WindowKinds.None;
    public TimeOnly? WindowStartTime { get; set; }
    public int WindowStartOffsetMin { get; set; }
    public string WindowEndKind { get; set; } = WindowKinds.None;
    public TimeOnly? WindowEndTime { get; set; }
    public int WindowEndOffsetMin { get; set; }
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>spread, avoid_peak, follow_demand, late or early (0041).</summary>
    public string Placement { get; set; } = "spread";
    /// <summary>re_runs or decarding; null = decided by the name (0049).</summary>
    public string? SubTask { get; set; }
}

/// <summary>Planning Order: when each labour group's hours are placed across the day (0021).</summary>
public class PlanPlacementRule
{
    public Guid Id { get; set; }
    public int Position { get; set; }
    public string LabourGroup { get; set; } = "";
    public string Strategy { get; set; } = "spread";
    public string AnchorKind { get; set; } = WindowKinds.None;
    public TimeOnly? AnchorTime { get; set; }
    public int AnchorOffsetMin { get; set; }
    public decimal? PrePeakPct { get; set; }
    public decimal? PostPeakPct { get; set; }
    public decimal? AvoidStrength { get; set; }
    public string? NightTraderStrategy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Single-row settings (the key is always true).</summary>
public class PlanPlacementSettings
{
    public bool Id { get; set; } = true;
    public bool Enabled { get; set; }
    public string PeakBasis { get; set; } = "hourly";
    public int PeakSmoothingBlocks { get; set; } = 3;
    public decimal PeakThresholdPct { get; set; } = 85m;
    public TimeOnly NightTraderFrom { get; set; } = new(17, 0);
    public decimal NightTraderSharePct { get; set; } = 25m;
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Order the window groups are laid on the canvas (0042); a JSON array.</summary>
    public List<string> StepOrder { get; set; } = ["admin", "load_running", "reset", "specials", "cleaning"];
}

/// <summary>Variance-report thresholds (single row, 0022).</summary>
public class VarianceSettings
{
    public bool Id { get; set; } = true;
    public decimal DataLightPct { get; set; } = 15m;
    public decimal MetricPct { get; set; } = 70m;
    public decimal HoursPct { get; set; } = 20m;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>SUPERSEDED by load_types.minutes_per_pallet_lt30/60/90 (0032); retained for reference.</summary>
public class ProbationBuffer
{
    public string LabourGroup { get; set; } = "*";
    public short MaxDays { get; set; }
    public decimal UpliftPct { get; set; }
}
