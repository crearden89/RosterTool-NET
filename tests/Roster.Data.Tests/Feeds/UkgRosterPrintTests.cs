// Port of src/app/api/print/ukg-roster/route.test.ts.
// The print roster's weekend-off lines and sub-task codes, end to end through the real
// service and the real loaders around it (store days, sales overrides, holidays, published
// sub-tasks) on an in-memory database. The route's session check is in the web endpoint.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using Roster.Data.Entities;
using Roster.Data.Feeds;
using Roster.Data.Scope;
using Roles = Roster.Engine.Core.Roles;

namespace Roster.Data.Tests.Feeds;

public sealed partial class UkgRosterPrintTests : IDisposable
{
    // Fortnight Mon 31/08/2026 - Sun 13/09/2026: Saturdays are day 5 and day 12.
    private const int Sat1 = 5, Sun1 = 6, Sat2 = 12, Sun2 = 13;
    private static readonly DateOnly Start = new(2026, 8, 31);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private readonly Guid _store;
    private readonly Guid _e1, _e2, _e3;

    public UkgRosterPrintTests()
    {
        _connection.Open();
        var configuration = new ConfigurationBuilder().Build();
        _services = new ServiceCollection()
            .AddLogging()
            .AddRosterData(b => b.UseSqlite(_connection), configuration)
            .BuildServiceProvider();
        using var db = System();
        db.Database.EnsureCreated();
        var group = new DirectorGroup { Name = "Example Group" };
        db.DirectorGroups.Add(group);
        db.SaveChanges();
        var region = new Region { Name = "901 - Example Region", DirectorGroupId = group.Id };
        db.Regions.Add(region);
        db.SaveChanges();
        var store = new Store { Code = "G999", Name = "Test Store", Slug = "g999", RegionId = region.Id, State = AuStates.Nsw, KioskToken = "kiosk-g999" };
        db.Stores.Add(store);
        db.SaveChanges();
        _store = store.Id;
        Employee Emp(string name, string role) =>
            new() { StoreId = store.Id, Name = name, Role = role, ContractHours = 76, NotionalHours = null, Active = true };
        var (e1, e2, e3) = (Emp("Alex Chen", Roles.SA), Emp("Kim Patel", Roles.SA), Emp("Sam Rivera", Roles.SM));
        db.Employees.AddRange(e1, e2, e3);
        db.SaveChanges();
        (_e1, _e2, _e3) = (e1.Id, e2.Id, e3.Id);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private RosterDbContext System() =>
        _services.GetRequiredService<IRosterDbContextFactory>().CreateSystem("print roster tests");

    private GeneratedShift Shift(Guid emp, string role, DateOnly date, int startHour, int endHour, int breakMinutes) => new()
    {
        StoreId = _store, EmployeeId = emp, EmployeeName = "x", Role = role, Date = date,
        StartTime = new TimeOnly(startHour, 0), EndTime = new TimeOnly(endHour, 0), BreakMinutes = breakMinutes,
    };

    private void WeekendShiftsAndMarkers(bool markers = true)
    {
        using var db = System();
        db.GeneratedShifts.AddRange(
            Shift(_e2, Roles.SA, new DateOnly(2026, 9, 5), 8, 14, 30),
            Shift(_e3, Roles.SM, new DateOnly(2026, 9, 13), 9, 17, 30));
        if (markers)
            db.WeekendMarkers.AddRange(
                new WeekendMarker { EmployeeId = _e1, WeekendStart = new DateOnly(2026, 9, 5), SatOff = true, SunOff = false },
                new WeekendMarker { EmployeeId = _e3, WeekendStart = new DateOnly(2026, 9, 12), SatOff = false, SunOff = true });
        db.SaveChanges();
    }

    private async Task<string> Print(bool subtasks = false)
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AccessContextAccessor>().Current = AccessContext.System("print roster tests");
        return await scope.ServiceProvider.GetRequiredService<IUkgRosterFiles>().PrintHtmlAsync(_store.ToString(), Start, subtasks);
    }

    [GeneratedRegex("""<td class="shift[^"]*"[^>]*>[\s\S]*?</td>""")]
    private static partial Regex DayCell();

    // One person's 14 day cells, Monday first.
    private static List<string> Days(string html, string name)
    {
        var row = html.Split("<tr>").FirstOrDefault(r => r.Contains($">{name}<")) ?? "";
        return DayCell().Matches(row).Select(m => m.Value).ToList();
    }

    private static bool Struck(string cell) => Regex.IsMatch(cell, """class="[^"]*\boff\b""");

    [Fact]
    public async Task Strikes_through_only_the_weekend_day_the_Weekends_page_marks_off()
    {
        WeekendShiftsAndMarkers();
        var alex = Days(await Print(), "Alex Chen");
        Assert.Equal(14, alex.Count);
        Assert.True(Struck(alex[Sat1]));
        Assert.Equal([false, false, false], new[] { Sun1, Sat2, Sun2 }.Select(i => Struck(alex[i])));
        Assert.Single(alex.Where(Struck));
    }

    [Fact]
    public async Task Leaves_everyone_else_weekend_cells_unstruck_blank_where_not_rostered()
    {
        WeekendShiftsAndMarkers();
        var kim = Days(await Print(), "Kim Patel");
        Assert.DoesNotContain(kim, Struck);
        Assert.Contains("08:00–14:00", kim[Sat1]);
        Assert.Matches("<td[^>]*></td>", kim[Sun1]);
    }

    [Fact]
    public async Task Prints_a_shift_given_anyway_on_a_label_over_the_lines_so_the_clash_shows()
    {
        WeekendShiftsAndMarkers();
        var sam = Days(await Print(), "Sam Rivera");
        Assert.True(Struck(sam[Sun2]));
        Assert.Contains("""<span class="over">09:00–17:00</span>""", sam[Sun2]);
    }

    [Fact]
    public async Task Draws_the_lines_as_vector_strokes_which_print_whatever_the_print_settings()
    {
        WeekendShiftsAndMarkers();
        var html = await Print();
        // Not a CSS background: those print as a coarse bitmap, or not at all
        // with "Background graphics" off.
        Assert.Contains("""<use href="#off-lines"/>""", Days(html, "Alex Chen")[Sat1]);
        Assert.Single(Regex.Matches(html, """<path id="off-lines" d="M"""));
        Assert.DoesNotContain("linear-gradient", html);
        Assert.Contains("Black diagonal lines: marked off for that weekend day on the Weekends page", html);
    }

    [Fact]
    public async Task Prints_no_key_when_nobody_is_marked_off()
    {
        WeekendShiftsAndMarkers(markers: false);
        var html = await Print();
        Assert.DoesNotContain("Black diagonal lines", html);
        Assert.DoesNotContain("off-lines", html);
        Assert.DoesNotContain(Days(html, "Alex Chen"), Struck);
    }

    private void PublishedTiles()
    {
        using var db = System();
        var s1 = Shift(_e2, Roles.SA, new DateOnly(2026, 9, 1), 6, 10, 0);
        var s2 = Shift(_e1, Roles.SA, new DateOnly(2026, 9, 2), 6, 8, 0);
        db.GeneratedShifts.AddRange(s1, s2);
        db.SaveChanges();
        ShiftTaskBlock Tile(GeneratedShift s, int hour, int minute, string group, string? sub) =>
            new() { ShiftId = s.Id, BlockTime = new TimeOnly(hour, minute), LabourGroup = group, SubTask = sub };
        db.ShiftTaskBlocks.AddRange(
            // Published with labels.
            Tile(s1, 6, 0, "load_running", "produce"),
            Tile(s1, 6, 15, "load_running", "ambient"),
            Tile(s1, 6, 30, "checkout", null),
            Tile(s1, 6, 45, "cleaning", "front_end"),
            // Published before labels were kept: labelled when printed.
            Tile(s2, 6, 0, "cleaning", null),
            Tile(s2, 6, 15, "cleaning", null));
        db.SaveChanges();
    }

    [Fact]
    public async Task Prints_exactly_as_before_when_sub_tasks_are_not_asked_for()
    {
        PublishedTiles();
        var html = await Print();
        Assert.DoesNotContain("""class="subs""", html);
        Assert.DoesNotContain("Sub-tasks, in the order", html);
        Assert.Equal("""<td class="shift">06:00–10:00</td>""", Days(html, "Kim Patel")[1]);
    }

    [Fact]
    public async Task With_sub_tasks_on_shows_each_shift_codes_in_the_order_worked_and_a_legend()
    {
        PublishedTiles();
        var html = await Print(subtasks: true);
        Assert.Contains("Sub-tasks, in the order each person works them", html);
        Assert.Contains("RE Re-runs", html);
        Assert.Contains("""<div class="subs">PR AM FE</div>""", Days(html, "Kim Patel")[1]);
        // The unlabelled published rows were labelled on read, as Cleaning sub-tasks.
        Assert.Matches("""<div class="subs">(FE|SF|BH)( (FE|SF|BH))?</div>""", Days(html, "Alex Chen")[2]);
    }
}
