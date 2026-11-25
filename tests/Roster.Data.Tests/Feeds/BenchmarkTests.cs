// Port of src/lib/csv/benchmark.test.ts.

using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class BenchmarkTests
{
    private static readonly HashSet<string> Stores = ["G001"];

    // One full 14-day fortnight for a single store, real-export column order.
    private static string BuildCsv(string storeCodeCell)
    {
        var cols = BenchmarkColumns.All;
        var values = new Dictionary<string, string>
        {
            [BenchmarkColumns.Date] = "",
            [BenchmarkColumns.IsSco] = "TRUE",
            [BenchmarkColumns.SalesForecast] = "12500.50",
            [BenchmarkColumns.Items] = "3400",
            [BenchmarkColumns.CustomerCount] = "1060.188", // decimal - was rejected by the old integer column
            [BenchmarkColumns.LoadRunning] = "8",
            [BenchmarkColumns.Cleaning] = "4",
            [BenchmarkColumns.Specials] = "3",
            [BenchmarkColumns.Reset] = "6",
            [BenchmarkColumns.Admin] = "5",
            [BenchmarkColumns.CheckoutHours] = "50",
        };
        var rows = new List<string> { string.Join(",", cols) };
        for (var day = 1; day <= 14; day++)
        {
            var date = $"2026-08-{day:00}";
            rows.Add(string.Join(",", cols.Select(c =>
                c == BenchmarkColumns.StoreCode ? storeCodeCell : c == BenchmarkColumns.Date ? date : values[c])));
        }
        return string.Join("\n", rows);
    }

    // The header names ARE the integration contract with the export: a typo here
    // surfaces only as "Missing required column(s)" when someone uploads a real
    // file. Everything else in this suite builds its CSV from the constant, so
    // this is the one place the literal strings are checked.
    [Fact]
    public void Expects_exactly_the_real_export_column_names() =>
        Assert.Equal(
            ["StoreID", "Date", "Has_Sco", "Retail_Sales", "Total_Items1", "Customer_Count", "LoadRunning_Hours",
             "Cleaning_Hours", "Specials_Hours", "LoadReset_Hours", "AdminMgmt_Hours", "Checkout_Hours"],
            BenchmarkColumns.All);

    [Fact]
    public void Parses_a_bare_StoreID_and_normalises_it_to_the_GXXX_store_code()
    {
        var res = Benchmark.ValidateBenchmark(BuildCsv("1"), Stores);
        Assert.Empty(res.FileErrors);
        Assert.Empty(res.Errors);
        Assert.Equal(14, res.ValidRows.Count);
        Assert.Equal("G001", res.ValidRows[0].StoreCode);
    }

    [Fact]
    public void Accepts_a_decimal_Customer_Count() =>
        Assert.Equal(1060.188, Benchmark.ValidateBenchmark(BuildCsv("1"), Stores).ValidRows[0].CustomerCount, 2);

    [Fact]
    public void Parses_the_single_Checkout_Hours_total() =>
        Assert.Equal(50, Benchmark.ValidateBenchmark(BuildCsv("1"), Stores).ValidRows[0].CheckoutHours100);

    [Fact]
    public void Rejects_a_store_number_not_in_the_known_set()
    {
        var res = Benchmark.ValidateBenchmark(BuildCsv("999"), Stores);
        Assert.NotEmpty(res.Errors);
        Assert.Empty(res.ValidRows);
    }
}
