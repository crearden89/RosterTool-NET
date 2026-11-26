// New: the TypeScript app has no tests for its Super User pages. These pin the checks
// and messages each page's actions.ts makes, case by case, as the TypeScript code makes them.

using Roster.Web.Features.Admin.LoadTypes;
using Roster.Web.Features.Admin.NonOpCategories;
using Roster.Web.Features.Admin.SpecialTradingHours;
using Roster.Web.Features.Admin.StoreAttributes;
using Roster.Web.Features.Admin.VarianceReport;
using Roster.Web.Features.Admin.Views;

namespace Roster.Web.Tests.AdminPages;

public class SettingsRulesTests
{
    private static SpecialHoursInput Range(string start, string end, bool closed = false, string? open = "08:30", string? close = "21:00") =>
        new(Guid.NewGuid(), start, end, closed, open, close, "");

    [Fact]
    public void Special_hours_are_checked_in_the_actions_order()
    {
        Assert.Equal("Store, start date and end date are required.", SpecialTradingHoursService.Validate(Range("", "2026-12-25")));
        Assert.Equal("Store, start date and end date are required.", SpecialTradingHoursService.Validate(Range("2026-12-25", "2026-12-25") with { StoreId = null }));
        Assert.Equal("End date must be on or after the start date.", SpecialTradingHoursService.Validate(Range("2026-12-26", "2026-12-25")));
        Assert.Equal("Enter open and close times, or mark the range closed.", SpecialTradingHoursService.Validate(Range("2026-12-25", "2026-12-25", open: "")));
        Assert.Equal("Open time must be before close time.", SpecialTradingHoursService.Validate(Range("2026-12-25", "2026-12-25", open: "21:00", close: "21:00")));
        // Closed ranges need no times.
        Assert.Null(SpecialTradingHoursService.Validate(Range("2026-12-25", "2026-12-25", closed: true, open: null, close: null)));
        Assert.Null(SpecialTradingHoursService.Validate(Range("2026-12-24", "2026-12-25")));
    }

    [Fact]
    public void Load_types_are_checked_in_the_actions_order()
    {
        var trained = TenureMinutes.Trained;
        Assert.Equal("Name is required", LoadTypesService.ValidateUpdate("  ", 15, "#2563eb", trained, 100, null));
        Assert.Equal("Minutes must be positive", LoadTypesService.ValidateUpdate("Ambient", 0, "#2563eb", trained, 100, null));
        Assert.Equal("Minutes must be positive", LoadTypesService.ValidateUpdate("Ambient", double.NaN, "#2563eb", trained, 100, null));
        Assert.Equal("Colour must be a hex value like #2563eb", LoadTypesService.ValidateUpdate("Ambient", 15, "blue", trained, 100, null));
        Assert.Equal("Colour must be a hex value like #2563eb", LoadTypesService.ValidateUpdate("Ambient", 15, "#2563e", trained, 100, null));
        Assert.Equal("Priority must be between 1 and 999", LoadTypesService.ValidateUpdate("Ambient", 15, "#2563EB", trained, 0, null));
        Assert.Equal("Priority must be between 1 and 999", LoadTypesService.ValidateUpdate("Ambient", 15, "#2563EB", trained, double.NaN, null));
        // The bands are checked in the order lt90, lt60, lt30; null charges the trained rate.
        Assert.Equal("lt60 minutes must be positive", LoadTypesService.ValidateUpdate("Ambient", 15, "#2563eb", new(null, 0, -1), 100, null));
        Assert.Equal("Unknown sub-task", LoadTypesService.ValidateUpdate("Ambient", 15, "#2563eb", trained, 100, "pallets"));
        Assert.Null(LoadTypesService.ValidateUpdate("Ambient", 15, "#2563eb", new(18, null, 25), 999, "ambient_last"));
        Assert.Null(LoadTypesService.ValidateUpdate("Ambient", 15, "#2563eb", trained, 1, ""));
    }

    [Fact]
    public void A_sub_category_is_cleaned_as_cleanSub_does()
    {
        Assert.Equal("Enter a sub-category name.", NonOpRules.Clean(new(" ", null, null, null)).Error);
        Assert.Equal("Default must be 0 or more.", NonOpRules.Clean(new("Meeting", -1, null, null)).Error);
        Assert.Equal("Default must be 0 or more.", NonOpRules.Clean(new("Meeting", double.NaN, null, null)).Error);
        Assert.Equal("Maximum must be 0 or more.", NonOpRules.Clean(new("Meeting", null, -5, null)).Error);
        Assert.Equal("Default time cannot exceed the maximum.", NonOpRules.Clean(new("Meeting", 60, 30, null)).Error);
        // Postgres refused a fraction for its integer columns.
        Assert.Equal("invalid input syntax for type integer: \"7.5\"", NonOpRules.Clean(new("Meeting", 7.5, null, null)).Error);

        var (row, error) = NonOpRules.Clean(new("  Store meeting ", 30, 60, "  ", [4, 0, 2]));
        Assert.Null(error);
        Assert.Equal("Store meeting", row!.Name);
        Assert.Equal(30, row.DefaultMinutes);
        Assert.Equal(60, row.MaxMinutes);
        Assert.Null(row.Note); // a blank note is no note
        Assert.Equal([0, 2, 4], row.DaysAllowed); // sorted
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], NonOpRules.Clean(new("Any day", null, null, "As approved by EMSO", [])).Row!.DaysAllowed);
        Assert.Equal("Any", NonOpRules.DaysLabel([0, 1, 2, 3, 4, 5, 6]));
        Assert.Equal("MWF", NonOpRules.DaysLabel([0, 2, 4]));
        Assert.Equal("1 recorded entry uses this sub-category.", NonOpRules.EntriesUseSubcategory(1));
        Assert.Equal("3 recorded entries depend on this category's sub-categories.", NonOpRules.EntriesDependOnCategory(3));
    }

    [Fact]
    public void The_stores_csv_skips_a_header_and_stops_at_a_short_line()
    {
        var parsed = StoreCsv.Parse("store_number,name,state,opening_date\r\n 10 , Newtown , nsw , 2026-09-01\n\n11,Hillview,vic\n");
        Assert.Null(parsed.Error);
        Assert.Equal([new NewStore("10", "Newtown", "NSW", "2026-09-01"), new NewStore("11", "Hillview", "VIC", null)], parsed.Rows);
        // No header: the first line is a store.
        Assert.Single(StoreCsv.Parse("12,Bayside,QLD").Rows);
        Assert.Equal("Line 2: expected at least code, name, state", StoreCsv.Parse("12,Bayside,QLD\n13,Lakeside").Error);
        Assert.Empty(StoreCsv.Parse("  \n ").Rows);
    }

    [Fact]
    public void The_variance_thresholds_are_held_as_the_action_holds_them()
    {
        Assert.Equal(100, VarianceReportService.Clamp(150, 100));
        Assert.Equal(0, VarianceReportService.Clamp(-5, 500));
        Assert.Equal(0, VarianceReportService.Clamp(double.NaN, 500)); // Number(v) || 0
        Assert.Equal(12.5, VarianceReportService.Clamp(12.5, 500));
        var today = new DateOnly(2026, 11, 25);
        Assert.Equal(new DateOnly(2026, 11, 24), VarianceReportService.DateFor(null, today));
        Assert.Equal(new DateOnly(2026, 11, 24), VarianceReportService.DateFor("25/11/2026", today));
        Assert.Equal(new DateOnly(2026, 10, 1), VarianceReportService.DateFor("2026-10-01", today));
    }

    [Fact]
    public void The_views_matrix_defaults_to_visible_and_saves_every_cell()
    {
        ViewTab[] tabs = [new("/overview", "Dashboard", "Overview"), new("/plan/sales", "Sales", "Plan & Execute")];
        var saved = ViewsMatrix.Saved(tabs, new Dictionary<string, bool> { ["PA:/plan/sales"] = false, ["SM:/gone"] = false });
        Assert.Equal(8, saved.Count); // 2 tabs × 4 levels; a stored cell for a page no longer in the menu is not shown
        Assert.False(saved["PA:/plan/sales"]);
        Assert.True(saved["DSO:/overview"]);
        var cells = ViewsMatrix.Cells(tabs, saved);
        Assert.Equal(8, cells.Count);
        Assert.Equal(new VisibilityCell("SM", "/overview", true), cells[0]);
        Assert.Equal(new VisibilityCell("PA", "/plan/sales", false), cells[5]);
    }
}
