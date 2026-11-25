// Port of src/lib/csv/actuals.ts.

using Roster.Engine.Core;
using static Roster.Data.Feeds.CsvParse;

namespace Roster.Data.Feeds;

// Column header map for the Actuals file (spec 4.2). Uploaded daily, per store;
// incremental upsert per store/day (not a full-fortnight replace).
public static class ActualsColumns
{
    public const string StoreCode = "StoreID";
    public const string Date = "Period";
    public const string ActualSales = "Retail_Sales";
    public const string ActualItems = "Total_Items";
    public const string ActualCustomers = "Customer_Count";
    public const string UkgActualHours = "Kronos_Actual_Hours";

    /// <summary><c>Object.values(ACTUALS_COLUMNS)</c>, in order.</summary>
    public static readonly IReadOnlyList<string> All = [StoreCode, Date, ActualSales, ActualItems, ActualCustomers, UkgActualHours];

    // Actual hours arrive SPLIT BY LABOUR CATEGORY - one column each, on the same
    // row - rather than as a single day total. They're named exactly as in the
    // Benchmark export (same warehouse), and the day's actual hours are their sum.
    //
    // All six are required: a silently absent column would under-count the day's
    // hours and quietly overstate performance. Missing one fails the upload by
    // name instead, which says precisely what to fix.
    public static readonly IReadOnlyList<string> HourCategories =
        ["LoadRunning_Hours", "Cleaning_Hours", "Specials_Hours", "LoadReset_Hours", "AdminMgmt_Hours", "Checkout_Hours"];
}

/// <param name="CategoryHours">
/// Per-category actual hours, keyed by the export's column name. Kept so the daily variance
/// report can compare each category with its benchmark.
/// </param>
public sealed record ActualsRow(
    int Row,
    string StoreCode,
    DateOnly Date,
    double ActualSales,
    double ActualItems,
    double ActualCustomers,
    double ActualHours100,
    double UkgActualHours,
    IReadOnlyDictionary<string, double> CategoryHours,
    // Optional file-carried flags (null = column absent from this upload).
    bool? IsMhs,
    bool? HasEsl,
    bool? HasBaler) : IFeedRow;

public static class Actuals
{
    public static ValidationResult<ActualsRow> ValidateActuals(string text, IReadOnlySet<string> validStoreCodes)
    {
        var (headers, rows) = ParseCsv(text);
        var errors = new List<RowError>();
        var fileErrors = new List<string>();
        var validRows = new List<ActualsRow>();

        var missing = MissingHeaders(headers, [.. ActualsColumns.All, .. ActualsColumns.HourCategories]);
        if (missing.Count > 0)
        {
            fileErrors.Add($"Missing required column(s): {string.Join(", ", missing)}");
            return new ValidationResult<ActualsRow>(rows.Count, validRows, errors, fileErrors);
        }

        (string Key, string Col)[] fields =
        [
            ("actualSales", ActualsColumns.ActualSales),
            ("actualItems", ActualsColumns.ActualItems),
            ("actualCustomers", ActualsColumns.ActualCustomers),
            ("ukgActualHours", ActualsColumns.UkgActualHours),
        ];

        for (var i = 0; i < rows.Count; i++)
        {
            var raw = rows[i];
            var rowNo = i + 1;
            var storeCode = StoreIds.FormatStoreId(Js.Trim(Cell(raw, ActualsColumns.StoreCode) ?? ""));
            var bad = false;
            void Flag(string field, string message)
            {
                errors.Add(new RowError(rowNo, storeCode, field, message));
                bad = true;
            }

            if (storeCode.Length == 0) Flag(ActualsColumns.StoreCode, "Missing store ID");
            else if (!validStoreCodes.Contains(storeCode))
                Flag(ActualsColumns.StoreCode, $"Store ID '{storeCode}' not recognised");

            var iso = ToIsoDate(Cell(raw, ActualsColumns.Date));
            if (iso is null) Flag(ActualsColumns.Date, "Unparseable date");

            var vals = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, col) in fields)
            {
                var v = Num(raw, col);
                if (double.IsNaN(v)) Flag(col, "Missing/invalid number");
                else if (v < 0) Flag(col, $"Negative value not allowed ({Js.NumberToString(v)})");
                vals[key] = v;
            }

            // The day's actual hours = the sum of the per-category columns. Each is
            // checked on its own so an error names the offending category rather than
            // reporting one opaque bad total.
            double actualHours100 = 0;
            var categoryHours = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var col in ActualsColumns.HourCategories)
            {
                var v = Num(raw, col);
                if (double.IsNaN(v)) Flag(col, "Missing/invalid number");
                else if (v < 0) Flag(col, $"Negative hours not allowed ({Js.NumberToString(v)})");
                else
                {
                    actualHours100 += v;
                    categoryHours[col] = v;
                }
            }

            if (bad || iso is null) continue;
            validRows.Add(new ActualsRow(
                Row: rowNo,
                StoreCode: storeCode,
                Date: Date(iso),
                ActualSales: vals["actualSales"],
                ActualItems: vals["actualItems"],
                ActualCustomers: vals["actualCustomers"],
                ActualHours100: actualHours100,
                UkgActualHours: vals["ukgActualHours"],
                CategoryHours: categoryHours,
                IsMhs: OptionalBool(raw, headers, "Is_Mhs"),
                HasEsl: OptionalBool(raw, headers, "Has_Esl"),
                HasBaler: OptionalBool(raw, headers, "Baler")));
        }

        var dupes = Duplicates.CheckDuplicates(
            validRows,
            r => $"{r.StoreCode}|{r.Date.ToIso()}",
            r => $"{r.StoreCode} on {r.Date.ToIso()}",
            ActualsColumns.Date);
        return new ValidationResult<ActualsRow>(
            rows.Count,
            dupes.Rows,
            Duplicates.ByRow([.. errors, .. dupes.Errors]),
            fileErrors,
            dupes.Collapsed);
    }
}
