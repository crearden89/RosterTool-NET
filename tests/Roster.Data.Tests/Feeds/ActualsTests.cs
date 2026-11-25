// Port of src/lib/csv/actuals.test.ts.

using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class ActualsTests
{
    private static readonly HashSet<string> Stores = ["G001"];

    // Per-category hours summing to 60.0 for the day.
    private static readonly (string, string)[] CategoryHours =
    [
        ("LoadRunning_Hours", "24"), ("Cleaning_Hours", "6"), ("Specials_Hours", "3"),
        ("LoadReset_Hours", "4.5"), ("AdminMgmt_Hours", "10.5"), ("Checkout_Hours", "12"),
    ];

    private static string BuildCsv(Dictionary<string, string>? over = null, string? dropColumn = null)
    {
        var values = new List<(string Col, string Value)>
        {
            (ActualsColumns.StoreCode, "1"), (ActualsColumns.Date, "2026-08-17"), (ActualsColumns.ActualSales, "45000"),
            (ActualsColumns.ActualItems, "3200"), (ActualsColumns.ActualCustomers, "1060"), (ActualsColumns.UkgActualHours, "62"),
        };
        values.AddRange(CategoryHours);
        if (over is not null) values = values.Select(v => over.TryGetValue(v.Col, out var o) ? (v.Col, o) : v).ToList();
        var cols = values.Where(v => v.Col != dropColumn).ToList();
        return string.Join(",", cols.Select(c => c.Col)) + "\n" + string.Join(",", cols.Select(c => c.Value));
    }

    // The header names ARE the integration contract: a typo surfaces only as
    // "Missing required column(s)" when someone uploads a real file.
    [Fact]
    public void Expects_exactly_the_real_export_column_names()
    {
        Assert.Equal(["StoreID", "Period", "Retail_Sales", "Total_Items", "Customer_Count", "Kronos_Actual_Hours"], ActualsColumns.All);
        Assert.Equal(
            ["LoadRunning_Hours", "Cleaning_Hours", "Specials_Hours", "LoadReset_Hours", "AdminMgmt_Hours", "Checkout_Hours"],
            ActualsColumns.HourCategories);
    }

    [Fact]
    public void Sums_the_per_category_hour_columns_into_the_day_actual_hours()
    {
        var res = Actuals.ValidateActuals(BuildCsv(), Stores);
        Assert.Empty(res.FileErrors);
        Assert.Empty(res.Errors);
        Assert.Single(res.ValidRows);
        Assert.Equal(60, res.ValidRows[0].ActualHours100, 2);
        // Kronos hours stay a separate figure - never conflated with the sum.
        Assert.Equal(62, res.ValidRows[0].UkgActualHours);
        // Actual customers come from the file, not the benchmark's forecast.
        Assert.Equal(1060, res.ValidRows[0].ActualCustomers);
    }

    [Fact]
    public void Reads_Period_as_the_date_and_normalises_a_bare_StoreID()
    {
        var res = Actuals.ValidateActuals(BuildCsv(), Stores);
        Assert.Equal(new DateOnly(2026, 8, 17), res.ValidRows[0].Date);
        Assert.Equal("G001", res.ValidRows[0].StoreCode);
    }

    // Silently skipping an absent category would under-count the day and
    // overstate performance - the upload has to fail by name instead.
    [Fact]
    public void Rejects_the_file_when_a_category_column_is_absent()
    {
        var res = Actuals.ValidateActuals(BuildCsv(dropColumn: "AdminMgmt_Hours"), Stores);
        Assert.Contains("AdminMgmt_Hours", string.Join(" ", res.FileErrors));
        Assert.Empty(res.ValidRows);
    }

    [Fact]
    public void Names_the_offending_category_when_one_cell_is_bad()
    {
        var res = Actuals.ValidateActuals(BuildCsv(new() { ["Cleaning_Hours"] = "-4" }), Stores);
        Assert.Contains(res.Errors, e => e.Field == "Cleaning_Hours");
        Assert.Empty(res.ValidRows);
    }

    [Fact]
    public void Rejects_a_store_number_not_in_the_known_set()
    {
        var res = Actuals.ValidateActuals(BuildCsv(new() { [ActualsColumns.StoreCode] = "999" }), Stores);
        Assert.NotEmpty(res.Errors);
        Assert.Empty(res.ValidRows);
    }
}
