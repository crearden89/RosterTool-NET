// Port of src/lib/csv/uploadValidation.test.ts.
// Roster Engine Review 07/11/2026, A09-1 / A09-2: every check here is one the
// database used to make instead, partway through a write - or that nothing
// made, so the bad value loaded. They now fail as row errors before anything
// is written.

using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class UploadValidationTests
{
    private static readonly HashSet<string> G001 = ["G001"];

    // ─── Benchmark ───────────────────────────────────────────────────────────────

    private static string BenchmarkCsv(string[]? extra = null, string hours = "8")
    {
        var cols = BenchmarkColumns.All;
        string Value(string c, string date) =>
            c == BenchmarkColumns.StoreCode ? "G001"
            : c == BenchmarkColumns.Date ? date
            : c == BenchmarkColumns.IsSco ? "TRUE"
            : c == BenchmarkColumns.CustomerCount ? "1060.188"
            : c == BenchmarkColumns.LoadRunning ? hours
            : "5";
        var rows = Enumerable.Range(0, 14).Select(i => string.Join(",", cols.Select(c => Value(c, $"2026-08-{i + 1:00}"))));
        return string.Join("\n", [string.Join(",", cols), .. rows, .. extra ?? []]);
    }

    [Fact]
    public void Benchmark_rejects_both_copies_of_a_store_day_listed_twice_with_different_figures()
    {
        var clash = string.Join(",", BenchmarkColumns.All.Select(c =>
            c == BenchmarkColumns.StoreCode ? "G001" : c == BenchmarkColumns.Date ? "2026-08-02" : "9"));
        var res = Benchmark.ValidateBenchmark(BenchmarkCsv([clash]), G001);
        Assert.Equal([2, 15], res.Errors.Select(e => e.Row));
        Assert.Equal(("Date", "G001"), (res.Errors[0].Field, res.Errors[0].StoreCode));
        Assert.Contains("G001 on 2026-08-02", res.Errors[0].Message);
        Assert.Equal(13, res.ValidRows.Count);
    }

    [Fact]
    public void Benchmark_loads_an_exact_repeated_line_once_and_says_so()
    {
        var csv = BenchmarkCsv();
        var repeat = csv.Split('\n')[3];
        var res = Benchmark.ValidateBenchmark($"{csv}\n{repeat}", G001);
        Assert.Empty(res.Errors);
        Assert.Equal(14, res.ValidRows.Count);
        Assert.Equal(1, res.DuplicatesCollapsed);
    }

    [Fact]
    public void Benchmark_keeps_decimal_customer_counts() =>
        Assert.Equal(1060.188, Benchmark.ValidateBenchmark(BenchmarkCsv(), G001).ValidRows[0].CustomerCount, 2);

    [Fact]
    public void Benchmark_refuses_an_hours_figure_written_with_a_decimal_comma()
    {
        var res = Benchmark.ValidateBenchmark(BenchmarkCsv([], "\"1,5\""), G001);
        Assert.Empty(res.ValidRows);
        Assert.Equal(BenchmarkColumns.LoadRunning, res.Errors[0].Field);
    }

    // ─── Actuals ─────────────────────────────────────────────────────────────────

    private static string ActualsRow(string date, string sales = "45000") =>
        string.Join(",", ["G001", date, sales, "3200", "1060", "62", .. ActualsColumns.HourCategories.Select(_ => "10")]);

    private static readonly string ActualsHeader = string.Join(",", [.. ActualsColumns.All, .. ActualsColumns.HourCategories]);

    [Fact]
    public void Actuals_rejects_a_date_that_does_not_exist_instead_of_letting_the_database_refuse_the_push()
    {
        var res = Actuals.ValidateActuals(
            string.Join("\n", ActualsHeader, ActualsRow("2026-07-20"), ActualsRow("31/02/2026"), ActualsRow("2026-07-21")), G001);
        Assert.Equal([new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21)], res.ValidRows.Select(r => r.Date));
        var e = Assert.Single(res.Errors);
        Assert.Equal((2, ActualsColumns.Date), (e.Row, e.Field));
    }

    [Fact]
    public void Actuals_rejects_both_copies_of_a_day_pushed_twice_with_different_figures()
    {
        var res = Actuals.ValidateActuals(string.Join("\n", ActualsHeader, ActualsRow("2026-07-20"), ActualsRow("2026-07-20", "47000")), G001);
        Assert.Empty(res.ValidRows);
        Assert.Equal([1, 2], res.Errors.Select(e => e.Row));
    }

    // ─── POS quarter-hour ────────────────────────────────────────────────────────

    private const string PosHeader = "StoreID,RegisterType1,Period,DataGroup,Interval,value";

    // One reading per weekday (01/08/2026 is a Saturday): the file needs every
    // weekday represented to be accepted at all.
    private static readonly string[] PosWeek =
        Enumerable.Range(0, 7).Select(i => $"G001,M,{i + 1}/08/2026,2. Customer Count,09:00 - 09:15,7").ToArray();

    [Fact]
    public void Pos_counts_a_repeated_identical_reading_once_not_twice()
    {
        var res = PosQuarterHour.ValidatePosQuarterHour(string.Join("\n", [PosHeader, .. PosWeek, PosWeek[0]]), G001);
        Assert.Empty(res.Errors);
        Assert.Equal(1, res.DuplicatesCollapsed);
        Assert.Equal(7, res.ValidRows.First(r => r.Date == new DateOnly(2026, 8, 1)).McoCustomers);
    }

    [Fact]
    public void Pos_rejects_both_copies_of_a_reading_that_disagree_and_keeps_the_rest_of_the_block()
    {
        string[] lines =
        [
            PosHeader, .. PosWeek,
            "G001,M,1/08/2026,2. Customer Count,09:00 - 09:15,9",
            "G001,S,1/08/2026,2. Customer Count,09:00 - 09:15,3",
        ];
        var res = PosQuarterHour.ValidatePosQuarterHour(string.Join("\n", lines), G001);
        Assert.Equal([1, 8], res.Errors.Select(e => e.Row));
        Assert.Contains("register type M", res.Errors[0].Message);
        var sat = res.ValidRows.First(r => r.Date == new DateOnly(2026, 8, 1));
        Assert.Equal((0d, 3d), (sat.McoCustomers, sat.ScoCustomers));
    }

    [Fact]
    public void Pos_rejects_a_reading_on_a_date_that_does_not_exist()
    {
        var res = PosQuarterHour.ValidatePosQuarterHour(
            string.Join("\n", [PosHeader, .. PosWeek, "G001,M,31/04/2026,2. Customer Count,09:00 - 09:15,7"]), G001);
        var e = Assert.Single(res.Errors);
        Assert.Equal((8, "Period"), (e.Row, e.Field));
    }

    // ─── UKG contacts ────────────────────────────────────────────────────────────

    private const string ContactsHeader = "StoreID,Location,Employee Name,Employee ID,Job,Contract Hrs,Notional Hrs,Birth Date,Hire Date";
    private static string Contact(string id, string name, string contract = "50.00", string birth = "01/01/2000") =>
        $"G001,505001,\"{name}\",{id},ST ASSIST,{contract},5.00,{birth},01/02/2024";

    [Fact]
    public void Contacts_loads_an_exact_repeated_employee_once()
    {
        var res = UkgContacts.ValidateContacts(
            string.Join("\n", ContactsHeader, Contact("910020", "4-Nye, Harper"), Contact("910020", "4-Nye, Harper")), G001);
        Assert.Empty(res.Errors);
        Assert.Single(res.ValidRows);
        Assert.Equal(1, res.DuplicatesCollapsed);
        Assert.Equal(2, res.TotalRows);
    }

    [Fact]
    public void Contacts_rejects_two_different_records_under_one_Employee_ID()
    {
        var res = UkgContacts.ValidateContacts(
            string.Join("\n", ContactsHeader, Contact("910020", "4-Nye, Harper"), Contact("910020", "4-Nye, Harper", "60.00")), G001);
        Assert.Empty(res.ValidRows);
        Assert.Equal([(2, "Employee ID"), (3, "Employee ID")], res.Errors.Select(e => (e.Row, e.Field!)));
    }

    [Fact]
    public void Contacts_rejects_a_birth_date_that_cannot_exist_but_still_accepts_a_blank_one()
    {
        var res = UkgContacts.ValidateContacts(
            string.Join("\n", ContactsHeader, Contact("1", "6-Junior, J", "0", "31/02/2009"), Contact("2", "6-Junior, K", "0", "")), G001);
        var e = Assert.Single(res.Errors);
        Assert.Equal((2, "Birth Date"), (e.Row, e.Field));
        Assert.Equal(["2"], res.ValidRows.Select(r => r.UkgEmployeeId));
    }

    // ─── UKG leave ───────────────────────────────────────────────────────────────

    private const string LiabilityHeader =
        "StoreID,Location,Employee Name,Employee ID,Job,Contract Hrs,Notional Hrs,Sick Lve Accrued,Annual Lve Accrued,Lieu Hrs Accrued,Banked Hrs Accrued,Banked 2 Hrs Accrued";
    private static string Balance(string store, string id, string sick = "12.50", string annual = "40.00") =>
        $"{store},505001,\"4-Person, {id}\",{id},ST ASSIST,50.00,5.00,{sick},{annual},,,";

    [Fact]
    public void Liability_checks_every_balance_not_only_the_contract()
    {
        var res = LeaveUploads.ValidateLeaveLiability(
            string.Join("\n", LiabilityHeader, Balance("G001", "1"), Balance("G001", "2", "-"), Balance("G001", "3", "12.50", "N/A")), G001);
        Assert.Equal(["1"], res.ValidRows.Select(r => r.UkgEmployeeId));
        Assert.Equal([(3, "Sick Lve Accrued"), (4, "Annual Lve Accrued")], res.Errors.Select(e => (e.Row, e.Field!)));
        Assert.Contains("'-'", res.Errors[0].Message);
    }

    [Fact]
    public void Liability_rejects_two_different_balances_for_one_employee()
    {
        var res = LeaveUploads.ValidateLeaveLiability(string.Join("\n", LiabilityHeader, Balance("G001", "7"), Balance("G001", "7", "20.00")), G001);
        Assert.Empty(res.ValidRows);
        Assert.Equal([2, 3], res.Errors.Select(e => e.Row));
    }

    [Fact]
    public void Liability_reports_an_unknown_store_on_the_line_it_is_on()
    {
        // The row number used to be the row's position among the rows that parsed.
        var res = LeaveUploads.ValidateLeaveLiability(string.Join("\n", LiabilityHeader, Balance("G001", "1", "x"), Balance("G999", "2")), G001);
        Assert.Equal([(2, "Sick Lve Accrued"), (3, "StoreID")], res.Errors.Select(e => (e.Row, e.Field!)));
    }

    private const string RequestHeader = "StoreID,Location,Employee ID,Employee Name,Leave Type,Start Date,End Date,Status,Approver";

    [Fact]
    public void Requests_reject_a_booking_with_no_Employee_ID()
    {
        // Two of these on the same dates collapsed into one booking, so one
        // person's leave disappeared.
        var res = LeaveUploads.ValidateLeaveRequests(string.Join("\n",
            RequestHeader,
            "G001,505001,,Someone,REQ - Annual Leave,01-12-2026 12:00 AM,05-12-2026 12:00 AM,Approved,x",
            "G001,505001,91,Other,REQ - Annual Leave,01-12-2026 12:00 AM,05-12-2026 12:00 AM,Approved,x"), G001);
        var e = Assert.Single(res.Errors);
        Assert.Equal((2, "Employee ID"), (e.Row, e.Field));
        Assert.Equal(["91"], res.ValidRows.Select(r => r.UkgEmployeeId));
    }

    [Fact]
    public void Requests_reject_a_booking_on_a_date_that_does_not_exist()
    {
        var res = LeaveUploads.ValidateLeaveRequests(string.Join("\n",
            RequestHeader, "G001,505001,91,Other,REQ - Annual Leave,31-11-2026 12:00 AM,05-12-2026 12:00 AM,Approved,x"), G001);
        Assert.Empty(res.ValidRows);
        var e = Assert.Single(res.Errors);
        Assert.Equal((2, "Start Date"), (e.Row, e.Field));
    }
}
