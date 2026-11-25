// Port of src/lib/csv/duplicates.test.ts.

using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class DuplicatesTests
{
    private sealed record TestRow(int Row, string StoreCode, string Date, double Hours, IReadOnlyDictionary<string, double>? CategoryHours = null)
        : IFeedRow;

    private static TestRow R(int row, string store, string date, double hours, IReadOnlyDictionary<string, double>? extra = null) =>
        new(row, store, date, hours, extra);

    private static string Key(TestRow r) => $"{r.StoreCode}|{r.Date}";
    private static string Label(TestRow r) => $"{r.StoreCode} on {r.Date}";

    [Fact]
    public void Passes_unique_rows_straight_through()
    {
        TestRow[] rows = [R(1, "G001", "2026-07-20", 8), R(2, "G001", "2026-07-21", 8)];
        var out_ = Duplicates.CheckDuplicates(rows, Key, Label);
        Assert.Equal(rows, out_.Rows);
        Assert.Empty(out_.Errors);
        Assert.Equal(0, out_.Collapsed);
    }

    [Fact]
    public void Keeps_one_of_a_set_of_identical_copies_and_counts_the_rest()
    {
        TestRow[] rows = [R(1, "G001", "2026-07-20", 8), R(2, "G001", "2026-07-20", 8), R(3, "G001", "2026-07-20", 8)];
        var out_ = Duplicates.CheckDuplicates(rows, Key, Label);
        Assert.Equal([1], out_.Rows.Select(r => r.Row));
        Assert.Empty(out_.Errors);
        Assert.Equal(2, out_.Collapsed);
    }

    [Fact]
    public void Rejects_every_copy_when_the_copies_disagree_naming_the_others()
    {
        TestRow[] rows = [R(4, "G001", "2026-07-21", 8), R(5, "G002", "2026-07-21", 6), R(9, "G001", "2026-07-21", 9)];
        var out_ = Duplicates.CheckDuplicates(rows, Key, Label, "Date");
        Assert.Equal([5], out_.Rows.Select(r => r.Row));
        Assert.Equal([(4, "G001", "Date"), (9, "G001", "Date")], out_.Errors.Select(e => (e.Row, e.StoreCode, e.Field)));
        Assert.Contains("G001 on 2026-07-21", out_.Errors[0].Message);
        Assert.Contains("also row 9", out_.Errors[0].Message);
        Assert.Equal(0, out_.Collapsed);
    }

    [Fact]
    public void Compares_nested_values_not_just_the_top_level()
    {
        // Actuals rows carry their per-category hours in a nested object.
        var a = R(1, "G001", "2026-07-20", 8, new Dictionary<string, double> { ["LoadRunning_Hours"] = 5, ["Cleaning_Hours"] = 3 });
        var b = R(2, "G001", "2026-07-20", 8, new Dictionary<string, double> { ["LoadRunning_Hours"] = 3, ["Cleaning_Hours"] = 5 });
        Assert.Equal(2, Duplicates.CheckDuplicates([a, b], Key, Label).Errors.Count);
        // ...in any key order.
        var c = R(3, "G001", "2026-07-20", 8, new Dictionary<string, double> { ["Cleaning_Hours"] = 3, ["LoadRunning_Hours"] = 5 });
        var out_ = Duplicates.CheckDuplicates([a, c], Key, Label);
        Assert.Empty(out_.Errors);
        Assert.Equal(1, out_.Collapsed);
    }
}
