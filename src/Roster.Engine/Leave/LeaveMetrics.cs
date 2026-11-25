// Port of src/lib/leave/metrics.ts.
// ─── Store-level leave liability metrics (pure) ──────────────────────────────
// The workbook's headline numbers:
//
//   FTE               = Σ contract hours ÷ 76
//   Liability per FTE = Σ annual leave hours ÷ FTE      (target 76)
//
// Casuals (contract 0, no accrual) are excluded from BOTH sides, as confirmed -
// they would otherwise drag the per-FTE figure without ever contributing leave.

using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Engine.Leave;

/// <param name="Headcount">Accruing employees only.</param>
/// <param name="CasualCount">Excluded, but reported so the omission is visible.</param>
/// <param name="TotalLieuHours">
/// Lieu, SIGNED. Positive lieu is hours owed TO the employee and is liability
/// like any other; negative lieu means they owe hours back and genuinely
/// reduces it. In the live network file both directions occur (110 positive,
/// 75 negative), so flooring at zero would overstate the network by the whole
/// negative side.
/// </param>
/// <param name="TotalLiabilityHours">annual + lieu - the two components that make up leave liability.</param>
/// <param name="HoursPerFte">Σ liability hours ÷ FTE. Null when the store has no FTE at all.</param>
/// <param name="VsTarget">Signed gap to the target: positive = over-accrued.</param>
public sealed record StoreLiability(
    int Headcount,
    int CasualCount,
    double TotalAnnualHours,
    double TotalLieuHours,
    double TotalLiabilityHours,
    double Fte,
    double? HoursPerFte,
    double? VsTarget);

public static partial class LeaveMetrics
{
    /// <summary>UKG Job title → entitlement class (a <see cref="RoleClass"/> code).</summary>
    public static string RoleClassFromJob(string? job)
    {
        var j = (job ?? "").ToUpperInvariant();
        if (j.Contains("CASUAL", StringComparison.Ordinal)) return RoleClass.CASUAL;
        if (j.Contains("DUTY", StringComparison.Ordinal)) return RoleClass.DSM;
        if (j.Contains("ASSIST MANAGER", StringComparison.Ordinal)) return RoleClass.ASM;
        if (j.Contains("MANAGER", StringComparison.Ordinal)) return RoleClass.SM; // incl. "ST MANAGER EXTENDED LEAVE"
        return RoleClass.SA;
    }

    /// <summary>
    /// Fallback for a missing/unknown Job: UKG names carry a rank prefix
    /// ("1-Smith, Jo" = SM, 2 = ASM, 3 = DSM, 4/5 = SA, 6C = casual). Suffixed
    /// variants like "4L"/"5L" are still Store Assistants.
    /// </summary>
    public static string RoleClassFromName(string name)
    {
        // name.split("-")[0] always exists, so the TypeScript's ?? "" never applies.
        var dash = name.IndexOf('-');
        var prefix = Js.Trim(dash < 0 ? name : name[..dash]).ToUpperInvariant();
        if (prefix.StartsWith('6')) return RoleClass.CASUAL;
        if (prefix.StartsWith('1')) return RoleClass.SM;
        if (prefix.StartsWith('2')) return RoleClass.ASM;
        if (prefix.StartsWith('3')) return RoleClass.DSM;
        return RoleClass.SA;
    }

    /// <summary>Rows that count toward liability: everyone who actually accrues leave.</summary>
    public static List<T> Accruing<T>(IEnumerable<T> rows) where T : LiabilityRow =>
        rows.Where(r => r.RoleClass != RoleClass.CASUAL && r.ContractHours > 0).ToList();

    /// <summary>
    /// Leave liability is annual leave PLUS lieu, and nothing else. Sick, carers,
    /// bereavement and long service accrue but are not a liability the business
    /// carries in this measure - they are visible on the calendar once requested,
    /// and stop there.
    /// </summary>
    public static StoreLiability StoreLiability(
        IReadOnlyList<LiabilityRow> rows,
        double fteDivisor,
        double targetHoursPerFte)
    {
        var live = Accruing(rows);
        // Left folds from 0, in row order, as the TypeScript's reduce adds them.
        double totalAnnualHours = 0;
        foreach (var r in live) totalAnnualHours += r.AnnualHours;
        double totalLieuHours = 0;
        foreach (var r in live) totalLieuHours += r.LieuHours;
        var totalLiabilityHours = totalAnnualHours + totalLieuHours;
        double fte = 0;
        if (fteDivisor > 0)
        {
            double contract = 0;
            foreach (var r in live) contract += r.ContractHours;
            fte = contract / fteDivisor;
        }
        double? hoursPerFte = fte > 0 ? totalLiabilityHours / fte : null;
        return new StoreLiability(
            Headcount: live.Count,
            CasualCount: rows.Count - live.Count,
            TotalAnnualHours: totalAnnualHours,
            TotalLieuHours: totalLieuHours,
            TotalLiabilityHours: totalLiabilityHours,
            Fte: fte,
            HoursPerFte: hoursPerFte,
            VsTarget: hoursPerFte == null ? null : hoursPerFte - targetHoursPerFte);
    }

    /// <summary>
    /// Period-on-period movement, as a fraction (0.05 = 5% worse). Null when there
    /// is no prior period to compare with, or the prior period was zero.
    /// </summary>
    public static double? LiabilityChange(double? current, double? previous)
    {
        if (current == null || previous == null || previous == 0) return null;
        return current / previous - 1;
    }

    /// <summary>Top N by a numeric key, descending. Ties broken by name for stability.</summary>
    public static List<T> TopBy<T>(IEnumerable<T> rows, Func<T, double> value, int n = 5) where T : IEmployeeNamed =>
        Js.Sorted(rows, (a, b) => (value(b) - value(a)).Or(Js.LocaleCompare(a.EmployeeName, b.EmployeeName)))
            .Take(n)
            .ToList();

    /// <summary>
    /// UKG names carry a rank prefix ("1-Smith, Jo"); strip it for display surfaces
    /// that already show the person's role another way. Kept here (not in a page
    /// file) because every UKG-fed surface - leave, variance, contacts - needs it.
    /// </summary>
    public static string DisplayName(string name) => RankPrefix().Replace(name, "", 1);

    // /^[0-9]+[A-Z]?-\s*/i - in JavaScript's (non-unicode) case folding [A-Z]/i is
    // exactly [A-Za-z], and \s is JavaScript's whitespace set, spelled out because
    // .NET's \s differs (it adds U+0085 and drops U+FEFF).
    [GeneratedRegex(@"^[0-9]+[A-Za-z]?-[\t\n\v\f\r    -     　﻿]*", RegexOptions.CultureInvariant)]
    private static partial Regex RankPrefix();
}
