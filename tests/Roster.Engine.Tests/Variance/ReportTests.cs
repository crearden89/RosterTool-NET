// New: the TypeScript app has no tests for the variance report page. The
// expected values below are what the page's own code (copied verbatim into
// $S/agents/p8/js/variance-report.mjs, minus the database reads) returns for the
// same rows.

using Roster.Engine.Variance;

namespace Roster.Engine.Tests.Variance;

public class ReportTests
{
    private static readonly Guid A = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("0b000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("0c000000-0000-0000-0000-000000000003");
    private static readonly Guid D = Guid.Parse("0d000000-0000-0000-0000-000000000004");
    private static readonly Guid E = Guid.Parse("0e000000-0000-0000-0000-000000000005");
    private static readonly Guid F = Guid.Parse("f0000000-0000-0000-0000-000000000006");

    private static readonly VarianceStore[] Stores =
    [
        new(A, "G010"), new(B, "G002"), new(C, "G030"), new(D, "G004"), new(E, "G005"),
    ];

    private static readonly BenchmarkDayFigures[] Bench =
    [
        new()
        {
            StoreId = B, SalesForecast = 10000, Items = 2000, CustomerCount = 400, IsMhs = true, HasEsl = true, HasBaler = false,
            LrHours100 = 20, CleaningHours100 = 5, SpecialsHours100 = 3, ResetHours100 = 2, AdminHours100 = 8, CheckoutHours100 = 30,
        },
        new()
        {
            StoreId = A, SalesForecast = 8000, Items = 0, CustomerCount = 300, IsMhs = null, HasEsl = true, HasBaler = true,
            LrHours100 = 10, CleaningHours100 = 4, SpecialsHours100 = 0, ResetHours100 = 1.5, AdminHours100 = 6, CheckoutHours100 = 20,
        },
        new()
        {
            StoreId = C, SalesForecast = 5000, Items = 900, CustomerCount = 200, IsMhs = true, HasEsl = true, HasBaler = true,
            LrHours100 = 1, CleaningHours100 = 1, SpecialsHours100 = 1, ResetHours100 = 1, AdminHours100 = 1, CheckoutHours100 = 1,
        },
        new()
        {
            StoreId = D, SalesForecast = 0, Items = 0, CustomerCount = 0,
            LrHours100 = 0, CleaningHours100 = 0, SpecialsHours100 = 0, ResetHours100 = 0, AdminHours100 = 0, CheckoutHours100 = 0,
        },
    ];

    private static readonly ActualsDayFigures[] Acts =
    [
        new()
        {
            StoreId = B, ActualSales = 18000, ActualItems = 2100, ActualCustomers = 90, ActualHours100 = 100, UkgActualHours = 101,
            IsMhs = false, HasEsl = false, HasBaler = true,
            LrHours = 30, CleaningHours = 5, SpecialsHours = 0, ResetHours = null, AdminHours = 8, CheckoutHours = 40,
        },
        new()
        {
            StoreId = A, ActualSales = 8100, ActualItems = 25, ActualCustomers = null, ActualHours100 = 41.5, UkgActualHours = 40,
            IsMhs = true, HasEsl = true, HasBaler = true,
        },
        new() { StoreId = E, ActualSales = 1234.567, ActualItems = 10, ActualCustomers = 5, ActualHours100 = 3, UkgActualHours = 3 },
        new() { StoreId = F, ActualSales = 7, ActualItems = 0, ActualCustomers = 0, ActualHours100 = 0, UkgActualHours = 0 },
    ];

    private const string Differs = "Boolean value differs between Actuals and Benchmark.";

    [Fact]
    public void The_day_report_matches_the_page()
    {
        var r = VarianceReports.Build(Stores, Bench, Acts, 70, 20);

        Assert.Equal(4, r.StoresWithBenchmarks);

        Assert.Equal(
            [
                new VarianceRow("G002", "Customer Count", 90, 400, -77.5, "Actual is 77.5% below benchmark."),
                new VarianceRow("G002", "Has_Baler", null, null, null, Differs, "TRUE", "FALSE"),
                new VarianceRow("G002", "Has_Esl", null, null, null, Differs, "FALSE", "TRUE"),
                new VarianceRow("G002", "Has_Esl", null, null, null, "Has_Esl is FALSE on a store that traded.", "FALSE", "Expected TRUE"),
                new VarianceRow("G002", "Is_Mhs", null, null, null, Differs, "FALSE", "TRUE"),
                new VarianceRow("G002", "Is_Mhs", null, null, null, "Is_Mhs is FALSE on a store that traded.", "FALSE", "Expected TRUE"),
                new VarianceRow("G002", "Retail Sales", 18000, 10000, 80, "Actual is 80.0% above benchmark."),
                new VarianceRow("G010", "Total Items", 25, 0, null, "Actual is non-zero while benchmark is zero."),
            ],
            r.MetricFlags);

        Assert.Equal(
            [
                new VarianceRow("G002", "Checkout", 40, 30, 33.33333333333333, "Actual is 33.3% above benchmark."),
                new VarianceRow("G002", "Load Running", 30, 20, 50, "Actual is 50.0% above benchmark."),
                new VarianceRow("G002", "Specials", 0, 3, -100, "Actual is 100.0% below benchmark."),
                new VarianceRow("G002", "Total Hours", 100, 68, 47.05882352941176, "Actual is 47.1% above benchmark."),
            ],
            r.HourFlags);

        Assert.Equal(
            [
                new MissingStore("G030", "Benchmark only", 5000, "Benchmark loaded but no actuals were uploaded for this date."),
                new MissingStore("G005", "Actuals only", 1234.567, "Actuals report sales but no benchmark exists for this date."),
                // No store row: the report names it by its id.
                new MissingStore("f0000000-0000-0000-0000-000000000006", "Actuals only", 7, "Actuals report sales but no benchmark exists for this date."),
            ],
            r.Missing);

        Assert.Equal(
            ["-77.5%", "—", "—", "—", "—", "—", "+80.0%", "Undefined"],
            r.MetricFlags.Select(VarianceReports.VarianceText));
        Assert.Equal(["+33.3%", "+50.0%", "-100.0%", "+47.1%"], r.HourFlags.Select(VarianceReports.VarianceText));
    }

    [Fact]
    public void The_rows_order_by_store_id_whatever_order_they_arrive_in()
    {
        var forward = VarianceReports.Build(Stores, Bench, Acts, 70, 20);
        var backward = VarianceReports.Build(Stores, Bench.Reverse(), Acts.Reverse(), 70, 20);
        Assert.Equal(forward.Missing, backward.Missing);
        Assert.Equal(forward.MetricFlags, backward.MetricFlags);
    }

    [Fact]
    public void A_dormant_day_lists_nothing()
    {
        var r = VarianceReports.Build([], [], [new ActualsDayFigures { StoreId = Guid.NewGuid(), ActualSales = 0 }], 70, 20);
        Assert.Equal(0, r.StoresWithBenchmarks);
        Assert.Empty(r.MetricFlags);
        Assert.Empty(r.HourFlags);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Severity_is_red_when_undefined_or_beyond_100_percent()
    {
        Assert.True(VarianceReports.VarianceIsSevere(new VarianceRow("s", "m", 1, 0, null, "")));
        Assert.True(VarianceReports.VarianceIsSevere(new VarianceRow("s", "m", 1, 0, -100.5, "")));
        Assert.False(VarianceReports.VarianceIsSevere(new VarianceRow("s", "m", 1, 0, 100, "")));
        Assert.False(VarianceReports.VarianceIsSevere(new VarianceRow("s", "m", 1, 0, -20, "")));
    }
}
