// New: the Super User page services against the data layer's model (in-memory SQLite):
// what each action writes, what it refuses, and that only a Super User may call it.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Admin.Calendars;
using Roster.Web.Features.Admin.DataFeed;
using Roster.Web.Features.Admin.KioskLinks;
using Roster.Web.Features.Admin.LoadTypes;
using Roster.Web.Features.Admin.NonOpCategories;
using Roster.Web.Features.Admin.Shared;
using Roster.Web.Features.Admin.SpecialTradingHours;
using Roster.Web.Features.Admin.StoreAttributes;

namespace Roster.Web.Tests.AdminPages;

public sealed class AdminServiceTests : IDisposable
{
    private readonly AdminTestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Only_a_super_user_may_call_an_action()
    {
        var (_, stores) = _db.Organisation("G101");
        _db.SignInAs(new ScopedUser(Guid.NewGuid(), "store.manager", Tier.Store, stores[0], null, null, null));
        var service = new KioskLinksService(_db.Services);
        await Assert.ThrowsAsync<SuperUserRequiredException>(() => service.RotateKioskTokenAsync(stores[0]));
        await Assert.ThrowsAsync<SuperUserRequiredException>(() => service.LoadAsync());
    }

    [Fact]
    public async Task Rotating_a_kiosk_token_issues_a_new_32_digit_hex_token()
    {
        var (_, stores) = _db.Organisation("G102", "G101");
        var service = new KioskLinksService(_db.Services);
        var before = await service.LoadAsync();
        Assert.Equal(["G101", "G102"], before.Select(s => s.Code)); // by code
        Assert.True((await service.RotateKioskTokenAsync(stores[1])).Ok);
        var after = (await service.LoadAsync()).Single(s => s.Id == stores[1]);
        Assert.NotEqual(before.Single(s => s.Id == stores[1]).KioskToken, after.KioskToken);
        Assert.Matches("^[0-9a-f]{32}$", after.KioskToken);
        Assert.Equal($"/floor/{after.KioskToken}", after.FloorPath);
    }

    [Fact]
    public async Task A_feed_token_needs_a_label_and_rotates_to_a_new_64_digit_token()
    {
        var service = new DataFeedService(_db.Services);
        Assert.Equal("Give the token a label (e.g. 'area-db-sync').", (await service.CreateFeedTokenAsync("   ")).Error);
        Assert.True((await service.CreateFeedTokenAsync("  area-db-sync ")).Ok);
        var token = Assert.Single((await service.LoadAsync()).Tokens);
        Assert.Equal("area-db-sync", token.Label);
        Assert.Matches("^[0-9a-f]{64}$", token.Token);
        Assert.True((await service.RotateFeedTokenAsync(token.Id)).Ok);
        Assert.NotEqual(token.Token, Assert.Single((await service.LoadAsync()).Tokens).Token);
        Assert.True((await service.DeleteFeedTokenAsync(token.Id)).Ok);
        Assert.Empty((await service.LoadAsync()).Tokens);
    }

    [Fact]
    public async Task Special_hours_list_latest_first_and_a_closed_range_keeps_no_times()
    {
        var (_, stores) = _db.Organisation("G101", "G102");
        var service = new SpecialTradingHoursService(_db.Services);
        Assert.True((await service.AddSpecialHoursAsync(new(stores[1], "2026-12-24", "2026-12-24", false, "08:30", "18:00", " Christmas Eve "))).Ok);
        Assert.True((await service.AddSpecialHoursAsync(new(stores[1], "2026-12-25", "2026-12-25", true, "08:30", "18:00", ""))).Ok);
        Assert.Equal("Open time must be before close time.",
            (await service.AddSpecialHoursAsync(new(stores[1], "2026-12-26", "2026-12-26", false, "18:00", "08:30", ""))).Error);

        // The store asked for wins; then the one last chosen; then the first.
        var view = await service.LoadAsync(stores[1], stores[0]);
        Assert.Equal(stores[1], view.StoreId);
        Assert.Equal(stores[0], (await service.LoadAsync(null, stores[0])).StoreId);
        Assert.Equal(stores[0], (await service.LoadAsync(Guid.NewGuid(), null)).StoreId);

        Assert.Equal([new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 24)], view.Rows.Select(r => r.StartDate));
        var closed = view.Rows[0];
        Assert.True(closed.Closed);
        Assert.Null(closed.OpenTime);
        Assert.Null(closed.Note);
        Assert.Equal("Christmas Eve", view.Rows[1].Note);
        Assert.Equal(new TimeOnly(8, 30), view.Rows[1].OpenTime);

        Assert.True((await service.DeleteSpecialHoursAsync(closed.Id)).Ok);
        Assert.Single((await service.LoadAsync(stores[1], null)).Rows);
    }

    [Fact]
    public async Task Calendar_rows_add_by_state_and_delete_from_their_table()
    {
        var (_, stores) = _db.Organisation("G101");
        var service = new CalendarsService(_db.Services);
        Assert.True((await service.AddNationalHolidayAsync("2026-12-25", "Christmas Day")).Ok);
        Assert.True((await service.AddStateHolidayAsync("VIC", "2026-11-03", "Melbourne Cup")).Ok);
        Assert.Equal("invalid input value for enum au_state: \"XYZ\"", (await service.AddStateHolidayAsync("XYZ", "2026-11-03", "Cup")).Error);
        Assert.True((await service.AddLocalHolidayAsync(stores[0], "2026-03-02", "Show day")).Ok);
        Assert.True((await service.AddSchoolTermAsync("NSW", "Term 4", "2026-10-12", "2026-12-18")).Ok);
        Assert.True((await service.AddSchoolTermExceptionAsync("NSW", "2026-11-02", "")).Ok);

        var view = await service.LoadAsync();
        Assert.Single(view.National);
        Assert.Equal("VIC", Assert.Single(view.State).State);
        Assert.Equal(stores[0], Assert.Single(view.Local).StoreId);
        Assert.Equal("Term 4", Assert.Single(view.Terms).TermLabel);
        Assert.Equal("", Assert.Single(view.Exceptions).Note); // a blank note is stored as typed

        Assert.True((await service.DeleteRowAsync(CalendarTable.PublicHolidaysState, view.State[0].Id)).Ok);
        Assert.Empty((await service.LoadAsync()).State);
    }

    [Fact]
    public async Task A_new_load_type_goes_last_and_a_saved_one_rounds_its_priority()
    {
        var service = new LoadTypesService(_db.Services);
        Assert.True((await service.AddLoadTypeAsync(" Ambient ", 15, "#2563eb")).Ok);
        Assert.True((await service.AddLoadTypeAsync("Freezer", 12, "#0ea5e9")).Ok);
        Assert.Equal("Minutes must be positive", (await service.AddLoadTypeAsync("Bread", -1, "#0ea5e9")).Error);
        var rows = await service.LoadAsync();
        Assert.Equal(["Ambient", "Freezer"], rows.Select(r => r.Name));

        var freezer = rows[1];
        Assert.True((await service.UpdateLoadTypeAsync(freezer.Id, "Freezer", 12, "#0ea5e9", new(14, null, 20), 10.5, "freezer")).Ok);
        var saved = (await service.LoadAsync())[1];
        Assert.Equal(11, saved.Priority); // Math.round
        Assert.Equal(14, saved.MinutesPerPalletLt90);
        Assert.Null(saved.MinutesPerPalletLt60);
        Assert.Equal("freezer", saved.SubTask);

        // Left out, the sub-task mapping is untouched; "" clears it.
        Assert.True((await service.UpdateLoadTypeAsync(freezer.Id, "Freezer", 12, "#0ea5e9")).Ok);
        Assert.Equal("freezer", (await service.LoadAsync())[1].SubTask);
        Assert.True((await service.UpdateLoadTypeAsync(freezer.Id, "Freezer", 12, "#0ea5e9", subTask: "")).Ok);
        Assert.Null((await service.LoadAsync())[1].SubTask);

        Assert.True((await service.DeleteLoadTypeAsync(freezer.Id)).Ok);
        Assert.Single(await service.LoadAsync());
    }

    [Fact]
    public async Task A_sub_category_with_entries_is_only_deleted_when_forced_with_them()
    {
        var (_, stores) = _db.Organisation("G101");
        var service = new NonOpCategoriesService(_db.Services);
        Assert.True((await service.AddCategoryAsync(" Meetings ")).Ok);
        var category = Assert.Single((await service.LoadAsync()).Categories);
        Assert.Equal("Meetings", category.Name);
        Assert.True((await service.AddSubcategoryAsync(category.Id, new("Store meeting", 30, 60, null))).Ok);
        var sub = Assert.Single((await service.LoadAsync()).Subs);
        using (var db = _db.System())
        {
            db.NonOpEntries.AddRange(
                new NonOpEntry { StoreId = stores[0], Date = new DateOnly(2026, 11, 2), SubcategoryId = sub.Id, Minutes = 30 },
                new NonOpEntry { StoreId = stores[0], Date = new DateOnly(2026, 11, 3), SubcategoryId = sub.Id, Minutes = 30 });
            await db.SaveChangesAsync();
        }

        var refused = await service.DeleteSubcategoryAsync(sub.Id);
        Assert.False(refused.Ok);
        Assert.Equal(2, refused.BlockedBy);
        Assert.Equal("2 recorded entries use this sub-category.", refused.Error);

        var category2 = await service.DeleteCategoryAsync(category.Id);
        Assert.Equal("2 recorded entries depend on this category's sub-categories.", category2.Error);
        Assert.Equal(2, category2.BlockedBy);

        Assert.True((await service.DeleteCategoryAsync(category.Id, force: true)).Ok);
        var after = await service.LoadAsync();
        Assert.Empty(after.Categories);
        Assert.Empty(after.Subs);
        using var check = _db.System();
        Assert.Equal(0, await check.NonOpEntries.CountAsync());
    }

    [Fact]
    public async Task The_bulk_upload_lands_valid_rows_and_names_each_rejected_one()
    {
        var (_, stores) = _db.Organisation("G101");
        var categories = new NonOpCategoriesService(_db.Services);
        await categories.AddCategoryAsync("Meetings");
        var category = Assert.Single((await categories.LoadAsync()).Categories);
        await categories.AddSubcategoryAsync(category.Id, new("Store meeting", 30, null, null));
        var upload = new NonOpEntryUpload(_db.Services);

        Assert.Equal("Missing required column(s): Minutes, Store ID",
            (await upload.BulkUploadNonOpEntriesAsync("Day,NonOpHoursCategory,NonOpHoursSubCategory\n02/11/2026,Meetings,Store meeting")).Error);

        const string csv =
            "Day,NonOpHoursCategory,NonOpHoursSubCategory,Description,Minutes,Store ID\n" +
            "02/11/2026, meetings ,STORE MEETING,Weekly,30.4,101\n" +
            "31/02/2026,Meetings,Store meeting,,30,G101\n" +
            "03/11/2026,Meetings,Store meeting,,30,G999\n" +
            "03/11/2026,Training,Store meeting,,30,G101\n" +
            "03/11/2026,Meetings,Huddle,,30,G101\n" +
            "03/11/2026,Meetings,Store meeting,,0,G101\n";
        var result = await upload.BulkUploadNonOpEntriesAsync(csv);
        Assert.True(result.Ok);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(
            [
                new NonOpRowError(3, "Day '31/02/2026' is not a valid DD/MM/YYYY date"),
                new NonOpRowError(4, "Unknown store 'G999'"),
                new NonOpRowError(5, "Unknown category 'Training'"),
                new NonOpRowError(6, "Unknown sub-category 'Huddle' under 'Meetings'"),
                new NonOpRowError(7, "Minutes '0' must be a positive number"),
            ],
            result.RowErrors);
        using var db = _db.System();
        var entry = Assert.Single(db.NonOpEntries);
        Assert.Equal(stores[0], entry.StoreId);
        Assert.Equal(30, entry.Minutes); // Math.round(30.4)
        Assert.Equal("Weekly", entry.Description);
        Assert.Null(entry.EmployeeId);
    }

    [Fact]
    public async Task A_new_store_comes_with_default_attributes_and_seven_open_days()
    {
        var (region, _) = _db.Organisation("G101");
        var service = new StoreAttributesService(_db.Services, _db.Clock);
        Assert.Equal("Store ID and name are required", (await service.AddStoreAsync(region, new("", "Lakeside", "NSW"))).Error);
        Assert.Equal("Invalid state 'nsw'", (await service.AddStoreAsync(region, new("25", "Lakeside", "nsw"))).Error);
        Assert.True((await service.AddStoreAsync(region, new("25", " Lakeside ", "QLD", "2026-09-01"))).Ok);
        // A second store with the same code is refused by the database, named by its code.
        var dup = await service.AddStoreAsync(region, new("G025", "Lakeside Two", "QLD"));
        Assert.False(dup.Ok);
        Assert.StartsWith("G025: ", dup.Error);

        using var db = _db.System();
        var store = db.Stores.Single(s => s.Code == "G025");
        Assert.Equal("Lakeside", store.Name);
        Assert.Equal("g025", store.Slug);
        Assert.Matches("^[0-9a-f]{32}$", store.KioskToken);
        Assert.Equal(new DateOnly(2026, 9, 1), db.StoreAttributes.Single(a => a.StoreId == store.Id).OpeningDate);
        var hours = db.StoreTradingHours.Where(h => h.StoreId == store.Id).OrderBy(h => h.DayOfWeek).ToList();
        Assert.Equal(7, hours.Count);
        Assert.All(hours, h => Assert.Equal((new TimeOnly(7, 0), new TimeOnly(21, 0), false), (h.OpenTime!.Value, h.CloseTime!.Value, h.Closed)));

        // Bulk: stops at the first refused, saying how many were made before it.
        var bulk = await service.BulkAddStoresAsync(region, [new("26", "Parkview", "QLD"), new("27", "Seaview", "XX"), new("28", "Bayview", "QLD")]);
        Assert.Equal(new BulkAddResult(false, 1, "Invalid state 'XX'"), bulk);
    }

    [Fact]
    public async Task Saving_a_store_clamps_its_share_and_keeps_no_times_on_a_closed_day()
    {
        var (_, stores) = _db.Organisation("G101");
        var service = new StoreAttributesService(_db.Services, _db.Clock);
        var view = await service.LoadAsync(stores[0], null);
        Assert.Equal(AttributeValues.Defaults, view.Attributes); // no row yet: the sheet's defaults
        Assert.Equal(("07:00", "21:00", false), (view.Hours[6].OpenTime, view.Hours[6].CloseTime, view.Hours[6].Closed));

        var hours = view.Hours.Select(h => h.DayOfWeek == 6 ? h with { Closed = true } : h with { OpenTime = "08:30" }).ToList();
        var input = view.Attributes with { HasSco = true, ScoShare = 0.95, McoRegisters = 5, OpeningDate = "2019-03-01" };
        Assert.Equal("invalid input syntax for type integer: \"2.5\"",
            (await service.SaveStoreAttributesAsync(stores[0], input with { ScoLanes = 2.5 }, hours)).Error);
        Assert.True((await service.SaveStoreAttributesAsync(stores[0], input, hours)).Ok);

        var saved = await service.LoadAsync(stores[0], null);
        Assert.Equal(0.9, saved.Attributes.ScoShare);
        Assert.True(saved.Attributes.HasSco);
        Assert.Equal(5, saved.Attributes.McoRegisters);
        Assert.Equal("2019-03-01", saved.Attributes.OpeningDate);
        Assert.Equal("08:30", saved.Hours[0].OpenTime);
        Assert.True(saved.Hours[6].Closed);
        using var db = _db.System();
        var sunday = db.StoreTradingHours.Single(h => h.StoreId == stores[0] && h.DayOfWeek == 6);
        Assert.Null(sunday.OpenTime);
        Assert.Null(sunday.CloseTime);
    }
}
