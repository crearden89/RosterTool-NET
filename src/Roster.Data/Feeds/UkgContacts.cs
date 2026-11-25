// Port of src/lib/csv/ukgContacts.ts.
// ─── UKG Contact Details export → team members ───────────────────────────────
// The fourth UKG export the app ingests, and the one that gets a store SET UP:
// one row per employee with name, job, contract and hire date. Names arrive as
// "4-Nye, Harper" (rank prefix + surname, firstname) and are KEPT in that
// format - it is the format the business reads every day in UKG, and the rank
// prefix is what orders the Team Members list.
//
// Same file shape as the leave exports (metadata preamble, then the real
// header row), so the shared ParseUkgExport does the stripping.

using Roster.Engine.Core;
using static Roster.Data.Feeds.CsvParse;

namespace Roster.Data.Feeds;

public static class ContactHeaders
{
    public const string StoreCode = "StoreID";
    public const string Location = "Location";
    public const string EmployeeName = "Employee Name";
    public const string EmployeeId = "Employee ID";
    public const string Job = "Job";
    public const string Contract = "Contract Hrs";
    public const string Notional = "Notional Hrs";
    public const string BirthDate = "Birth Date";
    public const string HireDate = "Hire Date";
}

/// <param name="Row">Line in the export, for error reports.</param>
/// <param name="Name">Verbatim UKG name, e.g. "4-Nye, Harper" - kept in this format.</param>
/// <param name="Role">The app role (Roles.SM ... Roles.JSA).</param>
/// <param name="ContractHours">Per fortnight, exactly as UKG quotes it (90 = full-time SM).</param>
/// <param name="NotionalHours">Per day.</param>
/// <param name="BirthDate">Null when UKG has no birth date recorded.</param>
/// <param name="HireDate">The employee's start date.</param>
public sealed record ContactRow(
    int Row,
    string StoreCode,
    string UkgEmployeeId,
    string Name,
    string Job,
    string Role,
    double ContractHours,
    double NotionalHours,
    DateOnly? BirthDate,
    DateOnly HireDate) : IFeedRow;

public static class UkgContacts
{
    /// <summary>
    /// UKG Job title → app role. Order matters: "ST ASSIST MANAGER" contains both
    /// ASSIST and MANAGER, and "ST DUTY STORE MANAGER" contains MANAGER too.
    /// </summary>
    public static string? RoleFromJob(string job)
    {
        var j = JsText.ToUpper(job);
        if (j.Length == 0) return null;
        if (j.Contains("CASUAL", StringComparison.Ordinal)) return Roles.JSA;
        if (j.Contains("DUTY", StringComparison.Ordinal)) return Roles.DSM;
        if (j.Contains("ASSIST MANAGER", StringComparison.Ordinal)) return Roles.ASM;
        if (j.Contains("MANAGER", StringComparison.Ordinal)) return Roles.SM;
        if (j.Contains("ASSIST", StringComparison.Ordinal)) return Roles.SA;
        return null;
    }

    /// <summary>Fallback when the Job cell is blank/unknown: the name's rank prefix.</summary>
    public static string RoleFromNamePrefix(string name)
    {
        var prefix = Js.Trim(name.Split('-')[0]);
        if (prefix.StartsWith('1')) return Roles.SM;
        if (prefix.StartsWith('2')) return Roles.ASM;
        if (prefix.StartsWith('3')) return Roles.DSM;
        if (prefix.StartsWith('6')) return Roles.JSA;
        return Roles.SA; // 4/5 and lettered variants (4L/5L) are Store Assistants
    }

    /// <summary>Whole years old on <paramref name="asOf"/>. Null when the birth date is unknown.</summary>
    private static int? AgeOn(DateOnly? birth, DateOnly asOf)
    {
        if (birth is not { } b) return null;
        var age = asOf.Year - b.Year;
        if (asOf.Month < b.Month || (asOf.Month == b.Month && asOf.Day < b.Day)) age -= 1;
        return age;
    }

    /// <summary>
    /// The position a fresh import lands on. Managers get the contract-size band
    /// nearest their fortnightly hours at YEAR 1 - the export carries no tenure
    /// year, so the year band is corrected by hand afterwards (same nearest-band
    /// shape as the 0020 backfill). JSAs pick 16/17 from their birth date; an
    /// unknown birth date lands on 16 rather than inventing an age.
    /// </summary>
    public static string PositionForImport(string role, double fortnightHours, DateOnly? birthDate, DateOnly asOf)
    {
        var weekly = fortnightHours / 2;
        switch (role)
        {
            case Roles.SM:
                return $"SM-{(weekly < 42.5 ? 40 : weekly < 47.5 ? 45 : 50)}-1";
            case Roles.ASM:
                return $"ASM-{(weekly < 31 ? 30 : weekly < 33.5 ? 32 : weekly < 37.5 ? 35 : weekly < 42.5 ? 40 : 45)}-1";
            case Roles.DSM:
                return "DSM";
            case Roles.RSM:
                return "RSM";
            case Roles.JSA:
            {
                var age = AgeOn(birthDate, asOf);
                return age is >= 17 ? "JSA-17" : "JSA-16";
            }
            default:
                return "SA-FF";
        }
    }

    public static ValidationResult<ContactRow> ValidateContacts(string text, IReadOnlySet<string> validStoreCodes)
    {
        // Anchor on Hire Date - present in this export's header row and absent from
        // the leave exports, so dropping the wrong UKG file here fails loudly.
        var rows = UkgLeave.ParseUkgExport(text, ContactHeaders.HireDate).Rows;
        var validRows = new List<ContactRow>();
        var errors = new List<RowError>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2;
            var storeCode = JsText.ToUpper(UkgLeave.Get(r, ContactHeaders.StoreCode));
            var name = UkgLeave.Get(r, ContactHeaders.EmployeeName);
            var empId = UkgLeave.Get(r, ContactHeaders.EmployeeId);
            if (name.Length == 0 && empId.Length == 0) continue; // trailing blank line

            if (storeCode.Length == 0)
            {
                errors.Add(new RowError(line, null, ContactHeaders.StoreCode, "Missing StoreID"));
                continue;
            }
            if (!validStoreCodes.Contains(storeCode))
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.StoreCode,
                    $"Unknown store code '{storeCode}' - add the store under Store Attributes first"));
                continue;
            }
            if (empId.Length == 0)
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.EmployeeId, "Missing Employee ID"));
                continue;
            }
            if (name.Length == 0)
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.EmployeeName, "Missing Employee Name"));
                continue;
            }

            var job = UkgLeave.Get(r, ContactHeaders.Job);
            var role = RoleFromJob(job) ?? RoleFromNamePrefix(name);

            var contractHours = UkgLeave.UkgNumber(UkgLeave.Get(r, ContactHeaders.Contract));
            if (!double.IsFinite(contractHours) || contractHours < 0)
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.Contract, "Contract Hrs is not a number"));
                continue;
            }
            var notionalHours = UkgLeave.UkgNumber(UkgLeave.Get(r, ContactHeaders.Notional));
            if (!double.IsFinite(notionalHours) || notionalHours < 0 || notionalHours > 24)
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.Notional, "Notional Hrs is not a valid daily figure"));
                continue;
            }

            // The hire date IS the start date - the point of the import - so a row
            // without one is rejected rather than silently imported as "no tenure".
            var hireDate = ToIsoDate(UkgLeave.Get(r, ContactHeaders.HireDate));
            if (hireDate is null)
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.HireDate,
                    "Unparseable or impossible Hire Date (expected DD/MM/YYYY)"));
                continue;
            }
            // Birth date is only used to pick JSA 16 vs 17 - blank is fine, but a date
            // that cannot exist is a typo, not "unknown", and would quietly pick 16.
            var rawBirth = Js.Trim(UkgLeave.Get(r, ContactHeaders.BirthDate));
            var birthDate = ToIsoDate(rawBirth);
            if (rawBirth.Length > 0 && birthDate is null)
            {
                errors.Add(new RowError(line, storeCode, ContactHeaders.BirthDate,
                    $"Unparseable or impossible Birth Date '{rawBirth}' (expected DD/MM/YYYY)"));
                continue;
            }

            validRows.Add(new ContactRow(
                Row: line,
                StoreCode: storeCode,
                UkgEmployeeId: empId,
                Name: Js.Trim(name),
                Job: job,
                Role: role,
                ContractHours: contractHours,
                NotionalHours: notionalHours,
                BirthDate: birthDate is null ? null : Date(birthDate),
                HireDate: Date(hireDate)));
        }

        if (validRows.Count == 0 && errors.Count == 0)
        {
            return new ValidationResult<ContactRow>(0, [], [],
            [
                "No data rows found. Check this is the UKG Contact Details export and that it carries StoreID and Hire Date columns.",
            ]);
        }
        // One team member per UKG ID per store. Two rows for the same ID used to
        // pass, and the second failed the database's unique index after the rest of
        // the team had already been updated.
        var dupes = Duplicates.CheckDuplicates(
            validRows,
            r => $"{r.StoreCode}|{r.UkgEmployeeId}",
            r => $"Employee ID {r.UkgEmployeeId} at {r.StoreCode}",
            ContactHeaders.EmployeeId);
        return new ValidationResult<ContactRow>(
            validRows.Count + errors.Count,
            dupes.Rows,
            Duplicates.ByRow([.. errors, .. dupes.Errors]),
            [],
            dupes.Collapsed);
    }
}
