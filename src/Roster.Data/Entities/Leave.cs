namespace Roster.Data.Entities;

// ─── Leave liability (0028) ───────────────────────────────────────────────────

public static class RoleClasses
{
    public const string Sm = "SM", Asm = "ASM", Dsm = "DSM", Sa = "SA", Casual = "CASUAL";
    public static readonly string[] All = [Sm, Asm, Dsm, Sa, Casual];
    public static readonly string[] Banded = [Sm, Asm, Dsm, Sa];
}

/// <summary>One employee's balances as at a pay-period end. Re-uploading a period supersedes it.</summary>
public class LeaveLiabilitySnapshot
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string UkgEmployeeId { get; set; } = "";
    public string? UkgLocation { get; set; }
    public string EmployeeName { get; set; } = "";
    public string? Job { get; set; }
    public string RoleClass { get; set; } = RoleClasses.Sa;
    public double ContractHours { get; set; }
    public double NotionalHours { get; set; }
    public double SickHours { get; set; }
    public double AnnualHours { get; set; }
    public double LieuHours { get; set; }
    public double BankedHours { get; set; }
    public double Banked2Hours { get; set; }
    public Guid? BatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The rolling-year booking ledger; replaced per store on each upload.</summary>
public class LeaveRequestUkg
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string UkgEmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string LeaveType { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "";
    public string? Approver { get; set; }
    public Guid? BatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Accrual bands keyed on (contract hours, role class) — the workbook's Setup sheet.</summary>
public class LeaveAccrualBand
{
    public double ContractHours { get; set; }
    public string RoleClass { get; set; } = RoleClasses.Sa;
    public double LeaveNotionalHours { get; set; }
    public double AccrualWeeks { get; set; }
    public double AnnualAccrualHours { get; set; }
}

/// <summary>Single-row leave KPI settings.</summary>
public class LeaveSettings
{
    public bool Id { get; set; } = true;
    public double TargetHoursPerFte { get; set; } = 76.0;
    public double FteDivisor { get; set; } = 76.0;
    public DateTimeOffset UpdatedAt { get; set; }
}
