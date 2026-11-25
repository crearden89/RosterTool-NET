using Roster.Data.Feeds;

namespace Roster.Data.Entities;

// ─── Uploaded and fed data ────────────────────────────────────────────────────
// Postgres: 0001 (upload_batches, benchmark_days, quarter_hour_segments, actuals_days),
// 0015 feed_tokens, 0019 employee_count, 0022 per-category actual hours, 0023 file
// booleans, 0024 actual_customers, 0029/0036/0040 upload type list, 0039
// pos_interval_stats, 0043 cron_runs. benchmark_days.checkout_hours_100 (and its ÷0.9
// column), the NUMERIC customer_count and quarter_hour_allocation were changed or
// created directly in the live database; their shape is read from the TypeScript app.

public static class UploadTypes
{
    public const string Benchmark = "benchmark";
    public const string Actuals = "actuals";
    /// <summary>Retired pre-computed curve; kept for historical batch rows.</summary>
    public const string QuarterHour = "quarter_hour";
    public const string LeaveLiability = "leave_liability";
    public const string LeaveRequests = "leave_requests";
    public const string Contacts = "contacts";
    public const string PosQuarterHour = "pos_quarter_hour";

    /// <summary>The upload_batches_type_check list after 0040.</summary>
    public static readonly string[] All =
        [Benchmark, Actuals, QuarterHour, LeaveLiability, LeaveRequests, Contacts, PosQuarterHour];
}

/// <summary>One upload or feed push, and what became of its rows.</summary>
public class UploadBatch
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Filename { get; set; } = "";
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? RosterPeriodId { get; set; }
    public int TotalRows { get; set; }
    public int AcceptedRows { get; set; }
    public int RejectedRows { get; set; }
    /// <summary>Row-level report, stored as a JSON array (nvarchar(max), default '[]').</summary>
    public List<RowError> ErrorReport { get; set; } = [];
}

/// <summary>
/// The fortnightly forecast for one store-day. Hours arrive at 100%; the *_90 columns are
/// persisted computed columns (x / 0.9), so the 90% planning transform can never drift.
/// </summary>
public class BenchmarkDay
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public bool IsSco { get; set; }
    public decimal SalesForecast { get; set; }
    public decimal Items { get; set; }
    /// <summary>NUMERIC in the live database: forecasts carry decimals.</summary>
    public decimal CustomerCount { get; set; }
    public decimal LrHours100 { get; set; }
    public decimal CleaningHours100 { get; set; }
    public decimal SpecialsHours100 { get; set; }
    public decimal ResetHours100 { get; set; }
    public decimal AdminHours100 { get; set; }
    /// <summary>Reference only (0001); the single Checkout_Hours total replaced the split.</summary>
    public decimal RegistersHours100 { get; set; }
    /// <summary>Reference only (0001).</summary>
    public decimal ScoHours100 { get; set; }
    /// <summary>The day's checkout labour budget (live-database column).</summary>
    public decimal CheckoutHours100 { get; set; }
    public decimal LrHours90 { get; private set; }
    public decimal CleaningHours90 { get; private set; }
    public decimal SpecialsHours90 { get; private set; }
    public decimal ResetHours90 { get; private set; }
    public decimal AdminHours90 { get; private set; }
    public decimal RegistersHours90 { get; private set; }
    public decimal ScoHours90 { get; private set; }
    public decimal CheckoutHours90 { get; private set; }
    /// <summary>Headcount that sizes the per-employee admin tasks (0019).</summary>
    public int EmployeeCount { get; set; }
    public bool? IsMhs { get; set; }
    public bool? HasEsl { get; set; }
    public bool? HasBaler { get; set; }
    public Guid? RosterPeriodId { get; set; }
    public Guid? BatchId { get; set; }
}

/// <summary>The retired dated quarter-hour segment upload (0001). Never dropped; nothing reads it.</summary>
public class QuarterHourSegment
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly BlockTime { get; set; }
    public decimal Segment { get; set; }
    public Guid? RosterPeriodId { get; set; }
    public Guid? BatchId { get; set; }
}

/// <summary>What a store actually traded and worked on a day.</summary>
public class ActualsDay
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public decimal ActualSales { get; set; }
    public decimal ActualItems { get; set; }
    /// <summary>Sum of the six per-category hours columns.</summary>
    public decimal ActualHours100 { get; set; }
    /// <summary>Kronos (UKG) paid hours.</summary>
    public decimal UkgActualHours { get; set; }
    public Guid? BatchId { get; set; }
    public decimal? LrHours { get; set; }
    public decimal? CleaningHours { get; set; }
    public decimal? SpecialsHours { get; set; }
    public decimal? ResetHours { get; set; }
    public decimal? AdminHours { get; set; }
    public decimal? CheckoutHours { get; set; }
    public bool? IsMhs { get; set; }
    public bool? HasEsl { get; set; }
    public bool? HasBaler { get; set; }
    public decimal? ActualCustomers { get; set; }
}

/// <summary>
/// The legacy weekly allocation curve (fraction of a weekday's checkout per block),
/// kept as the per-weekday fallback until a store's POS statistics arrive.
/// </summary>
public class QuarterHourAllocation
{
    public Guid StoreId { get; set; }
    public short DayOfWeek { get; set; }
    public TimeOnly BlockTime { get; set; }
    public decimal Allocation { get; set; }
    public Guid? BatchId { get; set; }
}

/// <summary>Four weeks of measured trade per 15-minute block, manned and self-checkout apart.</summary>
public class PosIntervalStat
{
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly BlockTime { get; set; }
    public decimal McoCustomers { get; set; }
    public decimal ScoCustomers { get; set; }
    public decimal McoItems { get; set; }
    public decimal ScoItems { get; set; }
    public Guid? BatchId { get; set; }
}

/// <summary>A bearer token for the data-feed API (/api/ingest/*). One per integration.</summary>
public class FeedToken
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Heartbeat for scheduled jobs (0043): when each last completed and what it returned.</summary>
public class CronRun
{
    public string Job { get; set; } = "";
    public DateTimeOffset LastRunAt { get; set; }
    public long RunCount { get; set; }
    /// <summary>JSON text, or null.</summary>
    public string? LastResult { get; set; }
}
