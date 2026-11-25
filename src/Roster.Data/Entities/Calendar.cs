namespace Roster.Data.Entities;

// ─── Calendars and periods (0001) ─────────────────────────────────────────────

public class PublicHolidayNational
{
    public Guid Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class PublicHolidayState
{
    public Guid Id { get; set; }
    public string State { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class PublicHolidayLocal
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class SchoolTerm
{
    public Guid Id { get; set; }
    public string State { get; set; } = "";
    public string TermLabel { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}

/// <summary>A pupil-free day inside a term.</summary>
public class SchoolTermException
{
    public Guid Id { get; set; }
    public string State { get; set; } = "";
    public DateOnly Date { get; set; }
    public string? Note { get; set; }
}

/// <summary>A national 14-day fortnight (rows follow the grid in Feeds/Period.cs).</summary>
public class RosterPeriod
{
    public Guid Id { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
