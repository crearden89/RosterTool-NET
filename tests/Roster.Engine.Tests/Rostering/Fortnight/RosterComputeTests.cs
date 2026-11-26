// New: the pure part of src/lib/stage2/compute.ts has no TypeScript test of its
// own (its harness runs it over the in-memory database). These check each
// decision it makes on the reads: who is on the team, and every employee's day.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Fortnight.FortnightKit;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class RosterComputeTests
{
    private static readonly DateOnly Monday = new(2026, 8, 3);

    private static DateOnly D(int i) => Monday.AddDays(i);

    private static TimeOnly T(double hours) => new((int)hours, (int)((hours - (int)hours) * 60));

    private const string BreakNote = "Longer shift so a break doesn't cost work: 0.25 h - Store assistant now 09:00–15:15 (finishes 15 minutes later), so the work a break would push out is still done in its window";

    private static FortnightSources Sources()
    {
        List<PlanSlot> slots = [Slot("sa-1", "SA", 9 * H, 15 * H)];
        var open = new Stage1Computation
        {
            HasData = true,
            Closed = false,
            StaffOpen = 6 * H,
            StaffClose = 21 * H + 30,
            TradingOpen = 8 * H,
            TradingClose = 20 * H,
            Windows = [new WindowRule("reset", 6 * H, 21 * H + 30)],
        };
        var shaped = new ShapedDay { Slots = slots, DemandByBlock = SlotDemand(slots), Flags = [BreakNote] };
        return new FortnightSources
        {
            PeriodStart = Monday,
            Employees =
            [
                new FortnightEmployeeRow { Id = "a", Name = "Ava", Role = "SA", ContractHours = 76, Active = true },
                new FortnightEmployeeRow { Id = "b", Name = "Ben", Role = "SA", ContractHours = 76, NotionalHours = 6, Active = true },
                new FortnightEmployeeRow { Id = "x", Name = "Xan", Role = "SA", ContractHours = 76, Active = false },
            ],
            Availability =
            [
                new AvailabilityRow { EmployeeId = "b", DayOfWeek = 3, Available = true, FromTime = T(8), ToTime = T(12) },
                new AvailabilityRow { EmployeeId = "b", DayOfWeek = 4, Available = false },
            ],
            Leave = [new LeaveRow { EmployeeId = "a", StartDate = D(0), EndDate = D(1), DayHours = 4 }],
            Restricted = [new RestrictedTaskRow { EmployeeId = "b", LabourGroup = "checkout" }],
            // A two-week cycle anchored on the period's Monday: its second week has a Wednesday shift.
            FixedRosters = [new FixedRosterRow { EmployeeId = "a", WeekIndex = 1, DayOfWeek = 2, StartTime = T(10), EndTime = T(14) }],
            FixedCycles = [new CycleRow("a", 2, Monday)],
            BreakRules = BreakRules,
            WeekendMarkers =
            [
                new WeekendMarkerRow { EmployeeId = "a", WeekendStart = D(5), SatOff = true, SunOff = false },
                new WeekendMarkerRow { EmployeeId = "b", WeekendStart = D(5), SatOff = false, SunOff = true },
            ],
            DayOffs = [new DayOffRow { EmployeeId = "a", Date = D(8) }, new DayOffRow { EmployeeId = "b", Date = D(0) }],
            // A locked shift is kept even on a granted day off.
            LockedShifts = [new LockedShiftRow { EmployeeId = "b", Date = D(0), StartTime = T(9), EndTime = T(13) }],
            SchoolCalendar = new SchoolCalendar
            {
                Terms = [new SchoolTerm(new DateOnly(2026, 7, 13), new DateOnly(2026, 9, 18))],
                ExceptionDates = [D(2)],
            },
            Holidays = [D(7)],
            DaySlots = Enumerable.Range(0, 14)
                .Select(i => i == 0 ? new DaySlots(shaped, open) : new DaySlots(null, new Stage1Computation { HasData = false }))
                .ToList(),
        };
    }

    [Fact]
    public void Rosters_the_active_team_only()
    {
        var input = RosterCompute.BuildFortnightInput(Sources());
        Assert.Equal(["a", "b"], input.Employees.Select(e => e.Id));
        Assert.Equal(["checkout"], input.Employees[1].Restricted);
        Assert.Equal(6, input.Employees[1].NotionalHours);
        Assert.All(input.Days, d => Assert.Equal(["a", "b"], d.Avail.Keys()));
    }

    [Fact]
    public void Collapses_leave_markers_day_offs_windows_fixed_rosters_and_locks_into_each_day()
    {
        var days = RosterCompute.BuildFortnightInput(Sources()).Days;
        DayAvail A(int i, string id) => days[i].Avail[id];

        // Leave: unavailable, with its hours per day.
        Assert.Equal(new DayAvail { OnLeave = true, LeaveDayHours = 4, Available = false }, A(0, "a"));
        Assert.True(A(1, "a").OnLeave);
        Assert.False(A(2, "a").OnLeave);
        // A locked shift on a granted day off: available, and fixed at its times.
        Assert.Equal(new DayAvail { Available = true, Fixed = new MinuteWindow(9 * H, 13 * H) }, A(0, "b"));
        // Weekend markers: Saturday off for one, Sunday off for the other.
        Assert.False(A(5, "a").Available);
        Assert.True(A(6, "a").Available);
        Assert.True(A(5, "b").Available);
        Assert.False(A(6, "b").Available);
        // A granted day off.
        Assert.False(A(8, "a").Available);
        // Standing availability: a window, and a weekday off.
        Assert.Equal((8 * H, 12 * H), (A(3, "b").FromMin!.Value, A(3, "b").ToMin!.Value));
        Assert.False(A(4, "b").Available);
        Assert.Null(A(3, "a").FromMin);
        // The fixed-roster cycle: week 2 of the cycle has the Wednesday shift, week 1 has none.
        Assert.Null(A(2, "a").Fixed);
        Assert.Equal(new MinuteWindow(10 * H, 14 * H), A(9, "a").Fixed);
    }

    [Fact]
    public void Gives_each_date_its_week_school_day_holiday_and_plan()
    {
        var days = RosterCompute.BuildFortnightInput(Sources()).Days;
        Assert.Equal(Enumerable.Range(0, 14).Select(i => D(i).ToIso()), days.Select(d => d.Date));
        Assert.Equal(Enumerable.Range(0, 14).Select(i => i < 7 ? 0 : 1), days.Select(d => d.WeekIndex));
        Assert.True(days[0].IsSchoolDay);
        Assert.False(days[2].IsSchoolDay); // a pupil-free day
        Assert.False(days[5].IsSchoolDay); // Saturday
        Assert.True(days[7].PublicHoliday);
        Assert.False(days[0].Closed);
        Assert.True(days[1].Closed); // no data
        Assert.Single(days[0].Slots);
        Assert.Equal(15, days[0].BreakTimeAddedMin); // the plan's note: 0.25 h already added
        Assert.NotNull(days[0].AllowedGroups);
        Assert.Null(days[1].AllowedGroups);
        Assert.Null(days[0].HeldShifts);
    }

    [Fact]
    public void Puts_the_plans_notes_in_front_of_the_rosters_own_flags_for_every_date()
    {
        var roster = RosterCompute.ComputeRosterFortnight(Sources());
        Assert.Equal(14, roster.ByDate.Size);
        var day0 = roster.ByDate[Monday];
        Assert.Equal(BreakNote, day0.Flags[0]);
        Assert.True(day0.HasData);
        Assert.Contains(day0.Shifts, s => s.EmployeeId == "b" && s.Locked);
        var closed = roster.ByDate[D(1)];
        Assert.False(closed.HasData);
        Assert.Empty(closed.Shifts);
        Assert.Equal(["a", "b"], roster.Outcomes.Select(o => o.EmployeeId));
        // Leave credits 4 h on the open Monday and nothing on the closed Tuesday;
        // the public holiday not worked credits the notional day (76 / 10).
        Assert.Equal(4 + 7.6, roster.Outcomes[0].CreditedHours, 9);
    }

    [Fact]
    public void Wants_one_day_of_slots_per_date()
    {
        var src = Sources() with { DaySlots = [] };
        Assert.Throws<ArgumentException>(() => RosterCompute.ComputeRosterFortnight(src));
    }
}
