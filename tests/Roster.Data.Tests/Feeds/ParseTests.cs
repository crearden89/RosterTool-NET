// Port of src/lib/csv/parse.test.ts.
// Roster Engine Review 07/11/2026, A09-2 / A09-17: values JavaScript's Number()
// accepts but no export means, and dates that cannot exist, passed validation
// and were then either loaded wrong or refused by Postgres partway through a
// write.

using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class ParseTests
{
    [Fact]
    public void Reads_plain_and_thousands_grouped_decimals()
    {
        Assert.Equal(18, CsvParse.ParseDecimal("18"));
        Assert.Equal(4.5, CsvParse.ParseDecimal("4.5"));
        Assert.Equal(-3.25, CsvParse.ParseDecimal("-3.25"));
        Assert.Equal(45000, CsvParse.ParseDecimal("45,000"));
        Assert.Equal(1234567.89, CsvParse.ParseDecimal("1,234,567.89"));
        Assert.Equal(0.5, CsvParse.ParseDecimal(".5"));
        Assert.Equal(5, CsvParse.ParseDecimal("+5"));
        // Reporting tools write tiny values in exponent form.
        Assert.Equal(0.000012, CsvParse.ParseDecimal("1.2E-05"), 10);
        Assert.Equal(1000, CsvParse.ParseDecimal("1e3"));
    }

    [Theory]
    // "1,5" used to load as 15; hex and Infinity loaded as numbers.
    [InlineData("1,5")]
    [InlineData("12,34")]
    [InlineData("0x10")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("e5")]
    [InlineData("-")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("$12")]
    public void Refuses_what_is_not_a_decimal_instead_of_guessing(string raw) =>
        Assert.True(double.IsNaN(CsvParse.ParseDecimal(raw)), raw);

    [Fact]
    public void Keeps_UKG_accounting_brackets_and_blanks_and_rejects_everything_else()
    {
        Assert.Equal(-69.48, UkgLeave.UkgNumber("(69.48)"));
        Assert.Equal(1234.5, UkgLeave.UkgNumber("1,234.50"));
        Assert.Equal(0, UkgLeave.UkgNumber(""));
        Assert.Equal(0, UkgLeave.UkgNumber(null));
        Assert.True(double.IsNaN(UkgLeave.UkgNumber("-")));
        Assert.True(double.IsNaN(UkgLeave.UkgNumber("N/A")));
        Assert.True(double.IsNaN(UkgLeave.UkgNumber("(1,5)")));
    }

    [Fact]
    public void Normalises_every_accepted_date_layout()
    {
        Assert.Equal("2026-07-21", CsvParse.ToIsoDate("2026-07-21"));
        Assert.Equal("2026-07-21", CsvParse.ToIsoDate("21/07/2026"));
        Assert.Equal("2026-08-01", CsvParse.ToIsoDate("1/8/2026"));
        Assert.Equal("2026-12-26", UkgLeave.UkgDate("26-12-2026 12:00 AM"));
        Assert.Equal("2028-02-29", UkgLeave.UkgDate("29/02/2028"));
    }

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("29/02/2026")]
    [InlineData("31/04/2026")]
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("00/01/2026")]
    [InlineData("1/1/0202")]
    public void Refuses_dates_that_cannot_exist(string raw) => Assert.Null(CsvParse.ToIsoDate(raw));

    [Fact]
    public void Refuses_impossible_UKG_and_calendar_dates()
    {
        Assert.Null(UkgLeave.UkgDate("31-11-2026 12:00 AM"));
        Assert.Null(CsvParse.IsoDate(2026, 2, 29));
        Assert.Equal("2028-02-29", CsvParse.IsoDate(2028, 2, 29));
    }
}
