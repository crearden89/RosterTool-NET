// New: the Team Members service against the data layer (SQLite): what the page loads,
// and the actions of setup/staff/actions.ts and setup/requests/actions.ts with their
// scope checks, weekend-marker sync and fixed-roster rules.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Web.Features.Team;

namespace Roster.Web.Tests.Team;

public sealed class TeamServiceTests : IDisposable
{
    private readonly TeamTestDb _db = new();

    public void Dispose() => _db.Dispose();

    // 05/09/2026 is a Saturday.
    private static readonly DateOnly Saturday = new(2026, 9, 5);

    [Fact]
    public async Task Loads_the_chosen_stores_active_team_in_rank_order()
    {
        _db.AddEmployee(TeamTestDb.StoreB, "6-Quinlan, Mia", "JSA", 0);
        _db.AddEmployee(TeamTestDb.StoreB, "1-Okoro, Dana", "SM", 90);
        _db.AddEmployee(TeamTestDb.StoreB, "4-Ashby, Lou", active: false);
        _db.AddEmployee(TeamTestDb.StoreA, "4-Nye, Harper");

        var page = await _db.Team.LoadAsync(TeamTestDb.StoreB);
        Assert.Equal(TeamTestDb.StoreB, page.StoreId);
        Assert.Equal(["G101", "G102", "G103"], page.Stores.Select(s => s.Code));
        Assert.Equal(["1-Okoro, Dana", "6-Quinlan, Mia"], page.Employees.Select(e => e.Name));
        Assert.Equal(14, page.Availability.Count);
        Assert.False(page.ReadOnly);
        Assert.Contains(page.Positions, p => p.Code == "SM-45-3" && p.WeeklyHours == 45 && p.YearBand == 3);

        // No store asked for: the one last chosen, else the first.
        _db.RememberedStore = TeamTestDb.StoreC;
        Assert.Equal(TeamTestDb.StoreC, (await _db.Team.LoadAsync(null)).StoreId);
        _db.RememberedStore = null;
        Assert.Equal(TeamTestDb.StoreA, (await _db.Team.LoadAsync(Guid.NewGuid())).StoreId);
    }

    [Fact]
    public async Task A_store_user_is_always_on_their_own_store_and_PA_reads_only()
    {
        _db.SignInAs(Tier.Store, TeamTestDb.StoreA);
        var page = await _db.Team.LoadAsync(TeamTestDb.StoreB);
        Assert.Equal(TeamTestDb.StoreA, page.StoreId);
        Assert.Empty(page.Stores);

        _db.SignInAs(Tier.Pa);
        Assert.True((await _db.Team.LoadAsync(null)).ReadOnly);
        var id = _db.AddEmployee(TeamTestDb.StoreA, "4-Nye, Harper");
        Assert.Equal(ScopeMessages.DeniedReadOnly, (await _db.Team.SetRestrictedTasksAsync(id, ["registers"])).Error);
    }

    [Fact]
    public async Task Adds_a_team_member_by_position_available_every_day()
    {
        Assert.True((await _db.Team.AddEmployeeAsync(TeamTestDb.StoreA, "6-Quinlan, Mia", "JSA-17", 30)).Ok);
        var mia = _db.Read(db => db.Employees.Single(e => e.Name == "6-Quinlan, Mia"));
        Assert.Equal(("JSA", "JSA-17", 0d), (mia.Role, mia.PositionCode, mia.ContractHours));
        Assert.Equal(7, _db.Read(db => db.EmployeeAvailability.Count(a => a.EmployeeId == mia.Id && a.Available)));

        Assert.True((await _db.Team.AddEmployeeAsync(TeamTestDb.StoreA, "1-Okoro, Dana", "SM-45-3", 90)).Ok);
        var dana = _db.Read(db => db.Employees.Single(e => e.Name == "1-Okoro, Dana"));
        Assert.Equal(("SM", 3, 90d), (dana.Role, dana.SmYearBand, dana.ContractHours));

        Assert.Equal("Unknown position 'XX'", (await _db.Team.AddEmployeeAsync(TeamTestDb.StoreA, "Someone", "XX", 10)).Error);
        Assert.Equal("No store", (await _db.Team.AddEmployeeAsync(null, "Someone", "SA-FF", 10)).Error);
        _db.SignInAs(Tier.Emso, areaId: TeamTestDb.AreaNorth);
        Assert.Equal(ScopeMessages.DeniedScope, (await _db.Team.AddEmployeeAsync(TeamTestDb.StoreC, "Someone", "SA-FF", 10)).Error);
    }

    [Fact]
    public async Task Edits_availability_contract_position_and_restrictions()
    {
        var id = _db.AddEmployee(TeamTestDb.StoreA, "4-Nye, Harper");
        var days = Enumerable.Range(0, 7).Select(d => new AvailabilityDay(d, d != 2,
            d < 2 ? new TimeOnly(15, 30) : null, d < 3 ? new TimeOnly(21, 0) : null)).ToList();
        Assert.True((await _db.Team.SetAvailabilityAsync(id, days)).Ok);
        var avail = _db.Read(db => db.EmployeeAvailability.Where(a => a.EmployeeId == id).OrderBy(a => a.DayOfWeek).ToList());
        Assert.Equal(new TimeOnly(15, 30), avail[0].FromTime);
        // A day marked unavailable loses its window.
        Assert.Equal((false, (TimeOnly?)null, (TimeOnly?)null), (avail[2].Available, avail[2].FromTime, avail[2].ToTime));

        Assert.Equal("Contract hours must be between 0 and 76", (await _db.Team.SetContractAndNotionalAsync(id, 80, null)).Error);
        Assert.True((await _db.Team.SetContractAndNotionalAsync(id, 64, 7.5, new DateOnly(2026, 11, 2))).Ok);
        var e = _db.Read(db => db.Employees.Single(x => x.Id == id));
        Assert.Equal((64d, 7.5, new DateOnly(2026, 11, 2)), (e.ContractHours, e.NotionalHours!.Value, e.StartDate!.Value));

        Assert.True((await _db.Team.SetPositionAsync(id, "ASM-35-2", 70)).Ok);
        e = _db.Read(db => db.Employees.Single(x => x.Id == id));
        Assert.Equal(("ASM", "ASM-35-2", 2, 70d), (e.Role, e.PositionCode, e.SmYearBand!.Value, e.ContractHours));
        Assert.True((await _db.Team.SetPositionAsync(id, "JSA-16")).Ok);
        Assert.Equal(0, _db.Read(db => db.Employees.Single(x => x.Id == id).ContractHours));

        Assert.True((await _db.Team.SetRestrictedTasksAsync(id, ["scos", "registers"])).Ok);
        Assert.True((await _db.Team.SetRestrictedTasksAsync(id, ["admin"])).Ok);
        Assert.Equal(["admin"], _db.Read(db => db.EmployeeRestrictedTasks.Where(r => r.EmployeeId == id).Select(r => r.LabourGroup).ToList()));
        Assert.True((await _db.Team.DeleteEmployeeAsync(id)).Ok);
        Assert.False(_db.Read(db => db.Employees.Any(x => x.Id == id)));
    }

    [Fact]
    public async Task Books_and_clears_leave_scope_checked_through_its_owner()
    {
        var id = _db.AddEmployee(TeamTestDb.StoreB, "4-Nye, Harper");
        Assert.Equal("Unknown leave type XYZ", (await _db.Team.AddLeaveAsync(id, Saturday, Saturday, "XYZ")).Error);
        Assert.True((await _db.Team.AddLeaveAsync(id, Saturday, Saturday.AddDays(3), "SCK", 7.6)).Ok);
        var leaveId = _db.Read(db => db.EmployeeLeave.Single(l => l.EmployeeId == id).Id);
        Assert.Equal(7.6, (await _db.Team.LoadAsync(TeamTestDb.StoreB)).Leave.Single().DayHours);

        // Another store's booking is refused as out of scope, not as missing.
        _db.SignInAs(Tier.Store, TeamTestDb.StoreA);
        Assert.Equal(ScopeMessages.DeniedScope, (await _db.Team.ClearLeaveAsync(leaveId)).Error);
        Assert.Equal(ScopeMessages.EmployeeGone, (await _db.Team.ClearLeaveAsync(Guid.NewGuid())).Error);
        _db.SignInAs(Tier.SuperUser);
        Assert.True((await _db.Team.ClearLeaveAsync(leaveId)).Ok);
        Assert.False(_db.Read(db => db.EmployeeLeave.Any()));
    }

    [Fact]
    public async Task A_weekend_day_off_keeps_its_weekend_marker_in_step()
    {
        var id = _db.AddEmployee(TeamTestDb.StoreA, "4-Nye, Harper");
        _db.Seed(db => db.WeekendMarkers.Add(new WeekendMarker { EmployeeId = id, WeekendStart = Saturday, SatOff = false, SunOff = true }));
        Assert.True((await _db.Team.AddDayOffAsync(id, Saturday, null, "Wedding")).Ok);
        var request = _db.Read(db => db.DayOffRequests.Single());
        Assert.Equal(("pending", "Wedding"), (request.Status, request.Notes));

        Assert.True((await _db.Team.SetRequestStatusAsync(request.Id, "granted")).Ok);
        Assert.Equal((true, true), _db.Read(db => db.WeekendMarkers.Where(m => m.EmployeeId == id).Select(m => ValueTuple.Create(m.SatOff, m.SunOff)).Single()));
        Assert.True((await _db.Team.SetRequestStatusAsync(request.Id, "declined")).Ok);
        // Declining clears that day's flag only.
        Assert.Equal((false, true), _db.Read(db => db.WeekendMarkers.Where(m => m.EmployeeId == id).Select(m => ValueTuple.Create(m.SatOff, m.SunOff)).Single()));
        Assert.Equal("declined", _db.Read(db => db.DayOffRequests.Single().Status));

        Assert.True((await _db.Team.DeleteRequestAsync(request.Id)).Ok);
        Assert.False(_db.Read(db => db.DayOffRequests.Any()));
    }

    [Fact]
    public async Task Grants_every_pending_request_of_the_stores_team_at_once()
    {
        var nye = _db.AddEmployee(TeamTestDb.StoreA, "4-Nye, Harper");
        var gone = _db.AddEmployee(TeamTestDb.StoreA, "4-Ashby, Lou", active: false);
        var other = _db.AddEmployee(TeamTestDb.StoreB, "4-Sandoval, Ruby");
        var now = DateTimeOffset.UtcNow;
        _db.Seed(db =>
        {
            DayOffRequest R(Guid e, DateOnly d, string status, int minutes) =>
                new() { Id = Guid.NewGuid(), EmployeeId = e, Date = d, Status = status, SubmittedAt = now.AddMinutes(minutes) };
            db.DayOffRequests.AddRange(
                R(nye, Saturday, "pending", 1), R(nye, Saturday.AddDays(1), "pending", 2), R(nye, Saturday.AddDays(3), "declined", 3),
                R(gone, Saturday.AddDays(4), "pending", 4), R(other, Saturday, "pending", 5));
        });

        // Oldest first on the page, the store's active team only.
        var page = await _db.Team.LoadAsync(TeamTestDb.StoreA);
        Assert.Equal([Saturday, Saturday.AddDays(1), Saturday.AddDays(3)], page.Requests.Select(r => r.Date));

        Assert.True((await _db.Team.GrantAllPendingAsync(TeamTestDb.StoreA)).Ok);
        var statuses = _db.Read(db => db.DayOffRequests.Select(r => new { r.EmployeeId, r.Status }).ToList());
        Assert.Equal(2, statuses.Count(s => s.EmployeeId == nye && s.Status == "granted"));
        Assert.Contains(statuses, s => s.EmployeeId == gone && s.Status == "granted");
        Assert.Contains(statuses, s => s.EmployeeId == other && s.Status == "pending");
        // Both days of the weekend granted in one go: both flags on.
        Assert.Equal((true, true), _db.Read(db => db.WeekendMarkers.Where(m => m.EmployeeId == nye).Select(m => ValueTuple.Create(m.SatOff, m.SunOff)).Single()));

        _db.SignInAs(Tier.Store, TeamTestDb.StoreA);
        Assert.Equal(ScopeMessages.DeniedScope, (await _db.Team.GrantAllPendingAsync(TeamTestDb.StoreB)).Error);
    }

    [Fact]
    public async Task Saves_a_fixed_roster_cycle_after_the_shift_rules()
    {
        var id = _db.AddEmployee(TeamTestDb.StoreA, "4-Nye, Harper", contract: 40);
        _db.Seed(db =>
        {
            db.BreakRules.Add(new Roster.Data.Entities.BreakRule { Role = "SA", MinShiftHours = 3, MaxShiftHours = 9, GapHours = 11, BreakType = "tiered" });
            for (var d = 0; d < 7; d++)
                db.StoreTradingHours.Add(new StoreTradingHours { StoreId = TeamTestDb.StoreA, DayOfWeek = d, OpenTime = new TimeOnly(8, 0), CloseTime = new TimeOnly(d == 6 ? 18 : 21, 0), Closed = false });
        });
        FixedDay Day(int week, int day, int sh, int eh) => new(week, day, new TimeOnly(sh, 0), new TimeOnly(eh, 0));

        Assert.Equal("Week 1 Mon 07:00–17:00 is longer than the 9.50-hour maximum for a SA.",
            (await _db.Team.SetFixedRosterCycleAsync(id, 2, Saturday, [Day(0, 0, 7, 17)])).Error);
        Assert.Equal("Week 2 Sun 12:00–19:00 runs past the store's close (18:00) and its 30-minute close-down.",
            (await _db.Team.SetFixedRosterCycleAsync(id, 2, Saturday, [Day(0, 0, 8, 17), Day(1, 6, 12, 19)])).Error);
        Assert.Equal("Cycle must be 1, 2 or 4 weeks", (await _db.Team.SetFixedRosterCycleAsync(id, 3, Saturday, [])).Error);

        // Five 9h shifts over two weeks: 8.5 paid each, 42.5 a fortnight, over the 40h contract.
        var days = new[] { Day(0, 0, 8, 17), Day(0, 2, 8, 17), Day(0, 4, 8, 17), Day(1, 1, 8, 17), Day(1, 3, 8, 17) };
        Assert.True((await _db.Team.SetFixedRosterCycleAsync(id, 2, Saturday, days)).Ok);
        var cycle = _db.Read(db => db.FixedRosterCycles.Single());
        // Week 1 is anchored on the Monday of the date entered.
        Assert.Equal((2, new DateOnly(2026, 8, 31)), (cycle.CycleWeeks, cycle.StartDate));
        var page = await _db.Team.LoadAsync(TeamTestDb.StoreA);
        Assert.Equal(5, page.Fixed.Count);
        Assert.Equal(42.5, page.FixedPaidHours[id]);

        // Fewer days replace the pattern; none removes it, cycle included.
        Assert.True((await _db.Team.SetFixedRosterCycleAsync(id, 1, Saturday, [Day(0, 1, 9, 15)])).Ok);
        Assert.Equal(1, _db.Read(db => db.FixedRosters.Count()));
        Assert.Equal(1, _db.Read(db => db.FixedRosterCycles.Single().CycleWeeks));
        Assert.True((await _db.Team.SetFixedRosterCycleAsync(id, 1, Saturday, [])).Ok);
        Assert.False(_db.Read(db => db.FixedRosterCycles.Any() || db.FixedRosters.Any()));

        Assert.True((await _db.Team.SetFixedRosterCycleAsync(id, 4, Saturday, [Day(3, 1, 9, 15)])).Ok);
        Assert.True((await _db.Team.ClearFixedRosterAsync(id)).Ok);
        Assert.False(_db.Read(db => db.FixedRosterCycles.Any() || db.FixedRosters.Any()));
    }
}
