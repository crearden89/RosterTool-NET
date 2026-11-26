// Tests for the ports of src/lib/roster/approval.ts (publishRoster),
// src/lib/subtasks/stored.ts (publishedSubtaskRuns), the reads of
// src/lib/payroll/shiftCost.ts and idealCost.ts, and refdata's getTradeCurveWeek,
// on D's in-memory SQLite (the whole pipeline runs in Harness/, on SQL Server).

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Entities = Roster.Data.Entities;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Held;
using Roster.Data.Services.Pay;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Data.Services.SubTasks;
using Roster.Data.Snapshots;
using Roster.Data.Tests.Support;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Fortnight;
using EngineBlock = Roster.Engine.Planning.Stage1.PlannedBlock;

namespace Roster.Data.Tests.Services.Rostering;

public sealed class PublishAndCostTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Period = new(2026, 11, 23);
    private readonly TestDatabase _tdb = new();
    private readonly TestContexts _contexts;
    private readonly FixedClock _clock = new(Now);
    private readonly Store _store;
    private readonly RefDataReader _ref;

    public PublishAndCostTests()
    {
        _contexts = new TestContexts(_tdb);
        _store = Seed.Stores(_tdb)[0];
        _ref = new RefDataReader(_contexts);
    }

    public void Dispose() => _tdb.Dispose();

    private static Shift Shift(Employee e, double start, double end, bool unfilled = false, params (double Time, string Group, string? Sub)[] tiles) => new()
    {
        EmployeeId = unfilled ? "" : e.Id.ToString("D"),
        Name = unfilled ? "Open shift" : e.Name,
        Role = e.Role,
        Start = start,
        End = end,
        BreakMinutes = 30,
        Unfilled = unfilled,
        Blocks = tiles.Select(t => new ShiftBlock(t.Time, t.Group, t.Sub)).ToList(),
    };

    private async Task HoldRosterAsync(params (DateOnly Date, RosterComputation Day)[] days)
    {
        var byDate = new JsMap<DateOnly, RosterComputation>();
        foreach (var (d, day) in days) byDate.Set(d, day);
        var payload = JsonSerializer.Serialize(new { byDate, fortnightFlags = new List<string>() }, RosterJson.Options);
        await new PlanSnapshotStore(_contexts).StoreAsync(_store.Id, Period, SnapshotKinds.Roster, payload, Now, null);
    }

    private RosterApprovalService Approval()
    {
        var store = new PlanSnapshotStore(_contexts);
        var held = new HeldPlans(store, null!, null!, null!, new PlanPeriods(_contexts, _clock), _clock);
        var labels = new SubtaskLabels(_contexts, _ref);
        var rosters = new HeldRosters(store, held, null!, labels, _clock);
        return new RosterApprovalService(_contexts, new DatabaseFunctionsService(_contexts, _contexts.Scope(), _clock), rosters, _clock);
    }

    private AppUser User()
    {
        using var db = _tdb.System();
        var u = new AppUser { Id = Guid.NewGuid(), Username = "store.user", Tier = Tier.Store, StoreId = _store.Id };
        db.AppUsers.Add(u);
        db.SaveChanges();
        return u;
    }

    [Fact]
    public async Task Publish_persists_the_held_roster_signs_off_closed_days_and_asks_for_a_note_on_an_open_shift()
    {
        var ann = Seed.Employee(_tdb, _store.Id, "1-Able, Ann");
        var ben = Seed.Employee(_tdb, _store.Id, "2-Baker, Ben");
        var dates = Roster.Engine.Core.Period.PeriodDatesFrom(Period);
        var worked = new RosterComputation
        {
            HasData = true,
            Shifts =
            [
                Shift(ann, 420, 900, false, (420, "load_running", "ambient"), (435, "registers", null), (450, "flex", null), (465, "break", null)),
                Shift(ben, 540, 1020, true),
            ],
        };
        await HoldRosterAsync((dates[0], worked), (dates[1], new RosterComputation { Closed = true }), (dates[2], new RosterComputation()));
        var user = User();
        var approval = Approval();

        var refused = await approval.PublishRosterAsync(_store.Id, dates, user.Id);
        Assert.False(refused.Ok);
        Assert.EndsWith("Add a note on how you tried to cover them before publishing.", refused.Error);

        Assert.Equal(ApprovalResult.Success, await approval.PublishRosterAsync(_store.Id, dates, user.Id, "  Rang the casuals  "));
        using var db = _tdb.System();
        var shift = await db.GeneratedShifts.Include(s => s.Blocks).SingleAsync();
        Assert.Equal((ann.Id, new TimeOnly(7, 0), new TimeOnly(15, 0), 30), (shift.EmployeeId, shift.StartTime, shift.EndTime, shift.BreakMinutes));
        // The flex tile goes; breaks stay; registers is the one checkout group; a label stays on its own group only.
        Assert.Equal([("load_running", "ambient"), ("checkout", null), ("break", null)],
            shift.Blocks.OrderBy(b => b.BlockTime).Select(b => (b.LabourGroup, b.SubTask)));
        var days = await db.DayPlans.OrderBy(d => d.Date).ToListAsync();
        Assert.Equal([(dates[0], "approved"), (dates[1], "approved")], days.Select(d => (d.Date, d.RosterStatus)));
        var audit = await db.RosterAudit.SingleAsync();
        Assert.Equal("approve_roster", audit.Action);
        Assert.Contains("\"approvedDays\":1", audit.Detail);
        Assert.Contains("\"coverageComment\":\"Rang the casuals\"", audit.Detail);

        // The printed roster's sub-task codes for the published shift.
        var runs = await new PublishedSubtasks(_contexts, new SubtaskLabels(_contexts, _ref),
            new HeldPlans(new PlanSnapshotStore(_contexts), null!, null!, null!, new PlanPeriods(_contexts, _clock), _clock))
            .PublishedSubtaskRunsAsync(_store.Id, Period, dates);
        Assert.True(runs.Has($"{ann.Id:D}:{dates[0]:yyyy-MM-dd}"));
    }

    [Fact]
    public async Task Publish_needs_a_held_roster_and_a_day_to_approve()
    {
        var user = User();
        var dates = Roster.Engine.Core.Period.PeriodDatesFrom(Period);
        Assert.Equal(new ApprovalResult(false, "Generate the roster first."), await Approval().PublishRosterAsync(_store.Id, dates, user.Id));
        await HoldRosterAsync((dates[0], new RosterComputation()));
        Assert.Equal(new ApprovalResult(false, "No roster could be generated for any day in this fortnight"),
            await Approval().PublishRosterAsync(_store.Id, dates, user.Id));
    }

    [Fact]
    public async Task Shift_costs_and_the_ideal_day_price_from_the_rate_grid()
    {
        var date = Period; // a Monday
        using (var db = _tdb.System())
        {
            db.Positions.Add(new Position { Code = "SA", Label = "Store Assistant", BaseRole = "SA" });
            for (var h = 0; h < 24; h++) db.PayRates.Add(new PayRate { DayType = "weekday", Hour = h, Role = "SA", Rate = 40 });
            db.BreakRules.Add(new Entities.BreakRule { Role = "SA", MinShiftHours = 3, MaxShiftHours = 9, GapHours = 10, BreakType = "tiered" });
            await db.SaveChangesAsync();
        }
        var emp = Seed.Employee(_tdb, _store.Id, "1-Able, Ann");
        var pay = new PayDataReader(_contexts, _ref);
        var costs = new PayCostsLoader(_ref, pay, null!, null!);
        var shift = new CostableShift(emp.Id.ToString("D"), "SA", 540, 780, 0); // 09:00-13:00, no unpaid break
        var result = await costs.ComputeShiftCostsAsync(_store.Id, date, [shift], new JsMap<string, ShiftTiers>());
        var expected = ShiftCost.ComputeShiftCosts(date, [shift], await pay.GetPayRatesAsync(),
            [new CostEmployee(emp.Id.ToString("D"), null, null)], new JsMap<string, ShiftTiers>(), Roster.Engine.Pay.PaySettings.Default, "weekday");
        Assert.Equal(expected.DayTotal, result.DayTotal);
        Assert.True(result.DayTotal > 0);
        Assert.Equal(0, (await costs.ComputeShiftCostsAsync(_store.Id, date, [], null)).DayTotal);

        var s1 = new Stage1Computation { HasData = true, StaffOpen = 360, StaffClose = 1290, Blocks = [new EngineBlock { Group = "cleaning", Time = 540, Headcount = 2 }] };
        var ideal = await costs.ComputeIdealDayCostAsync(_store.Id, date, s1);
        Assert.Equal(IdealCost.ComputeIdealDayCost(true, [("cleaning", 540.0, 2.0)], 360, 1290, await pay.GetPayRatesAsync(), "weekday"), ideal);
        Assert.Equal(0, await costs.ComputeIdealDayCostAsync(_store.Id, date, new Stage1Computation()));
    }

    [Fact]
    public async Task The_trade_curve_is_derived_from_the_POS_statistics_with_the_legacy_curve_behind_it()
    {
        using (var db = _tdb.System())
        {
            db.QuarterHourAllocations.Add(new QuarterHourAllocation { StoreId = _store.Id, DayOfWeek = 0, BlockTime = new TimeOnly(9, 0), Allocation = 1 });
            db.QuarterHourAllocations.Add(new QuarterHourAllocation { StoreId = _store.Id, DayOfWeek = 2, BlockTime = new TimeOnly(9, 0), Allocation = 1 });
            foreach (var week in new[] { 0, 7, 14, 21 })
                foreach (var (h, n) in new[] { (9, 10.0), (10, 30.0) })
                    db.PosIntervalStats.Add(new PosIntervalStat { StoreId = _store.Id, Date = new DateOnly(2026, 10, 5).AddDays(week), BlockTime = new TimeOnly(h, 0), McoCustomers = n, McoItems = n * 10 });
            await db.SaveChangesAsync();
        }
        var week0 = await _ref.GetTradeCurveWeekAsync(_store.Id);
        var expected = Roster.Data.Feeds.PosAllocation.AssembleCurveWeek(
            await _ref.GetPosIntervalRowsAsync(_store.Id), (await _ref.GetQuarterHourWeekAsync(_store.Id)).Values.SelectMany(v => v),
            await _ref.GetHolidaySetAsync(_store.Id));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(week0));
        // Monday (the POS sample's weekday) is derived from it; Wednesday keeps the legacy row.
        Assert.Equal(2, week0[0].Count);
        Assert.Equal(1, Assert.Single(week0[2]).Allocation);
        Assert.Equal(10, (await _ref.GetPosCustomerRowsAsync(_store.Id))[0].Customers);
    }
}
