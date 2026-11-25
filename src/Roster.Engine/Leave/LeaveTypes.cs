// Port of src/lib/leave/types.ts.
// ─── Leave liability: shared shapes ──────────────────────────────────────────

namespace Roster.Engine.Leave;

/// <summary>
/// Entitlement class, derived from UKG's Job title. The distinction matters for
/// two reasons: SM/ASM/DSM accrue 5 weeks of annual leave against everyone
/// else's 4, and a 70-hour contract resolves to a different leave notional for
/// a DSM (7.50) than for a Store Assistant (7.25).
///
/// CASUAL is carried so a row is explainable rather than silently dropped, but
/// casuals accrue no leave and are excluded from every figure on the page.
///
/// The codes are the strings the database stores (leave_liability_snapshots.role_class),
/// so a row's role class is a plain string, exactly as in the TypeScript.
/// </summary>
public static class RoleClass
{
    public const string SM = "SM";
    public const string ASM = "ASM";
    public const string DSM = "DSM";
    public const string SA = "SA";
    public const string CASUAL = "CASUAL";

    /// <summary>Every role class, in the TypeScript union's order.</summary>
    public static readonly IReadOnlyList<string> All = [SM, ASM, DSM, SA, CASUAL];

    /// <summary><c>MANAGER_CLASSES</c>.</summary>
    public static readonly IReadOnlyList<string> ManagerClasses = [SM, ASM, DSM];
}

/// <summary>
/// Calendar cell codes, in the precedence order the workbook applies. A cell
/// with no live booking is the empty string.
/// </summary>
public static class LeaveCode
{
    public const string AL = "AL";
    public const string S = "S";
    public const string SIC = "SIC";
    public const string UP = "UP";
    public const string None = "";
}

/// <summary>Anything that carries a UKG employee name (what <see cref="LeaveMetrics.TopBy{T}"/> breaks ties on).</summary>
public interface IEmployeeNamed
{
    string EmployeeName { get; }
}

/// <param name="ContractHours">Fortnightly contract hours the band is for.</param>
/// <param name="RoleClass">One of <see cref="Leave.RoleClass"/>'s codes.</param>
/// <param name="LeaveNotionalHours">
/// What a LEAVE DAY is worth in hours. Deliberately distinct from the
/// employee's true notional on the liability file - they diverge for
/// part-timers (a 30h contract is 5.50 true, 4.75 for leave).
/// </param>
/// <param name="AccrualWeeks">Weeks of annual leave a year.</param>
/// <param name="AnnualAccrualHours">Hours of annual leave a year.</param>
public sealed record AccrualBand(
    double ContractHours,
    string RoleClass,
    double LeaveNotionalHours,
    double AccrualWeeks,
    double AnnualAccrualHours);

/// <summary>One employee's balances as at a pay period end.</summary>
public record LiabilityRow : IEmployeeNamed
{
    public string UkgEmployeeId { get; init; } = "";
    public string EmployeeName { get; init; } = "";
    public string? Job { get; init; }
    /// <summary>One of <see cref="Leave.RoleClass"/>'s codes.</summary>
    public string RoleClass { get; init; } = Leave.RoleClass.SA;
    public double ContractHours { get; init; }
    /// <summary>True notional from the file (pay/roster basis, not the leave basis).</summary>
    public double NotionalHours { get; init; }
    public double AnnualHours { get; init; }
    public double SickHours { get; init; }
    public double LieuHours { get; init; }
    public double BankedHours { get; init; }
    public double Banked2Hours { get; init; }
}

/// <summary>One UKG leave booking.</summary>
/// <param name="EndDate">Inclusive.</param>
public sealed record LeaveRequestRow(
    string UkgEmployeeId,
    string EmployeeName,
    string LeaveType,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status);
