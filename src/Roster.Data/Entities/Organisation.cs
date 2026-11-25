namespace Roster.Data.Entities;

// ─── Organisation: director group → region → area → store ────────────────────
// Postgres: 0001_foundation (director_groups, regions, stores, store_trading_hours,
// store_attributes), 0014 (kiosk_token), 0016 (areas, stores.area_id/dso_name),
// 0028 (max_staff_on_leave), 0038 (night_fill), 0047 (min_checkout_hours dropped).
// special_trading_hours and store_attributes.opening_date/pair_store_id were created
// directly in the live database; their shape is read from the TypeScript app.

/// <summary>The Australian states and territories (Postgres enum au_state).</summary>
public static class AuStates
{
    public const string Nsw = "NSW", Vic = "VIC", Qld = "QLD", Sa = "SA", Wa = "WA", Tas = "TAS", Nt = "NT", Act = "ACT";
    public static readonly IReadOnlyList<string> All = [Nsw, Vic, Qld, Sa, Wa, Tas, Nt, Act];
}

public class DirectorGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Region
{
    public Guid Id { get; set; }
    /// <summary>Named "501 - Somewhere"; the 3-digit prefix keys region-varying admin schedules.</summary>
    public string Name { get; set; } = "";
    public Guid DirectorGroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DirectorGroup? DirectorGroup { get; set; }
}

/// <summary>An EMSO's patch of about six stores within a region (0016).</summary>
public class Area
{
    public Guid Id { get; set; }
    /// <summary>The Area Manager's name, as in the org extract. Unique.</summary>
    public string Name { get; set; } = "";
    public Guid? RegionId { get; set; }
    public Region? Region { get; set; }
}

public class Store
{
    public Guid Id { get; set; }
    /// <summary>External store ID used in every upload ("G" + 3 digits). Unique.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Unique URL-safe key (the retired floor-display route).</summary>
    public string Slug { get; set; } = "";
    public Guid RegionId { get; set; }
    public string State { get; set; } = AuStates.Nsw;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Secret per-store token behind the no-login kiosk links (0014). Unique.</summary>
    public string KioskToken { get; set; } = "";
    public Guid? AreaId { get; set; }
    /// <summary>The store's DSO, kept as reference from the org extract (0016).</summary>
    public string? DsoName { get; set; }

    public Region? Region { get; set; }
    public Area? Area { get; set; }
    public StoreAttributes? Attributes { get; set; }
    public List<StoreTradingHours> TradingHours { get; set; } = [];
}

/// <summary>Weekly trading hours; day_of_week 0 = Monday … 6 = Sunday.</summary>
public class StoreTradingHours
{
    public Guid StoreId { get; set; }
    public short DayOfWeek { get; set; }
    public TimeOnly? OpenTime { get; set; }
    public TimeOnly? CloseTime { get; set; }
    public bool Closed { get; set; }
}

public class StoreAttributes
{
    public Guid StoreId { get; set; }
    public bool HasSco { get; set; }
    public bool IsNewScoModel { get; set; }
    public int McoRegisters { get; set; }
    public int ScoLanes { get; set; }
    public decimal ScoEfficiency { get; set; } = 0.45m;
    public decimal FirstStep { get; set; } = 1.2m;
    /// <summary>Between 0 and 0.90 (check constraint).</summary>
    public decimal ScoShare { get; set; } = 0.66m;
    public decimal OeTargetPct { get; set; } = 90m;
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Most people on approved leave at once (0028).</summary>
    public short MaxStaffOnLeave { get; set; } = 3;
    /// <summary>Night-fill store: load is worked in the evening (0038).</summary>
    public bool NightFill { get; set; }
    /// <summary>Store opening date (live-database column; the uploads page reads it).</summary>
    public DateOnly? OpeningDate { get; set; }
    /// <summary>A paired store (live-database column).</summary>
    public Guid? PairStoreId { get; set; }
}

/// <summary>Trading hours that override the weekly pattern for a date range.</summary>
public class SpecialTradingHours
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool Closed { get; set; }
    public TimeOnly? OpenTime { get; set; }
    public TimeOnly? CloseTime { get; set; }
    public string? Note { get; set; }
}
