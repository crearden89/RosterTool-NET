// Port of src/lib/csv/posQuarterHour.fixture.test.ts.
// The TypeScript test reads a client's real four-week export for the demo store. Real
// trade figures stay out of this repository, so the same file is built here instead:
// the export's exact shape (byte-order mark, the extra report columns, D/MM/YYYY
// periods, each hour's last quarter closing at :59, blank cells for quiet blocks,
// manned and self-checkout lines for both measures) with invented counts, and the
// worked example's block (Sun 09/08/2026 11:45: 883 items, 46 customers) put in as given.

using System.Text;
using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class PosQuarterHourFixtureTests
{
    private static readonly string Text = BuildExport();
    private static readonly ValidationResult<PosQuarterHourRow> Parsed =
        PosQuarterHour.ValidatePosQuarterHour(Text, new HashSet<string> { "G999" });

    private static string BuildExport()
    {
        var sb = new StringBuilder("﻿Textbox31,Store_Region,StoreID,RegisterType1,Period,DataGroup,Interval,value,OpeningHours\n");
        // 20/07/2026 - 16/08/2026, in the export's own order (1/08 ... 16/08, then 20/07 ... 31/07).
        var dates = Enumerable.Range(0, 28).Select(i => new DateOnly(2026, 7, 20).AddDays(i))
            .OrderBy(d => d.Month == 7 ? 1 : 0).ThenBy(d => d.Day).ToList();
        var random = new Random(20260809);
        foreach (var rt in new[] { "M", "S" })
            foreach (var d in dates)
                foreach (var (group, scale) in new[] { ("2. Customer Count", 3), ("5. Items Scanned", 40) })
                    for (var b = 8 * 60; b <= 20 * 60; b += 15)
                    {
                        var start = $"{b / 60:00}:{b % 60:00}";
                        var endMin = b % 60 == 45 ? b + 14 : b + 15;
                        var interval = $"{start} - {endMin / 60:00}:{endMin % 60:00}";
                        string value;
                        if (d == new DateOnly(2026, 8, 9) && b == 11 * 60 + 45)
                            value = (rt, group[0]) switch { ("M", '2') => "30", ("S", '2') => "16", ("M", _) => "700", _ => "183" };
                        else if (b < 8 * 60 + 30 && random.Next(3) == 0)
                            value = ""; // a quiet block: no trade, written blank
                        else
                            value = (random.Next(1, 12) * scale / (rt == "S" ? 2 : 1)).ToString();
                        sb.Append($"Date,46,G999,{rt},{d.Day}/{d.Month:00}/{d.Year},{group},{interval},{value},-\n");
                    }
        return sb.ToString();
    }

    [Fact]
    public void Parses_with_no_row_errors()
    {
        Assert.Empty(Parsed.Errors);
        Assert.Empty(Parsed.FileErrors);
        // 28 dates x 49 intervals, folded from 5,488 long-format lines.
        Assert.Equal(5488, Parsed.TotalRows);
        Assert.Equal(28 * 49, Parsed.ValidRows.Count);
    }

    [Fact]
    public void Derives_a_curve_that_sums_to_1_on_every_weekday()
    {
        var (allocations, reports) = PosAllocation.DeriveAllocations(Parsed.ValidRows);
        Assert.True(reports[0].HasSco);
        Assert.Equal(28, reports[0].DatesUsed);
        Assert.Empty(reports[0].WeekdaysWithoutData);
        var byDow = allocations.GroupBy(a => a.DayOfWeek).ToDictionary(g => g.Key, g => g.Sum(a => a.Allocation));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], byDow.Keys.Order());
        foreach (var t in byDow.Values) Assert.Equal(1, t, 10);
    }

    [Fact]
    public void Matches_the_worked_example_given_to_the_client()
    {
        // G999, Sun 09/08/2026, 11:45 block: 883 items, 46 customers.
        var r = Parsed.ValidRows.First(x => x.Date == new DateOnly(2026, 8, 9) && x.BlockTime == "11:45:00");
        Assert.Equal(883, r.McoItems + r.ScoItems);
        Assert.Equal(46, r.McoCustomers + r.ScoCustomers);
        Assert.Equal(0.734, 883 * 0.0008 + 46 * 0.0006, 6);
    }

    [Fact]
    public void Drops_a_public_holiday_from_the_sample_it_is_given()
    {
        var reports = PosAllocation.DeriveAllocations(Parsed.ValidRows, new DeriveOptions(new HashSet<string> { "G999|2026-08-03" })).Reports;
        Assert.Equal(1, reports[0].DatesExcludedPublicHoliday);
        Assert.Equal(27, reports[0].DatesUsed);
    }
}
