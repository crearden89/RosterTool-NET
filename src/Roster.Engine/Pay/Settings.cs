// Port of the shapes and defaults of src/lib/payroll/settings.ts (the reads
// themselves belong to the data layer).

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public sealed record PaySettings
{
    public double RsmAllowance { get; init; } = 9;
    public double SuperRate { get; init; } = 0.12;
    public double Overtime15Multiplier { get; init; } = 1.5;
    public double Overtime19Multiplier { get; init; } = 1.9;
    // Overtime worked inside this Sunday band (hours from midnight) is paid at
    // the 1.9x multiplier instead of 1.5x.
    public double SundayOtFromHour { get; init; } = 0;
    public double SundayOtToHour { get; init; } = 9;
    // Paid for a whole shift that follows a break shorter than the role's
    // double_time_under_gap_hours.
    public double DoubleTimeMultiplier { get; init; } = 2;

    /// <summary>The settings used when pay_settings has no row.</summary>
    public static readonly PaySettings Default = new();
}

/// <summary>One cell of the pay-rate grid (pay_rates): day type × hour × position → $/hour.</summary>
public sealed record PayRateRow(
    [property: JsonPropertyName("day_type")] string DayType,
    [property: JsonPropertyName("hour")] double Hour,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("rate")] double Rate);

// The per-role halves of the pay rules (break_rules); the global multipliers
// and the Sunday band live in pay_settings. A null threshold means the rule
// does not apply to that role at all - which is how the salaried SM/ASM are
// kept out of overtime and double time.
public sealed record RolePayRule(double? DailyOtAfterPaidHours, double? PeriodOtAfterPaidHours, double? ShortBreakHours);

public static class PayrollTax
{
    /// <summary>Every state at 0 - what the tax table reads as before any row is saved.</summary>
    public static Dictionary<string, double> ZeroRates() => AuStates.All.ToDictionary(s => s, _ => 0.0);
}
