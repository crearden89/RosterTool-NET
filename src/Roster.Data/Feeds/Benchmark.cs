// Port of src/lib/csv/benchmark.ts.

using Roster.Engine.Core;
using static Roster.Data.Feeds.CsvParse;

namespace Roster.Data.Feeds;

// Column header map for the real Benchmark export. The file carries dozens of
// reference-only columns (region/store descriptors, historical mean/stddev
// figures, case-shipment stats, etc.) that the engine doesn't need - this map
// picks out just the ones we ingest. StoreID is a bare store number or store
// code; it's normalised to the GXXX store code via FormatStoreId below.
public static class BenchmarkColumns
{
    public const string StoreCode = "StoreID";
    public const string Date = "Date";
    public const string IsSco = "Has_Sco";
    public const string SalesForecast = "Retail_Sales";
    public const string Items = "Total_Items1";
    public const string CustomerCount = "Customer_Count";
    public const string LoadRunning = "LoadRunning_Hours";
    public const string Cleaning = "Cleaning_Hours";
    public const string Specials = "Specials_Hours";
    public const string Reset = "LoadReset_Hours";
    public const string Admin = "AdminMgmt_Hours";
    // The day's total checkout labour budget - no longer split Registers/SCO
    // (the file still carries Registers_Hours/SCOs_Hours but they're reference-
    // only now). The Till Allocation Engine distributes this across the day's
    // quarter-hour blocks via the Quarter-Hour file's Allocation% (see lib/till).
    public const string CheckoutHours = "Checkout_Hours";

    /// <summary><c>Object.values(BENCHMARK_COLUMNS)</c>: every required column, in order.</summary>
    public static readonly IReadOnlyList<string> All =
        [StoreCode, Date, IsSco, SalesForecast, Items, CustomerCount, LoadRunning, Cleaning, Specials, Reset, Admin, CheckoutHours];

    // Present on the current export but OPTIONAL here so older files still load.
    // Sizes the formula-driven admin tasks (UKG pay-period/rostering, SPi
    // planning, Till Calibration) - a missing column just means those fall back
    // to their minimum durations.
    public const string EmployeeCount = "EmployeeCount";
}

/// <summary>One store-day of the Benchmark file. Hours are at 100%; <see cref="EmployeeCount"/> is whole.</summary>
public sealed record BenchmarkRow(
    int Row,
    string StoreCode,
    DateOnly Date,
    bool IsSco,
    double SalesForecast,
    double Items,
    double CustomerCount,
    double LrHours100,
    double CleaningHours100,
    double SpecialsHours100,
    double ResetHours100,
    double AdminHours100,
    double CheckoutHours100,
    double EmployeeCount,
    // Optional file-carried flags (null = column absent from this upload).
    bool? IsMhs,
    bool? HasEsl,
    bool? HasBaler) : IFeedRow;

public static class Benchmark
{
    // Labour-group columns that carry 100% hours and get the ÷0.9 transform.
    private static readonly (string Key, string Col)[] HourFields =
    [
        ("loadRunning", BenchmarkColumns.LoadRunning),
        ("cleaning", BenchmarkColumns.Cleaning),
        ("specials", BenchmarkColumns.Specials),
        ("reset", BenchmarkColumns.Reset),
        ("admin", BenchmarkColumns.Admin),
        ("checkout", BenchmarkColumns.CheckoutHours),
    ];

    // Validate a benchmark CSV. `validStoreCodes` restricts store IDs to known
    // stores (referential check, spec 4.4).
    public static ValidationResult<BenchmarkRow> ValidateBenchmark(string text, IReadOnlySet<string> validStoreCodes)
    {
        var (headers, rows) = ParseCsv(text);
        var errors = new List<RowError>();
        var fileErrors = new List<string>();
        var validRows = new List<BenchmarkRow>();

        var missing = MissingHeaders(headers, BenchmarkColumns.All);
        if (missing.Count > 0)
        {
            fileErrors.Add($"Missing required column(s): {string.Join(", ", missing)}");
            return new ValidationResult<BenchmarkRow>(rows.Count, validRows, errors, fileErrors);
        }

        var seenDates = new List<DateOnly>();

        for (var i = 0; i < rows.Count; i++)
        {
            var raw = rows[i];
            var rowNo = i + 1;
            // The file's StoreID is a bare number (e.g. "25"); normalise to the store's
            // canonical GXXX code so it matches the stores.code the rest of the app uses.
            var storeCode = StoreIds.FormatStoreId(Js.Trim(Cell(raw, BenchmarkColumns.StoreCode) ?? ""));
            var bad = false;
            void Flag(string field, string message)
            {
                errors.Add(new RowError(rowNo, storeCode, field, message));
                bad = true;
            }

            if (storeCode.Length == 0) Flag(BenchmarkColumns.StoreCode, "Missing store ID");
            else if (!validStoreCodes.Contains(storeCode))
                Flag(BenchmarkColumns.StoreCode, $"Store ID '{storeCode}' not recognised");

            var iso = ToIsoDate(Cell(raw, BenchmarkColumns.Date));
            if (iso is null) Flag(BenchmarkColumns.Date, $"Unparseable date '{Cell(raw, BenchmarkColumns.Date) ?? ""}'");
            else seenDates.Add(Date(iso));

            var hours = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, col) in HourFields)
            {
                var v = Num(raw, col);
                if (double.IsNaN(v)) Flag(col, "Missing/invalid number");
                else if (v < 0) Flag(col, $"Negative hours not allowed ({Js.NumberToString(v)})");
                hours[key] = v;
            }

            var salesForecast = Num(raw, BenchmarkColumns.SalesForecast);
            var items = Num(raw, BenchmarkColumns.Items);
            var customerCount = Num(raw, BenchmarkColumns.CustomerCount);
            if (double.IsNaN(salesForecast) || salesForecast < 0)
                Flag(BenchmarkColumns.SalesForecast, "Missing/invalid sales forecast");
            if (double.IsNaN(customerCount) || customerCount < 0)
                Flag(BenchmarkColumns.CustomerCount, "Missing/invalid customer count");

            // Optional column: blank/absent → 0; a value present must be a whole
            // non-negative headcount.
            var hasEmpCol = headers.Contains(BenchmarkColumns.EmployeeCount);
            var empCell = hasEmpCol ? Cell(raw, BenchmarkColumns.EmployeeCount) : null;
            var empRaw = empCell is null ? null : Js.Trim(empCell);
            double employeeCount = 0;
            if (!string.IsNullOrEmpty(empRaw))
            {
                var v = ParseDecimal(empRaw);
                if (!double.IsFinite(v) || v < 0) Flag(BenchmarkColumns.EmployeeCount, $"Invalid employee count '{empRaw}'");
                else employeeCount = Js.Round(v);
            }

            if (bad || iso is null) continue;

            validRows.Add(new BenchmarkRow(
                Row: rowNo,
                StoreCode: storeCode,
                Date: Date(iso),
                IsSco: ToBool(Cell(raw, BenchmarkColumns.IsSco)),
                SalesForecast: salesForecast,
                Items: double.IsNaN(items) ? 0 : items,
                CustomerCount: customerCount,
                LrHours100: hours["loadRunning"],
                CleaningHours100: hours["cleaning"],
                SpecialsHours100: hours["specials"],
                ResetHours100: hours["reset"],
                AdminHours100: hours["admin"],
                CheckoutHours100: hours["checkout"],
                EmployeeCount: employeeCount,
                IsMhs: OptionalBool(raw, headers, "Is_Mhs"),
                HasEsl: OptionalBool(raw, headers, "Has_Esl"),
                HasBaler: OptionalBool(raw, headers, "Has_Baler1")));
        }

        // Fortnight span check across the valid dates (spec 4.1: 14 days per store).
        if (seenDates.Count > 0)
        {
            var spanError = Fortnight.ValidateFortnightSpan(seenDates);
            if (spanError is not null) fileErrors.Add(spanError);
        }

        var dupes = Duplicates.CheckDuplicates(
            validRows,
            r => $"{r.StoreCode}|{r.Date.ToIso()}",
            r => $"{r.StoreCode} on {r.Date.ToIso()}",
            BenchmarkColumns.Date);
        return new ValidationResult<BenchmarkRow>(
            rows.Count,
            dupes.Rows,
            Duplicates.ByRow([.. errors, .. dupes.Errors]),
            fileErrors,
            dupes.Collapsed);
    }

    // Convert a validated row to a DB record. The ÷0.9 (90%) columns are computed by
    // the DB as generated columns, but we expose the helper for UI traceability.
    public static double To90(double hours100) => hours100 / PlanningConstants.PlanningFactor;

    public static DerivedPeriod? BenchmarkPeriod(IReadOnlyList<BenchmarkRow> rows) =>
        Fortnight.DerivePeriod(rows.Select(r => r.Date).ToList());
}
