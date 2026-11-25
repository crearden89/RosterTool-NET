// Port of src/lib/load/probation.ts.
// ─── Tenure bands for the Load Planner (pure) ──────────────────────────────
// A new starter is slower at physical work than someone who has done it for a
// year. Without an allowance the planner prices every pallet the same and tells
// the SM the truck will be away earlier than it will be.
//
// The allowance lives on the LOAD TYPE, not here: each type carries its own
// minutes per pallet for each tenure band, so a manager reading the pallet
// standards can see exactly what a new starter costs on Freezer versus Produce
// rather than inferring it from a percentage kept on another screen.
//
// This module's only job is deciding WHICH band a person is in on a given day,
// and reading the matching number off a load type.

namespace Roster.Engine.Load;

/// <summary>Days of service, newest band first. <c>gt90</c> is a fully trained team member (TenureBand).</summary>
public static class TenureBands
{
    public const string Lt30 = "lt30";
    public const string Lt60 = "lt60";
    public const string Lt90 = "lt90";
    public const string Gt90 = "gt90";

    /// <summary>TENURE_BANDS: each band's key, label and short label, trained first.</summary>
    public static readonly IReadOnlyList<TenureBandInfo> All =
    [
        new(Gt90, "Over 90 days", ">90d"),
        new(Lt90, "Under 90 days", "<90d"),
        new(Lt60, "Under 60 days", "<60d"),
        new(Lt30, "Under 30 days", "<30d"),
    ];
}

public sealed record TenureBandInfo(string Key, string Label, string Short);

/// <summary>Minutes per pallet for one load type, by band. Null = fall back to >90.</summary>
public sealed record PalletMinutes
{
    public double Gt90 { get; init; }
    public double? Lt90 { get; init; }
    public double? Lt60 { get; init; }
    public double? Lt30 { get; init; }

    public PalletMinutes() { }

    public PalletMinutes(double gt90, double? lt90 = null, double? lt60 = null, double? lt30 = null)
    {
        Gt90 = gt90;
        Lt90 = lt90;
        Lt60 = lt60;
        Lt30 = lt30;
    }
}

public static class Probation
{
    /// <summary>
    /// Days of service on <paramref name="asOf"/>. Day 1 is the start date itself, so someone who
    /// started today has 1 day of service, not 0 - otherwise the first shift falls
    /// outside every band.
    ///
    /// Null start date returns null: an employee with no recorded start is treated
    /// as EXPERIENCED, never as a brand-new hire. Most of the existing team has no
    /// start date, and silently slowing them all down would be far worse than
    /// missing a genuine new starter.
    /// </summary>
    public static int? DaysOfService(DateOnly? startDate, DateOnly asOf)
    {
        if (startDate is null) return null;
        var days = asOf.DayNumber - startDate.Value.DayNumber + 1;
        return days > 0 ? days : null; // a future start date is not yet on the floor
    }

    /// <summary>
    /// <see cref="DaysOfService(DateOnly?, DateOnly)"/> for an ISO date string as the database
    /// row gives it: null, empty or unreadable is no start date.
    /// </summary>
    public static int? DaysOfService(string? startDate, DateOnly asOf) =>
        DaysOfService(ParseDate(startDate), asOf);

    /// <summary>
    /// Which band a tenure falls in. Bounds are INCLUSIVE at the top, so day 30 is
    /// still "&lt;30 days" and day 31 moves up - the way the labels read to a manager.
    /// Unknown tenure is treated as fully trained, per DaysOfService above.
    /// </summary>
    public static string TenureBandFor(int? days)
    {
        if (days is null) return TenureBands.Gt90;
        if (days <= 30) return TenureBands.Lt30;
        if (days <= 60) return TenureBands.Lt60;
        if (days <= 90) return TenureBands.Lt90;
        return TenureBands.Gt90;
    }

    /// <summary>The band for a start date on a given day, in one step.</summary>
    public static string BandForStartDate(DateOnly? startDate, DateOnly asOf) => TenureBandFor(DaysOfService(startDate, asOf));

    /// <inheritdoc cref="BandForStartDate(DateOnly?, DateOnly)"/>
    public static string BandForStartDate(string? startDate, DateOnly asOf) => TenureBandFor(DaysOfService(startDate, asOf));

    /// <summary>
    /// Minutes per pallet for a band. An unset band falls back to the >90 figure -
    /// a blank cell means "no extra allowance for this type", never zero minutes.
    /// </summary>
    public static double MinutesForBand(PalletMinutes m, string band)
    {
        double? v = band switch
        {
            TenureBands.Gt90 => m.Gt90,
            TenureBands.Lt90 => m.Lt90,
            TenureBands.Lt60 => m.Lt60,
            TenureBands.Lt30 => m.Lt30,
            _ => null,
        };
        return v is not null && v > 0 ? v.Value : m.Gt90;
    }

    /// <summary>Short label for the UI - nothing once someone is fully trained.</summary>
    public static string? TenureLabel(int? days)
    {
        if (days is null) return null;
        var band = TenureBandFor(days);
        if (band == TenureBands.Gt90) return null;
        return $"{days}d · {TenureBands.All.First(b => b.Key == band).Short}";
    }

    private static DateOnly? ParseDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateOnly.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }
}
