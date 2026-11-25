namespace Roster.Data.Entities;

// ─── Pay rates (created in the live database; 0012 and 0020 reshape them) ─────

public static class PayDayTypes
{
    public const string Weekday = "weekday", Saturday = "saturday", Sunday = "sunday", PublicHoliday = "public_holiday";
    public static readonly string[] All = [Weekday, Saturday, Sunday, PublicHoliday];
}

/// <summary>An hourly rate by day type, hour of day (0-23) and position code.</summary>
public class PayRate
{
    public string DayType { get; set; } = PayDayTypes.Weekday;
    public short Hour { get; set; }
    /// <summary>A position code (pay_rates_role_fkey → positions.code).</summary>
    public string Role { get; set; } = "";
    public decimal Rate { get; set; }
}

/// <summary>Global pay multipliers (single row).</summary>
public class PaySettings
{
    public bool Id { get; set; } = true;
    public decimal RsmAllowance { get; set; } = 9m;
    public decimal SuperRate { get; set; } = 0.12m;
    public decimal Overtime15Multiplier { get; set; } = 1.5m;
    public decimal Overtime19Multiplier { get; set; } = 1.9m;
    public decimal DoubleTimeMultiplier { get; set; } = 2.0m;
    public decimal SundayOtFromHour { get; set; }
    public decimal SundayOtToHour { get; set; } = 9m;
}

public class PayrollTaxRate
{
    public string State { get; set; } = "";
    public decimal Rate { get; set; }
}
