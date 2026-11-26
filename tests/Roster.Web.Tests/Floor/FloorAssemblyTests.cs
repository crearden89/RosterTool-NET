// New: page.tsx has no tests of its own; these pin down the parts of it that
// FloorAssembly carries (the links, the crew list, the empty messages, the
// load link), so the wall screen keeps the TypeScript's words and addresses.

using Roster.Engine.Core;
using Roster.Engine.Walk;
using Roster.Web.Features.Floor;

namespace Roster.Web.Tests.Floor;

public class FloorAssemblyTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static FloorReads Reads(DateOnly? day = null, bool approved = true, IReadOnlyList<FloorShiftRow>? shifts = null) => new()
    {
        Token = "tok",
        StoreName = "Example Store",
        StoreCode = "G901",
        KioskToken = "tok",
        NoLoginNeeded = true,
        Day = day ?? Today,
        LocalDate = Today,
        LocalMinutes = 9 * 60 + 30,
        DateGiven = day is not null,
        ShowNotes = false,
        Shifts = shifts ??
        [
            new("s1", "e2", "Zoe Taylor", Roles.SA, 9 * 60, 13 * 60, 0),
            new("s2", "e1", "Alex Morgan", Roles.SM, 6 * 60, 10 * 60, 0),
            new("s3", "e1", "Alex Morgan", Roles.SM, 14 * 60, 16 * 60, 0),
        ],
        Blocks = [new("s1", 9 * 60, "checkout"), new("s2", 6 * 60, "load_running")],
        AdminTaskRules = [],
        McoRegisters = 4,
        ScoLanes = 6,
        EmployeeCount = 30,
        Hours = new FloorTradingHours(8 * 60 + 30, 20 * 60, false),
        RegionName = "501 - Example North",
        PublicHoliday = false,
        LoadTypes = [],
        Loads = [],
        LoadHistory = [],
        CheckedNames = [],
        Notes =
        [
            new WalkNote("n1", "Open one", null, null, Today.AddDays(-2), null),
            new WalkNote("n2", "Done today", null, null, Today, DateTimeOffset.Parse("2026-11-20T01:00:00Z")),
        ],
        People = [],
        Approved = approved,
        Drifted = false,
        ApprovedAt = null,
        RegeneratedAt = null,
    };

    private static FloorScreenView Screen(FloorReads r)
    {
        var raw = FloorAssembly.RawShifts(r);
        return FloorAssembly.ScreenView(r, raw, Display.BuildRosterRows(raw), []);
    }

    [Fact]
    public void Keeps_the_links_date_on_the_notes_link_and_the_back_link()
    {
        Assert.Equal("/floor/tok?view=notes", FloorAssembly.KeepDate(Reads(), "notes"));
        Assert.Equal("/floor/tok", FloorAssembly.KeepDate(Reads(), null));
        var other = Reads(new DateOnly(2026, 11, 18));
        Assert.Equal("/floor/tok?view=notes&date=2026-11-18", FloorAssembly.KeepDate(other, "notes"));
        Assert.Equal("/floor/tok?date=2026-11-18", FloorAssembly.KeepDate(other, null));
    }

    [Fact]
    public void Lists_each_person_on_today_once_by_name()
    {
        Assert.Equal([new CrewOption("e1", "Alex Morgan"), new CrewOption("e2", "Zoe Taylor")], FloorAssembly.Crew(Reads()));
    }

    [Fact]
    public void Reads_the_regions_three_digit_prefix()
    {
        Assert.Equal("501", FloorAssembly.RegionPrefix("501 - Example North"));
        Assert.Null(FloorAssembly.RegionPrefix("Example North"));
        Assert.Null(FloorAssembly.RegionPrefix(null));
    }

    [Fact]
    public void Shows_only_an_approved_roster_and_says_why_not()
    {
        var approved = Screen(Reads());
        Assert.Null(approved.EmptyMessage);
        Assert.Equal(3, approved.Rows.Count);
        Assert.Equal("Alex Morgan", approved.Rows[0].Name); // the manager first

        var unapproved = Screen(Reads(approved: false));
        Assert.Empty(unapproved.Rows);
        Assert.Equal(new EmptyMessage("This day's roster has not been approved yet.", "It shows here once the Store Manager approves it."), unapproved.EmptyMessage);

        Assert.Equal(new EmptyMessage("No approved roster for today.", ""), Screen(Reads(shifts: [])).EmptyMessage);
        Assert.Equal(new EmptyMessage("No approved roster for this day.", ""), Screen(Reads(new DateOnly(2026, 11, 18), shifts: [])).EmptyMessage);
    }

    [Fact]
    public void Links_the_strip_to_the_Load_Planner_today_only_and_stands_at_now_today_only()
    {
        var today = Screen(Reads());
        Assert.Equal("/load/tok?from=floor", today.LoadHref);
        Assert.Equal(9 * 60 + 30, today.Now);
        Assert.True(today.CanTick);
        Assert.Equal("Fri 20 Nov · 9:30 am", today.Header.When);

        var other = Screen(Reads(new DateOnly(2026, 11, 18)));
        Assert.Null(other.LoadHref);
        Assert.Null(other.Now);
        Assert.False(other.CanTick);
        Assert.All(other.Rows, r => Assert.Null(r.State));
        Assert.Equal("Wed 18 Nov", other.Header.When);
        Assert.True(other.Header.OtherDay);
    }

    [Fact]
    public void The_notes_view_backs_Undo_with_todays_completed_notes()
    {
        var notes = FloorAssembly.NotesView(Reads());
        Assert.Equal(["n1"], notes.Notes.Select(n => n.Id));
        Assert.Equal(["n2"], notes.CompletedToday.Select(n => n.Id));
        Assert.Equal("/floor/tok", notes.BackHref);
    }
}
