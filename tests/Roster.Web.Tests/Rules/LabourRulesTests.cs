// New: the Labour Group Rules actions (src/app/(app)/admin/rules/labour/actions.ts), the
// summary labels its page.tsx builds, and what the service writes, on SQLite.

using Roster.Data.Entities;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Rules;
using Roster.Web.Features.Rules.Labour;

namespace Roster.Web.Tests.Rules;

public class LabourRulesTests
{
    private static AdminTaskInput Task(string schedule = "weekly", int? day = 0) => new()
    {
        Name = "Comms Overhaul", ScheduleType = schedule, DayOfWeek = day, DurationType = "fixed", FixedMinutes = 30,
    };

    [Fact]
    public void CleanTask_refuses_in_the_actions_order()
    {
        Assert.Equal("Enter a task name.", LabourRulesChecks.CleanTask(Task() with { Name = "  " }).Error);
        Assert.Equal("Pick a day of the week.", LabourRulesChecks.CleanTask(Task(day: null)).Error);
        Assert.Equal("Pick which week of the month.", LabourRulesChecks.CleanTask(Task("monthly_nth", 1)).Error);
        Assert.Equal("Enter the fixed minutes.", LabourRulesChecks.CleanTask(Task() with { FixedMinutes = 0 }).Error);
        Assert.Equal("Enter the minutes per unit.", LabourRulesChecks.CleanTask(Task() with { DurationType = "per_register" }).Error);
        Assert.Equal("Enter the window start time.", LabourRulesChecks.CleanTask(Task() with { WindowStartKind = "clock", WindowStartTime = "" }).Error);
        Assert.Equal("Enter the window end time.", LabourRulesChecks.CleanTask(Task() with { WindowEndKind = "clock" }).Error);
        // A weekly task with region days (even none picked) needs no flat day.
        Assert.Null(LabourRulesChecks.CleanTask(Task(day: null) with { RegionDays = new Dictionary<string, int>() }).Error);
    }

    [Fact]
    public void CleanTask_keeps_only_the_fields_its_types_use()
    {
        var (row, _) = LabourRulesChecks.CleanTask(new AdminTaskInput
        {
            Name = " Fresh Ordering ", ScheduleType = "daily", DayOfWeek = 3, NthWeek = 2, RegionDays = new Dictionary<string, int> { ["501"] = 0 },
            DurationType = "per_employee", FixedMinutes = 10, MinutesPerUnit = 1.32, MinMinutes = 20,
            WindowStartKind = "close", WindowStartTime = "09:00", WindowStartOffsetMin = -210.5,
            WindowEndKind = "clock", WindowEndTime = "21:00", WindowEndOffsetMin = 15,
        });
        Assert.NotNull(row);
        Assert.Equal(("Fresh Ordering", (int?)null, (int?)null), (row.Name, row.DayOfWeek, row.NthWeek));
        Assert.Null(row.RegionDays);
        Assert.Null(row.FixedMinutes);
        Assert.Equal(1.32, row.MinutesPerUnit);
        Assert.Equal(20, row.MinMinutes);
        Assert.Null(row.WindowStartTime);
        Assert.Equal(-210, row.WindowStartOffsetMin);
        Assert.Equal("21:00", row.WindowEndTime);
        Assert.Equal(0, row.WindowEndOffsetMin);
    }

    [Fact]
    public void ResetTaskRecord_refuses_in_the_actions_order_and_keeps_its_sizing()
    {
        var share = new ResetTaskInput { Name = "Re-run 1", Sizing = "share", SharePct = 20, FixedMinutes = 30 };
        Assert.Equal("Name is required", LabourRulesChecks.ResetTaskRecordFor(share with { Name = "" }).Error);
        Assert.Equal("Share must be between 1 and 100%", LabourRulesChecks.ResetTaskRecordFor(share with { SharePct = 0 }).Error);
        Assert.Equal("Fixed minutes must be positive", LabourRulesChecks.ResetTaskRecordFor(share with { Sizing = "fixed", FixedMinutes = 0 }).Error);
        Assert.Equal("Window start needs a time", LabourRulesChecks.ResetTaskRecordFor(share with { WindowStartKind = "clock", WindowStartTime = null }).Error);
        Assert.Equal("Unknown placement", LabourRulesChecks.ResetTaskRecordFor(share with { Placement = "middle" }).Error);
        Assert.Equal("A Reset task counts as Re-runs or Decarding", LabourRulesChecks.ResetTaskRecordFor(share with { SubTask = "tidy" }).Error);

        var row = LabourRulesChecks.ResetTaskRecordFor(share with { WindowStartOffsetMin = 7.5, SubTask = "re_runs" }).Row!;
        Assert.Equal((20.0, (double?)null, 8.0, "re_runs"), (row.SharePct!.Value, row.FixedMinutes, row.WindowStartOffsetMin, row.SubTask));
        var remainder = LabourRulesChecks.ResetTaskRecordFor(share with { Sizing = "remainder" }).Row!;
        Assert.Null(remainder.SharePct);
        Assert.Null(remainder.FixedMinutes);
        Assert.Null(remainder.SubTask);
    }

    [Fact]
    public void Window_quota_break_factor_and_load_start_checks()
    {
        Assert.Null(LabourRulesChecks.CheckQuota(null));
        Assert.Equal("Hours must be positive (or blank for no quota).", LabourRulesChecks.CheckQuota(0));
        Assert.Equal("Start must be before end.", LabourRulesChecks.CheckWindowUpdate("10:00", "09:00", null));
        Assert.Equal("Start must be before end.", LabourRulesChecks.CheckWindowUpdate("", "09:00", null));
        Assert.Null(LabourRulesChecks.CheckWindowUpdate("09:00", "10:00", 1));
        Assert.Equal("Break factor must be between 0 and 100%.", LabourRulesChecks.CheckBreakFactor(100.5));
        Assert.Null(LabourRulesChecks.CheckBreakFactor(0));

        var ok = new LoadStartInput(true, 12, 15, 90, 120, 60);
        Assert.Null(LabourRulesChecks.CheckLoadStart(ok));
        Assert.Equal("Every threshold must be a positive number.", LabourRulesChecks.CheckLoadStart(ok with { BusyCustomersMax = double.NaN }));
        Assert.Equal("The 07:00 hours threshold must sit below the 06:00 large-load threshold.", LabourRulesChecks.CheckLoadStart(ok with { SmallLoadMaxHours = 15 }));
        Assert.Equal("The 10:00 customer threshold must sit below the 09:30 threshold.", LabourRulesChecks.CheckLoadStart(ok with { QuietCustomersMax = 130 }));
        Assert.Equal("The customer sample window must be between 15 and 240 minutes.", LabourRulesChecks.CheckLoadStart(ok with { CustomerWindowMin = 10 }));
        Assert.Equal([("lrStartEnabled", 0.0), ("lrStartSmallLoadMaxHours", 12.0)], LabourRulesChecks.LoadStartRows(ok with { Enabled = false }).Take(2));
    }

    [Fact]
    public void Schedule_and_duration_labels_read_as_the_catalogue_writes_them()
    {
        // Till Calibration as seeded (0019): the day varies by region.
        var till = new AdminTaskInput
        {
            Name = "Till Calibration", ScheduleType = "weekly", DurationType = "till_calibration",
            RegionDays = new Dictionary<string, int> { ["501"] = 0, ["505"] = 0, ["506"] = 0, ["504"] = 1, ["507"] = 1, ["509"] = 1, ["503"] = 3, ["502"] = 3 },
        };
        Assert.Equal("Mon (501, 505, 506) · Tue (504, 507, 509) · Thu (502, 503)", LabourPage.ScheduleLabel(till));
        Assert.Equal("Registers + SCOs + 1 min", LabourPage.DurationLabel(till));
        Assert.Equal("", LabourPage.ScheduleLabel(till with { RegionDays = new Dictionary<string, int>() }));
        Assert.Equal("Every day", LabourPage.ScheduleLabel(Task("daily", null)));
        Assert.Equal("Every Fri", LabourPage.ScheduleLabel(Task("weekly", 4)));
        Assert.Equal("3rd Tue of the month", LabourPage.ScheduleLabel(Task("monthly_nth", 1) with { NthWeek = 3 }));
        Assert.Equal("Tue of the planning week", LabourPage.ScheduleLabel(Task("planning_week", 1)));
        Assert.Equal("58.5 min", LabourPage.DurationLabel(Task() with { FixedMinutes = 58.5, MinMinutes = 5 }));
        Assert.Equal("1.32 min × employees (min 20m)", LabourPage.DurationLabel(Task() with { DurationType = "per_employee", MinutesPerUnit = 1.32, MinMinutes = 20 }));
        Assert.Equal("5 min × registers", LabourPage.DurationLabel(Task() with { DurationType = "per_register", MinutesPerUnit = 5 }));
    }

    [Fact]
    public void Window_labels_come_from_the_engine()
    {
        var fresh = Task("daily", null) with { WindowStartKind = "close", WindowStartOffsetMin = -210, WindowEndKind = "close" };
        Assert.Equal("3h30 before close – close", LabourPage.ToTaskRow(Guid.Empty, fresh, 40).WindowLabel);
        Assert.Equal("Before 08:30", LabourPage.ToTaskRow(Guid.Empty, Task() with { WindowEndKind = "clock", WindowEndTime = "08:30" }, 50).WindowLabel);
        Assert.Equal("Anytime", LabourPage.ToTaskRow(Guid.Empty, Task(), 60).WindowLabel);
    }

    [Fact]
    public void Region_prefixes_and_the_priority_list()
    {
        Assert.Equal(["501", "502"], LabourPage.RegionPrefixes(["501 - Northside", "501 - Upper", "502 - Hills", "North", null, "5x1 - Odd"]));
        Assert.Equal(["admin", "checkout", "load_running", "reset", "specials", "cleaning"],
            LabourPage.PriorityGroups(["admin", "bogus", "checkout"]).Select(g => g.Key));
        Assert.Equal("Checkout", LabourPage.PriorityGroups([])[0].Label);
        Assert.Equal(["load_running", "reset", "admin", "specials", "cleaning", "non_op", "break"], LabourPage.WindowGroups.Select(g => g.Key));
    }

    [Fact]
    public async Task Load_reads_windows_tasks_resets_and_the_rostering_overrides()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db =>
        {
            db.LabourGroupRules.Add(new LabourGroupRule { LabourGroup = "cleaning", DayOfWeek = 2, WindowStart = new(18, 0), WindowEnd = new(21, 30) });
            db.LabourGroupRules.Add(new LabourGroupRule { LabourGroup = "cleaning", DayOfWeek = 0, WindowStart = new(9, 0), WindowEnd = new(10, 0), WindowHours = 1 });
            db.AdminTasks.Add(new AdminTask { Name = "Old", ScheduleType = "daily", DurationType = "fixed", FixedMinutes = 5, Active = false });
            db.AdminTasks.Add(new AdminTask { Name = "APM", ScheduleType = "weekly", DayOfWeek = 0, DurationType = "fixed", FixedMinutes = 15, WindowStartKind = "clock", WindowStartTime = new(9, 0), WindowEndKind = "clock", WindowEndTime = new(12, 0), SortOrder = 10 });
            db.ResetTasks.Add(new ResetTask { Name = "Full decard", WindowStartKind = "close", WindowStartOffsetMin = -150, WindowEndKind = "close", SortOrder = 40 });
            db.LabourGroupPriority.Add(new LabourGroupPriority { LabourGroup = "admin", Priority = 0 });
            db.RosteringRules.Add(new RosteringRule { Key = "paidBreakFactorPct", Value = 5 });
            db.RosteringRules.Add(new RosteringRule { Key = "lrStartEnabled", Value = 0 });
        });

        var data = await new LabourRulesService(t, t.Scope, t.Clock).LoadAsync();

        Assert.Equal([0, 2], data.Rules.Select(r => r.DayOfWeek));
        Assert.Equal(("09:00", "10:00", (double?)1), (data.Rules[0].WindowStart, data.Rules[0].WindowEnd, data.Rules[0].WindowHours));
        var apm = Assert.Single(data.Tasks);
        Assert.Equal(("APM", "Every Mon", "15 min", "09:00 – 12:00"), (apm.Task.Name, apm.ScheduleLabel, apm.DurationLabel, apm.WindowLabel));
        Assert.Equal("09:00", apm.Task.WindowStartTime);
        Assert.Equal(("Full decard", -150.0, (double?)null), (data.ResetTasks[0].Name, data.ResetTasks[0].WindowStartOffsetMin, data.ResetTasks[0].SharePct));
        Assert.Equal("admin", data.PriorityGroups[0].Key);
        Assert.Equal(5, data.BreakFactorPct);
        Assert.False(data.LoadStartRules.Enabled);
        Assert.Equal(LoadStart.DefaultRules.SmallLoadMaxHours, data.LoadStartRules.SmallLoadMaxHours);
    }

    [Fact]
    public async Task Windows_are_added_changed_and_cleared()
    {
        using var t = new RulesTestDatabase();
        var service = new LabourRulesService(t, t.Scope, t.Clock);

        Assert.Equal(SaveResult.Success, await service.AddWindowAsync("specials", 1, "10:00", "21:00"));
        // addWindow does not check the order of the times; updateWindow does.
        Assert.True((await service.AddWindowAsync("specials", 4, "21:00", "10:00", 2)).Ok);
        Assert.Equal(SaveResult.Fail("Hours must be positive (or blank for no quota)."), await service.AddWindowAsync("specials", 2, "10:00", "11:00", -1));
        var id = t.Read(db => db.LabourGroupRules.Single(r => r.DayOfWeek == 1).Id);
        Assert.Equal(SaveResult.Fail("Start must be before end."), await service.UpdateWindowAsync(id, 1, "12:00", "11:00"));
        Assert.True((await service.UpdateWindowAsync(id, 3, "09:30", "11:00", 1.5)).Ok);
        var row = t.Read(db => db.LabourGroupRules.Single(r => r.Id == id));
        Assert.Equal((3, new TimeOnly(9, 30), (double?)1.5), (row.DayOfWeek, row.WindowStart, row.WindowHours));

        Assert.True((await service.ClearWindowsAsync("specials")).Ok);
        Assert.Equal(0, t.Read(db => db.LabourGroupRules.Count()));
    }

    [Fact]
    public async Task A_new_admin_task_goes_ten_after_the_last()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db => db.AdminTasks.Add(new AdminTask { Name = "Hidden", ScheduleType = "daily", DurationType = "fixed", FixedMinutes = 5, SortOrder = 240, Active = false }));
        var service = new LabourRulesService(t, t.Scope, t.Clock);

        var till = new AdminTaskInput { Name = "Till Calibration", ScheduleType = "weekly", DurationType = "till_calibration", RegionDays = new Dictionary<string, int> { ["504"] = 1, ["501"] = 0 } };
        Assert.Equal(SaveResult.Success, await service.AddAdminTaskAsync(till));
        var saved = t.Read(db => db.AdminTasks.Single(a => a.Name == "Till Calibration"));
        Assert.Equal(250, saved.SortOrder);
        Assert.Equal("{\"501\":0,\"504\":1}", saved.RegionDays);
        Assert.Null(saved.DayOfWeek);

        Assert.True((await service.UpdateAdminTaskAsync(saved.Id, till with { ScheduleType = "daily", WindowEndKind = "clock", WindowEndTime = "13:00" })).Ok);
        saved = t.Read(db => db.AdminTasks.Single(a => a.Id == saved.Id));
        Assert.Equal(("daily", (string?)null, new TimeOnly(13, 0), 250), (saved.ScheduleType, saved.RegionDays, saved.WindowEndTime!.Value, saved.SortOrder));
        Assert.Equal(SaveResult.Fail("Enter a task name."), await service.UpdateAdminTaskAsync(saved.Id, till with { Name = "" }));
        Assert.True((await service.DeleteAdminTaskAsync(saved.Id)).Ok);
        Assert.Equal(1, t.Read(db => db.AdminTasks.Count()));
    }

    [Fact]
    public async Task Priority_break_factor_reset_tasks_and_the_load_start_rule_save()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db => db.LabourGroupPriority.Add(new LabourGroupPriority { LabourGroup = "cleaning", Priority = 0 }));
        var service = new LabourRulesService(t, t.Scope, t.Clock);

        Assert.True((await service.SavePriorityOrderAsync(["admin", "cleaning"])).Ok);
        Assert.Equal([("admin", 0), ("cleaning", 1)], t.Read(db => db.LabourGroupPriority.OrderBy(p => p.Priority).Select(p => new { p.LabourGroup, p.Priority }).ToList()).Select(p => (p.LabourGroup, p.Priority)));

        Assert.Equal(SaveResult.Fail("Break factor must be between 0 and 100%."), await service.SaveBreakFactorAsync(-1));
        Assert.True((await service.SaveBreakFactorAsync(5.5)).Ok);
        var factor = t.Read(db => db.RosteringRules.Single(r => r.Key == "paidBreakFactorPct"));
        Assert.Equal((5.5, LabourRulesChecks.BreakFactorNote, RulesTestDatabase.Now), (factor.Value, factor.Note, factor.UpdatedAt));

        var reRun = new ResetTaskInput { Name = "Re-run 1", Sizing = "share", SharePct = 15, WindowStartKind = "clock", WindowStartTime = "10:00", WindowEndKind = "clock", WindowEndTime = "14:00" };
        Assert.True((await service.SaveResetTaskAsync(null, reRun)).Ok);
        var id = t.Read(db => db.ResetTasks.Single().Id);
        Assert.True((await service.SaveResetTaskAsync(id, reRun with { Sizing = "fixed", FixedMinutes = 45, Placement = "late", SubTask = "decarding" })).Ok);
        var task = t.Read(db => db.ResetTasks.Single());
        Assert.Equal(((double?)null, (double?)45, "late", "decarding", new TimeOnly(14, 0)), (task.SharePct, task.FixedMinutes, task.Placement, task.SubTask, task.WindowEndTime!.Value));
        Assert.True((await service.DeleteResetTaskAsync(id)).Ok);

        Assert.Equal(SaveResult.Fail("The customer sample window must be between 15 and 240 minutes."), await service.SaveLoadStartRulesAsync(new(true, 12, 15, 90, 120, 300)));
        Assert.True((await service.SaveLoadStartRulesAsync(new(false, 11, 14, 80, 110, 45))).Ok);
        var rules = t.Read(db => db.RosteringRules.Where(r => r.Key.StartsWith("lrStart")).ToDictionary(r => r.Key, r => r.Value));
        Assert.Equal(6, rules.Count);
        Assert.Equal((0.0, 11.0, 45.0), (rules["lrStartEnabled"], rules["lrStartSmallLoadMaxHours"], rules["lrStartCustomerWindowMin"]));
    }
}
