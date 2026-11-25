// Tests for the port of src/lib/refdata.ts (and the reads of planSlots,
// planPeriod and payroll/settings) on D's in-memory SQLite. The TypeScript reads
// had no tests of their own; these pin each mapping the TypeScript made.

using Roster.Data.Entities;
using Entities = Roster.Data.Entities;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Pay;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Data.Tests.Support;
using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;

namespace Roster.Data.Tests.Services.RefData;

public sealed class RefDataReaderTests : IDisposable
{
    private readonly TestDatabase _tdb = new();
    private readonly TestContexts _contexts;
    private readonly RefDataReader _ref;
    private readonly Store _store;

    public RefDataReaderTests()
    {
        _contexts = new TestContexts(_tdb);
        _ref = new RefDataReader(_contexts);
        _store = Seed.Stores(_tdb, 1, "507 - Example Region")[0];
    }

    public void Dispose() => _tdb.Dispose();

    private void With(Action<RosterDbContext> add)
    {
        using var db = _tdb.System();
        add(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task Store_basics()
    {
        With(db =>
        {
            db.StoreTradingHours.Add(new StoreTradingHours { StoreId = _store.Id, DayOfWeek = 0, OpenTime = new TimeOnly(8, 30), CloseTime = new TimeOnly(20, 0) });
            db.StoreTradingHours.Add(new StoreTradingHours { StoreId = _store.Id, DayOfWeek = 6, Closed = true });
            db.SpecialTradingHours.Add(new SpecialTradingHours { Id = Guid.NewGuid(), StoreId = _store.Id, StartDate = new DateOnly(2026, 12, 24), EndDate = new DateOnly(2026, 12, 24), OpenTime = new TimeOnly(7, 0), CloseTime = new TimeOnly(18, 0) });
            db.StoreAttributes.Add(new StoreAttributes { StoreId = _store.Id, HasSco = true, McoRegisters = 4, ScoLanes = 6, NightFill = true });
        });
        Assert.Equal("NSW", await _ref.GetStoreStateAsync(_store.Id));
        Assert.Equal("507", await _ref.GetRegionPrefixAsync(_store.Id));
        var week = await _ref.GetTradingWeekAsync(_store.Id);
        Assert.Equal(new TradingDayRow(0, "08:30:00", "20:00:00", false), week[0]);
        Assert.Equal(new TradingDayRow(6, null, null, true), week[1]);
        var special = Assert.Single(await _ref.GetSpecialHoursAsync(_store.Id));
        Assert.Equal("07:00:00", special.OpenTime);
        var attrs = await _ref.GetStoreAttrsAsync(_store.Id);
        Assert.True(attrs!.NightFill);
        Assert.Equal(4, attrs.McoRegisters);
        Assert.Equal(0.66, attrs.ScoShare);
        Assert.Null(await _ref.GetStoreAttrsAsync(Guid.NewGuid()));
        Assert.Null(await _ref.GetRegionPrefixAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Holidays_and_school_days()
    {
        With(db =>
        {
            db.PublicHolidaysNational.Add(new PublicHolidayNational { Id = Guid.NewGuid(), Date = new DateOnly(2026, 12, 25), Name = "Christmas Day" });
            db.PublicHolidaysState.Add(new PublicHolidayState { Id = Guid.NewGuid(), State = "NSW", Date = new DateOnly(2026, 10, 5), Name = "Labour Day" });
            db.PublicHolidaysState.Add(new PublicHolidayState { Id = Guid.NewGuid(), State = "QLD", Date = new DateOnly(2026, 10, 6), Name = "Other" });
            db.PublicHolidaysLocal.Add(new PublicHolidayLocal { Id = Guid.NewGuid(), StoreId = _store.Id, Date = new DateOnly(2026, 9, 3), Name = "Show Day" });
            db.SchoolTerms.Add(new Entities.SchoolTerm { Id = Guid.NewGuid(), State = "NSW", TermLabel = "T4", StartDate = new DateOnly(2026, 10, 13), EndDate = new DateOnly(2026, 12, 18) });
            db.SchoolTermExceptions.Add(new SchoolTermException { Id = Guid.NewGuid(), State = "NSW", Date = new DateOnly(2026, 11, 2) });
        });
        var set = await _ref.GetHolidaySetAsync(_store.Id);
        Assert.Equal([new DateOnly(2026, 12, 25), new DateOnly(2026, 10, 5), new DateOnly(2026, 9, 3)], set);
        Assert.True(await _ref.IsPublicHolidayAsync(_store.Id, new DateOnly(2026, 9, 3)));
        Assert.False(await _ref.IsPublicHolidayAsync(_store.Id, new DateOnly(2026, 10, 6)));
        Assert.True(await _ref.IsSchoolDayForAsync(_store.Id, new DateOnly(2026, 11, 3)));   // a Tuesday in term
        Assert.False(await _ref.IsSchoolDayForAsync(_store.Id, new DateOnly(2026, 11, 2)));  // pupil-free day
        Assert.False(await _ref.IsSchoolDayForAsync(_store.Id, new DateOnly(2026, 11, 7)));  // Saturday
        Assert.False(await _ref.IsSchoolDayForAsync(_store.Id, new DateOnly(2026, 12, 21))); // after term
        Assert.Same(SchoolCalendar.None, await _ref.GetSchoolCalendarAsync(null));
    }

    [Fact]
    public async Task Rules_and_catalogues()
    {
        With(db =>
        {
            db.BreakRules.Add(new Entities.BreakRule { Role = "SA", MinShiftHours = 3, MaxShiftHours = 9, GapHours = 10, BreakType = "tiered", Ot15AfterHours = 9, OtPeriodAfterHours = 76, DoubleTimeUnderGapHours = 10, ConsecDayCap = 6 });
            db.BreakRules.Add(new Entities.BreakRule { Role = "SM", MinShiftHours = 3, MaxShiftHours = 12, GapHours = 10, BreakType = "flat_30_unpaid" });
            db.RosteringRules.Add(new RosteringRule { Key = "closeDownMin", Value = 45 });
            db.LabourGroupRules.Add(new LabourGroupRule { Id = Guid.NewGuid(), LabourGroup = "cleaning", DayOfWeek = 0, WindowStart = new TimeOnly(18, 0), WindowEnd = new TimeOnly(21, 0) });
            db.LabourGroupRules.Add(new LabourGroupRule { Id = Guid.NewGuid(), LabourGroup = "cleaning", DayOfWeek = 0, WindowStart = new TimeOnly(9, 0), WindowEnd = new TimeOnly(10, 0), WindowHours = 1 });
            db.AdminTasks.Add(new AdminTask { Id = Guid.NewGuid(), Name = "Till Calibration", ScheduleType = "weekly", RegionDays = "{\"501\":0,\"507\":3}", DurationType = "till_calibration", MinutesPerUnit = 5, WindowStartKind = "clock", WindowStartTime = new TimeOnly(13, 0), WindowEndKind = "close", WindowEndOffsetMin = -60, SortOrder = 2 });
            db.AdminTasks.Add(new AdminTask { Id = Guid.NewGuid(), Name = "Banking", ScheduleType = "daily", DurationType = "fixed", FixedMinutes = 15, SortOrder = 1 });
            db.AdminTasks.Add(new AdminTask { Id = Guid.NewGuid(), Name = "Retired", SortOrder = 0, Active = false });
            db.ResetTasks.Add(new ResetTask { Id = Guid.NewGuid(), Name = "Full decard", SortOrder = 3, Placement = "late", SubTask = "decarding" });
            db.ResetTasks.Add(new ResetTask { Id = Guid.NewGuid(), Name = "Re-run 1", SharePct = 30, WindowEndKind = "open", WindowEndOffsetMin = 30, SortOrder = 1 });
            db.LabourGroupPriority.Add(new LabourGroupPriority { LabourGroup = "reset", Priority = 1 });
            db.LabourGroupPriority.Add(new LabourGroupPriority { LabourGroup = "made_up", Priority = 2 });
        });
        var rules = await _ref.GetBreakRulesAsync();
        Assert.Equal(9, rules["SA"].MaxShiftHours);
        Assert.Equal(6, rules["SA"].ConsecDayCap);
        Assert.Equal("flat_30_unpaid", rules["SM"].BreakType);
        var pay = await _ref.GetRolePayRulesAsync();
        Assert.Equal(new Roster.Engine.Pay.RolePayRule(9, 76, 10), pay["SA"]);
        Assert.Equal(new Roster.Engine.Pay.RolePayRule(null, null, null), pay["SM"]);
        Assert.Equal(45, (await _ref.GetRuleOverridesAsync())["closeDownMin"]);
        var windows = (await _ref.GetLabourWindowsAsync())[0];
        Assert.Equal([(540.0, 600.0, (double?)1), (1080.0, 1260.0, (double?)null)], windows.Select(w => (w.Start, w.End, w.Hours)));

        var admin = await _ref.GetAdminTasksAsync();
        Assert.Equal(["Banking", "Till Calibration"], admin.Select(a => a.Name));
        var till = admin[1];
        Assert.Equal(3, till.RegionDays!["507"]);
        Assert.Equal(780, till.WindowStartTime);
        Assert.Equal(-60, till.WindowEndOffsetMin);
        Assert.Null(till.WindowEndTime);

        var reset = await _ref.GetResetTasksAsync();
        Assert.Equal(["Re-run 1", "Full decard"], reset.Select(r => r.Name));
        Assert.Equal("spread", reset[0].Placement);
        Assert.Equal(30, reset[0].SharePct);
        Assert.Equal("decarding", reset[1].SubTask);

        // labour_group_priority: unknown groups dropped, the rest of the default appended.
        Assert.Equal(["reset", .. TaskGroups.TaskPriority.Where(g => g != "reset")], await _ref.GetTaskPriorityAsync());
        Assert.Null(await _ref.GetRosterOrderAsync());
    }

    [Fact]
    public async Task Placement_config_normalises_and_knows_the_saved_groups()
    {
        With(db =>
        {
            db.PlanPlacementRules.Add(new PlanPlacementRule { Id = Guid.NewGuid(), Position = 1, LabourGroup = "cleaning", Strategy = "backfill", AnchorKind = "close", AnchorOffsetMin = 0 });
            db.PlanPlacementRules.Add(new PlanPlacementRule { Id = Guid.NewGuid(), Position = 2, LabourGroup = "specials", Strategy = "spread" });
            db.PlanPlacementSettings.Add(new PlanPlacementSettings { Id = true, Enabled = true, StepOrder = ["cleaning", "bogus", "admin"] });
            db.RosterOrderSteps.Add(new RosterOrderStep { Id = Guid.NewGuid(), Position = 2, StepType = "FILL_FLOOR", DayOrder = [5, 6, 0] });
            db.RosterOrderSteps.Add(new RosterOrderStep { Id = Guid.NewGuid(), Position = 1, StepType = "ALLOCATE_SM" });
        });
        var cfg = await _ref.GetPlacementConfigAsync();
        Assert.True(cfg.Settings.Enabled);
        Assert.Equal(17 * 60, cfg.Settings.NightTraderFromMin);
        Assert.Equal(new HashSet<string> { "cleaning", "specials" }, cfg.SavedGroups.ToHashSet());
        Assert.Equal("backfill", cfg.Rules.Single(r => r.Group == "cleaning").Strategy);
        Assert.Equal(["cleaning", "admin", "load_running", "reset", "specials"], cfg.StepOrder);
        var order = await _ref.GetRosterOrderAsync();
        Assert.Equal([new OrderStep("ALLOCATE_SM"), new OrderStep("FILL_FLOOR", [5, 6, 0])], order!.Select(o => o with { DayOrder = o.DayOrder?.ToList() }),
            new OrderStepComparer());
    }

    private sealed class OrderStepComparer : IEqualityComparer<OrderStep>
    {
        public bool Equals(OrderStep? x, OrderStep? y) => x!.Type == y!.Type && (x.DayOrder ?? []).SequenceEqual(y.DayOrder ?? []);
        public int GetHashCode(OrderStep obj) => obj.Type.GetHashCode();
    }

    [Fact]
    public async Task Planning_inputs()
    {
        var day = new DateOnly(2026, 9, 3);
        With(db =>
        {
            db.BenchmarkDays.Add(new BenchmarkDay { StoreId = _store.Id, Date = day, SalesForecast = 41000, LrHours100 = 18, CheckoutHours100 = 27, EmployeeCount = 31 });
            db.SalesOverrides.Add(new SalesOverride { StoreId = _store.Id, Date = day, Method = "pct", Pct = 110 });
            db.DayPlans.Add(new DayPlan { StoreId = _store.Id, Date = day.AddDays(1), PhLoadReallocated = true });
            db.QuarterHourAllocations.Add(new QuarterHourAllocation { StoreId = _store.Id, DayOfWeek = 3, BlockTime = new TimeOnly(9, 15), Allocation = 0.02 });
            db.QuarterHourAllocations.Add(new QuarterHourAllocation { StoreId = _store.Id, DayOfWeek = 3, BlockTime = new TimeOnly(9, 0), Allocation = 0.01 });
            db.PosIntervalStats.Add(new PosIntervalStat { StoreId = _store.Id, Date = day, BlockTime = new TimeOnly(9, 0), McoCustomers = 12, ScoCustomers = 8 });
        });
        var bench = await _ref.GetBenchmarkDayAsync(_store.Id, day);
        Assert.Equal(41000, bench!.SalesForecast);
        Assert.Equal(20, bench.LrHours90, 10);
        Assert.Equal(31, bench.EmployeeCount);
        Assert.Null(await _ref.GetBenchmarkDayAsync(_store.Id, day.AddDays(1)));
        Assert.Equal(110, (await _ref.GetSalesOverrideMapAsync(_store.Id))[day].Pct);
        Assert.Equal([day.AddDays(1)], await _ref.GetPhReallocSetAsync(_store.Id));
        var qh = (await _ref.GetQuarterHourWeekAsync(_store.Id))[3];
        Assert.Equal(["09:00:00", "09:15:00"], qh.Select(r => r.BlockTime));
        var pos = Assert.Single(await _ref.GetPosCustomerRowsAsync(_store.Id));
        Assert.Equal((day, 540.0, 20.0), (pos.Date, pos.Time, pos.Customers));
    }

    [Fact]
    public async Task Team_positions_pay_and_the_memo()
    {
        var emp = Seed.Employee(_tdb, _store.Id, "1-Able, Ann", "SM");
        With(db =>
        {
            db.Positions.Add(new Position { Code = "SM-45-1", Label = "Store Manager", BaseRole = "SM", WeeklyHours = 45, YearBand = 1, SortOrder = 1 });
            db.Positions.Add(new Position { Code = "OLD", Label = "Retired", BaseRole = "SA", Active = false });
            db.PayRates.Add(new PayRate { DayType = "weekday", Hour = 9, Role = "SM-45-1", Rate = 41.5 });
            db.PayrollTaxRates.Add(new PayrollTaxRate { State = "NSW", Rate = 0.0545 });
        });
        var team = await _ref.GetEmployeesAsync(_store.Id);
        Assert.Equal(emp.Id, Assert.Single(team).Id);
        Assert.Equal(["SM-45-1"], (await _ref.GetPositionsAsync()).Select(p => p.Code));

        var pay = new PayDataReader(_contexts, _ref);
        Assert.Equal(Roster.Engine.Pay.PaySettings.Default, await pay.GetPaySettingsAsync());
        Assert.Equal(41.5, Assert.Single(await pay.GetPayRatesAsync()).Rate);
        var tax = await pay.GetPayrollTaxRatesAsync();
        Assert.Equal(0.0545, tax["NSW"]);
        Assert.Equal(0, tax["VIC"]);
        Assert.Equal(Roster.Engine.Core.AuStates.All, tax.Keys);
        Assert.Equal("saturday", await pay.DayTypeAsync(_store.Id, new DateOnly(2026, 11, 28)));

        // Inside a memo scope a read happens once: a row added after it is not seen.
        await RequestMemo.Run(async () =>
        {
            Assert.Single(await _ref.GetEmployeesAsync(_store.Id));
            Seed.Employee(_tdb, _store.Id, "2-Baker, Ben");
            Assert.Single(await _ref.GetEmployeesAsync(_store.Id));
        });
        Assert.Equal(2, (await _ref.GetEmployeesAsync(_store.Id)).Count);
    }

    [Fact]
    public async Task Fixed_shifts_are_the_cycle_week_s_less_anyone_away()
    {
        var saturday = new DateOnly(2026, 11, 28);
        var a = Seed.Employee(_tdb, _store.Id, "1-Able, Ann", "SM");
        var b = Seed.Employee(_tdb, _store.Id, "2-Baker, Ben", "JSA");
        var c = Seed.Employee(_tdb, _store.Id, "3-Cole, Cat", "SA");
        var d = Seed.Employee(_tdb, _store.Id, "4-Dunn, Dee", "SA");
        var monday = FixedCycle.MondayOf(saturday);
        With(db =>
        {
            foreach (var e in new[] { a, b, c, d })
                db.FixedRosters.Add(new FixedRoster { Id = Guid.NewGuid(), EmployeeId = e.Id, DayOfWeek = 5, StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(13, 0) });
            // Ann is on a two-week cycle anchored a week back: this Saturday is her week 1, with no row.
            db.FixedRosterCycles.Add(new FixedRosterCycle { EmployeeId = a.Id, CycleWeeks = 2, StartDate = monday.AddDays(-7) });
            db.WeekendMarkers.Add(new WeekendMarker { EmployeeId = c.Id, WeekendStart = saturday, SatOff = true });
            db.DayOffRequests.Add(new DayOffRequest { Id = Guid.NewGuid(), EmployeeId = d.Id, Date = saturday, Status = "pending" });
        });
        var loader = new DaySlotsLoader(_contexts, _ref, null!);
        var shifts = await loader.FixedShiftsForAsync(_store.Id, saturday, default);
        // Ben (a junior) works; Cat has the weekend marker; Dee's request is not granted.
        Assert.Equal(["JSA", "SA"], shifts.Select(s => s.Role));
        Assert.All(shifts, s => Assert.Equal((420.0, 780.0), (s.Start, s.End)));
        With(db => db.EmployeeLeave.Add(new EmployeeLeave { Id = Guid.NewGuid(), EmployeeId = b.Id, StartDate = saturday, EndDate = saturday.AddDays(3) }));
        Assert.Equal(["SA"], (await loader.FixedShiftsForAsync(_store.Id, saturday, default)).Select(s => s.Role));
    }

    [Fact]
    public async Task Plan_periods()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero)); // Wed 25 Nov in Sydney
        var periods = new PlanPeriods(_contexts, clock);
        var current = Period.CurrentPeriodStart(new DateOnly(2026, 11, 25));
        var planning = Period.PlanningPeriodStart(new DateOnly(2026, 11, 25));
        Assert.Equal(Period.PeriodDatesFrom(planning), periods.GetPlanPeriodDates());
        // Nothing benchmarked: the planning fortnight, nothing overdue.
        var none = await periods.ResolvePlanningPeriodAsync(_store.Id);
        Assert.Equal(planning, none.PeriodStart);
        Assert.Empty(none.OverduePeriods);
        // The current fortnight has a benchmarked day not yet approved: open on it, flag it.
        With(db => db.BenchmarkDays.Add(new BenchmarkDay { StoreId = _store.Id, Date = current.AddDays(2) }));
        var behind = await periods.ResolvePlanningPeriodAsync(_store.Id);
        Assert.Equal(current, behind.PeriodStart);
        Assert.Equal([current], behind.OverduePeriods);
        With(db => db.DayPlans.Add(new DayPlan { StoreId = _store.Id, Date = current.AddDays(2), RosterStatus = DayStatuses.Approved }));
        Assert.Equal(planning, (await periods.ResolvePlanningPeriodAsync(_store.Id)).PeriodStart);
    }
}
