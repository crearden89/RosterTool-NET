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
    public int Hour { get; set; }
    /// <summary>A position code (pay_rates_role_fkey → positions.code).</summary>
    public string Role { get; set; } = "";
    public double Rate { get; set; }
}

/// <summary>Global pay multipliers (single row).</summary>
public class PaySettings
{
    public bool Id { get; set; } = true;
    public double RsmAllowance { get; set; } = 9.0;
    public double SuperRate { get; set; } = 0.12;
    public double Overtime15Multiplier { get; set; } = 1.5;
    public double Overtime19Multiplier { get; set; } = 1.9;
    public double DoubleTimeMultiplier { get; set; } = 2.0;
    public double SundayOtFromHour { get; set; }
    public double SundayOtToHour { get; set; } = 9.0;
}

public class PayrollTaxRate
{
    public string State { get; set; } = "";
    public double Rate { get; set; }
}
