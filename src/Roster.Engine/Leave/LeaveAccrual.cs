// Port of src/lib/leave/accrual.ts.
// ─── Leave accrual + projection (pure) ───────────────────────────────────────
// The workbook's Team Analysis maths, corrected on the points confirmed with
// the business:
//
//   * the projection runs from TODAY, not the 1st of the current month (the
//     workbook double-counted month-to-date accrual already inside the balance);
//   * only FUTURE booked leave reduces the balance - leave already taken has
//     been deducted by UKG, so counting it again would double-charge it;
//   * entitlement is role-driven (SM/ASM/DSM 5 weeks, everyone else 4), which
//     is what separates the two 70-hour bands;
//   * casuals accrue nothing and are excluded upstream.

using Roster.Engine.Core;

namespace Roster.Engine.Leave;

/// <param name="LeaveNotional">Leave notional used to convert hours ↔ days.</param>
/// <param name="CurrentLiabilityHours">
/// Annual + lieu as at the snapshot - the employee's share of the store's
/// liability. Lieu is signed: positive is owed to them, negative means they
/// owe hours back.
/// </param>
/// <param name="PlannedDays">Approved future leave DAYS booked between today and 31 Dec.</param>
/// <param name="PlannedHours">Those days priced at the leave notional.</param>
/// <param name="EoyAccrualHours">Hours that will accrue between today and 31 Dec inclusive.</param>
/// <param name="EoyBalanceHours">
/// current liability − planned + still-to-accrue. Lieu carries forward flat:
/// it is earned by working extra hours, not by the calendar, so projecting it
/// would be inventing hours nobody has worked.
/// </param>
/// <param name="EoyBalanceWeeks">The balance in weeks of leave days.</param>
/// <param name="DaysToTake">
/// Leave DAYS the employee still needs to book this year to land at or under
/// the target. Zero when they are already at or below it.
/// </param>
/// <param name="Unbanded">True when no band matched - every figure above is then unreliable.</param>
public sealed record Projection(
    double LeaveNotional,
    double CurrentLiabilityHours,
    double PlannedDays,
    double PlannedHours,
    double EoyAccrualHours,
    double EoyBalanceHours,
    double EoyBalanceWeeks,
    double DaysToTake,
    bool Unbanded);

public static class LeaveAccrual
{
    /// <summary>Whole days from <paramref name="from"/> to <paramref name="to"/>, inclusive of both ends. Never negative.</summary>
    public static int DaysInclusive(DateOnly from, DateOnly to) => Math.Max(0, to.DayNumber - from.DayNumber + 1);

    /// <summary>31 December of the year <paramref name="iso"/> falls in.</summary>
    public static DateOnly EndOfYear(DateOnly iso) => new(iso.Year, 12, 31);

    /// <summary>
    /// The band for an employee. Falls back to the closest band of the same role
    /// class with a SMALLER contract (never over-crediting someone), and returns
    /// null when the role class has no bands at all - the caller surfaces that as a
    /// visible "unbanded" warning rather than a silent zero.
    /// </summary>
    public static AccrualBand? BandFor(IReadOnlyList<AccrualBand> bands, double contractHours, string roleClass)
    {
        var sameRole = bands.Where(b => b.RoleClass == roleClass).ToList();
        var exact = sameRole.FirstOrDefault(b => b.ContractHours == contractHours);
        if (exact != null) return exact;
        var smaller = Js.Sorted(
            sameRole.Where(b => b.ContractHours < contractHours),
            (a, b) => b.ContractHours - a.ContractHours);
        return smaller.Count > 0 ? smaller[0] : null;
    }

    /// <summary>Accrual earned per calendar day, from the band's annual entitlement.</summary>
    public static double DailyAccrualHours(AccrualBand band) => band.AnnualAccrualHours / 365;

    /// <summary>
    /// Project one employee to 31 December.
    ///
    /// Worked example (90h SM, 135.67h annual + (12.75)h lieu, 6 future AL days,
    /// viewed 8 Aug):
    ///   leave notional 9.00, daily accrual 225/365 = 0.6164
    ///   liability    = 135.67 − 12.75           = 122.92h
    ///   planned      = 6 × 9.00                 = 54.00h
    ///   eoy accrual  = 146 days × 0.6164        = 90.00h
    ///   eoy balance  = 122.92 − 54.00 + 90.00   = 158.92h = 3.5 weeks
    /// </summary>
    public static Projection ProjectEmployee(
        LiabilityRow row,
        IReadOnlyList<AccrualBand> bands,
        double plannedDays,
        DateOnly today,
        double targetHoursPerFte)
    {
        var band = BandFor(bands, row.ContractHours, row.RoleClass);
        // No band: fall back to the file's true notional so the row still renders
        // something honest, and flag it.
        var leaveNotional = band?.LeaveNotionalHours ?? row.NotionalHours;
        var daily = band != null ? DailyAccrualHours(band) : 0;

        var currentLiabilityHours = row.AnnualHours + row.LieuHours;
        var plannedHours = plannedDays * leaveNotional;
        var eoyAccrualHours = DaysInclusive(today, EndOfYear(today)) * daily;
        var eoyBalanceHours = currentLiabilityHours - plannedHours + eoyAccrualHours;
        var eoyBalanceWeeks = leaveNotional > 0 ? eoyBalanceHours / leaveNotional / 5 : 0;
        var daysToTake =
            leaveNotional > 0 ? Math.Max(0, (eoyBalanceHours - targetHoursPerFte) / leaveNotional) : 0;

        return new Projection(
            LeaveNotional: leaveNotional,
            CurrentLiabilityHours: currentLiabilityHours,
            PlannedDays: plannedDays,
            PlannedHours: plannedHours,
            EoyAccrualHours: eoyAccrualHours,
            EoyBalanceHours: eoyBalanceHours,
            EoyBalanceWeeks: eoyBalanceWeeks,
            DaysToTake: daysToTake,
            Unbanded: band == null);
    }
}
