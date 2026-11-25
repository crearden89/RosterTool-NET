// Port of src/lib/csv/posQuarterHour.test.ts.

using Roster.Data.Feeds;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Tests.Feeds;

public class PosQuarterHourTests
{
    private static readonly HashSet<string> Stores = ["G999"];
    private const string Head = "Textbox31,Store_Region,StoreID,RegisterType1,Period,DataGroup,Interval,value,OpeningHours";
    private static string Row(string rt, string date, string group, string interval, string value) =>
        $"Date,46,G999,{rt},{date},{group},{interval},{value},-";
    private const string Cust = "2. Customer Count";
    private const string Item = "5. Items Scanned";

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    [Fact]
    public void IntervalStartMinutes_reads_the_start_not_the_end()
    {
        Assert.Equal(480, PosQuarterHour.IntervalStartMinutes("08:00 - 08:15"));
        // The export closes each hour's last quarter at :59 - the end time is not
        // a 15-minute boundary, so only the start can be trusted.
        Assert.Equal(525, PosQuarterHour.IntervalStartMinutes("08:45 - 08:59"));
        Assert.Equal(1200, PosQuarterHour.IntervalStartMinutes("20:00 - 20:15"));
    }

    [Fact]
    public void IntervalStartMinutes_rejects_nonsense()
    {
        Assert.Null(PosQuarterHour.IntervalStartMinutes("not a time"));
        Assert.Null(PosQuarterHour.IntervalStartMinutes("99:00 - 99:15"));
    }

    [Fact]
    public void Folds_the_four_long_format_lines_of_a_block_into_one_record()
    {
        var csv = string.Join("\n",
            Head,
            Row("M", "1/08/2026", Cust, "08:30 - 08:45", "7"),
            Row("S", "1/08/2026", Cust, "08:30 - 08:45", "3"),
            Row("M", "1/08/2026", Item, "08:30 - 08:45", "120"),
            Row("S", "1/08/2026", Item, "08:30 - 08:45", "45"));
        var r = PosQuarterHour.ValidatePosQuarterHour(csv, Stores);
        Assert.Empty(r.Errors);
        var row = Assert.Single(r.ValidRows);
        Assert.Equal(("G999", D("2026-08-01"), "08:30:00", 7d, 3d, 120d, 45d),
            (row.StoreCode, row.Date, row.BlockTime, row.McoCustomers, row.ScoCustomers, row.McoItems, row.ScoItems));
    }

    [Fact]
    public void Treats_a_blank_value_as_no_trade_not_bad_data()
    {
        var r = PosQuarterHour.ValidatePosQuarterHour(string.Join("\n", Head, Row("M", "1/08/2026", Cust, "08:00 - 08:15", "")), Stores);
        Assert.Empty(r.Errors);
        Assert.Equal(0, r.ValidRows[0].McoCustomers);
    }

    [Fact]
    public void Flags_unknown_stores_register_types_groups_and_negatives()
    {
        var csv = string.Join("\n",
            Head,
            Row("M", "1/08/2026", Cust, "08:00 - 08:15", "-5").Replace("G999", "G404"),
            Row("X", "1/08/2026", Cust, "08:00 - 08:15", "1"),
            Row("M", "1/08/2026", "9. Something Else", "08:00 - 08:15", "1"),
            Row("M", "1/08/2026", Cust, "08:07 - 08:22", "1"));
        var fields = PosQuarterHour.ValidatePosQuarterHour(csv, Stores).Errors.Select(e => e.Field).ToList();
        Assert.Contains("StoreID", fields);
        Assert.Contains("RegisterType1", fields);
        Assert.Contains("DataGroup", fields);
        Assert.Contains("Interval", fields); // 08:07 is off the 15-minute grid
    }

    [Fact]
    public void Reports_a_store_that_cannot_cover_all_seven_weekdays()
    {
        var r = PosQuarterHour.ValidatePosQuarterHour(string.Join("\n", Head, Row("M", "1/08/2026", Cust, "08:00 - 08:15", "1")), Stores);
        Assert.Contains("7 weekdays", string.Join(" ", r.FileErrors));
    }

    // One weekday, two blocks, with SCO trade present.
    private static List<PosQuarterHourRow> Rows(params PosQuarterHourRow[] over) =>
    [
        new(1, "G999", D("2026-08-03"), "09:00:00", 10, 5, 100, 50),
        new(2, "G999", D("2026-08-03"), "09:15:00", 20, 10, 200, 100),
        .. over,
    ];

    [Fact]
    public void DeriveAllocations_uses_the_SCO_weights_when_the_file_carries_SCO_trade()
    {
        var (allocations, reports) = PosAllocation.DeriveAllocations(Rows());
        Assert.True(reports[0].HasSco);
        // Block 1: 150 items, 15 customers. Block 2: exactly double.
        var w1 = 150 * PosAllocation.ScoWeights.Items + 15 * PosAllocation.ScoWeights.Customers;
        var w2 = 300 * PosAllocation.ScoWeights.Items + 30 * PosAllocation.ScoWeights.Customers;
        var got = allocations.First(a => a.BlockTime == "09:00:00");
        Assert.Equal(w1 / (w1 + w2), got.Allocation, 10);
        Assert.Equal(1.0 / 3, got.Allocation, 10); // double the trade, double the share
    }

    [Fact]
    public void DeriveAllocations_uses_the_MCO_only_weights_when_there_is_no_SCO_trade_at_all()
    {
        var mcoOnly = Rows().Select(r => r with { ScoCustomers = 0, ScoItems = 0 }).ToList();
        Assert.False(PosAllocation.DeriveAllocations(mcoOnly).Reports[0].HasSco);
    }

    [Fact]
    public void DeriveAllocations_normalises_each_weekday_to_1()
    {
        foreach (var total in PosAllocation.DeriveAllocations(Rows()).Allocations.GroupBy(a => a.DayOfWeek).Select(g => g.Sum(a => a.Allocation)))
            Assert.Equal(1, total, 10);
    }

    [Fact]
    public void DeriveAllocations_excludes_public_holidays_so_one_does_not_distort_its_weekday()
    {
        // Two Mondays. The public holiday one is all evening trade; the normal one
        // all morning. Excluding it must leave the morning shape untouched.
        var ph = new PosQuarterHourRow(3, "G999", D("2026-08-10"), "18:00:00", 500, 0, 5000, 0);
        var all = PosAllocation.DeriveAllocations(Rows(ph));
        var excluded = PosAllocation.DeriveAllocations(Rows(ph), new DeriveOptions(new HashSet<string> { "G999|2026-08-10" }));
        Assert.Contains(all.Allocations, a => a.BlockTime == "18:00:00");
        Assert.DoesNotContain(excluded.Allocations, a => a.BlockTime == "18:00:00");
        Assert.Equal(1, excluded.Reports[0].DatesExcludedPublicHoliday);
        Assert.Equal(1.0 / 3, excluded.Allocations.First(a => a.BlockTime == "09:00:00").Allocation, 10);
    }

    [Fact]
    public void DeriveAllocations_excludes_a_date_the_store_recorded_no_trade_on()
    {
        var closed = new PosQuarterHourRow(4, "G999", D("2026-08-10"), "09:00:00", 0, 0, 0, 0);
        var reports = PosAllocation.DeriveAllocations(Rows(closed)).Reports;
        Assert.Equal(1, reports[0].DatesExcludedNoTrade);
        Assert.Equal(1, reports[0].DatesUsed);
    }

    [Fact]
    public void DeriveAllocations_names_the_weekdays_left_without_any_usable_date()
    {
        // Only a Monday (2026-08-03) was supplied.
        Assert.Equal(0, PosAllocation.DowMon0(D("2026-08-03")));
        Assert.Equal([1, 2, 3, 4, 5, 6], PosAllocation.DeriveAllocations(Rows()).Reports[0].WeekdaysWithoutData);
    }

    private static PosQuarterHourRow Pos(string date, string blockTime, double customers, double items) =>
        new(0, "G999", D(date), blockTime, customers, 0, items, 0);

    private static QuarterHourRow Legacy(int dow, string blockTime, double allocation) =>
        new() { DayOfWeek = dow, BlockTime = blockTime, Allocation = allocation };

    [Fact]
    public void AssembleCurveWeek_derived_POS_weekdays_replace_the_legacy_curve_wholesale()
    {
        // Legacy Monday says all trade at 09:00; POS says it is split with 15:00.
        var week = PosAllocation.AssembleCurveWeek(
            [Pos("2026-08-03", "09:00:00", 10, 100), Pos("2026-08-03", "15:00:00", 10, 100)],
            [Legacy(0, "09:00:00", 1)],
            new HashSet<DateOnly>());
        var mon = week[0];
        Assert.Equal(2, mon.Count);
        Assert.Equal(1, mon.Sum(r => r.Allocation), 10);
        Assert.Equal(0.5, mon.First(r => r.BlockTime == "15:00:00").Allocation, 10);
    }

    [Fact]
    public void AssembleCurveWeek_weekdays_without_POS_data_keep_the_legacy_curve()
    {
        var week = PosAllocation.AssembleCurveWeek(
            [Pos("2026-08-03", "09:00:00", 10, 100)], // a Monday only
            [Legacy(0, "08:00:00", 1), Legacy(5, "10:00:00", 0.4), Legacy(5, "11:00:00", 0.6)],
            new HashSet<DateOnly>());
        Assert.Equal("09:00:00", week[0][0].BlockTime); // POS won Monday
        Assert.Equal(2, week[5].Count); // Saturday still legacy
    }

    [Fact]
    public void AssembleCurveWeek_with_no_POS_rows_at_all_the_legacy_curve_passes_through_untouched() =>
        Assert.Equal(1, PosAllocation.AssembleCurveWeek([], [Legacy(2, "12:00:00", 1)], new HashSet<DateOnly>())[2][0].Allocation);

    [Fact]
    public void AssembleCurveWeek_a_public_holiday_in_the_sample_is_excluded_before_deriving()
    {
        // Two Mondays: the PH one is all-evening. Excluded, Monday reads as the
        // normal week alone.
        var week = PosAllocation.AssembleCurveWeek(
            [Pos("2026-08-03", "09:00:00", 10, 100), Pos("2026-08-10", "19:00:00", 500, 5000)],
            [],
            new HashSet<DateOnly> { D("2026-08-10") });
        var mon = Assert.Single(week[0]);
        Assert.Equal("09:00:00", mon.BlockTime);
    }
}
