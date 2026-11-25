// Port of src/lib/adminTasks/resolve.test.ts.

using Roster.Engine.Planning;

namespace Roster.Engine.Tests.Planning;

public class AdminTasksTests
{
    // Minimal rule factory - tests override only what they exercise.
    private static AdminTaskRule Rule(Func<AdminTaskRule, AdminTaskRule>? over = null)
    {
        var r = new AdminTaskRule
        {
            Id = "t",
            Name = "Task",
            ScheduleType = "daily",
            DayOfWeek = null,
            NthWeek = null,
            RegionDays = null,
            DurationType = "fixed",
            FixedMinutes = 30,
            MinutesPerUnit = null,
            MinMinutes = null,
            WindowStartKind = "none",
            WindowStartTime = null,
            WindowStartOffsetMin = 0,
            WindowEndKind = "none",
            WindowEndTime = null,
            WindowEndOffsetMin = 0,
            SortOrder = 0,
        };
        return over is null ? r : over(r);
    }

    // A typical store day: trades 08:30-20:00, staffed 06:00-20:30.
    private static AdminTaskContext Ctx(Func<AdminTaskContext, AdminTaskContext>? over = null)
    {
        var c = new AdminTaskContext
        {
            Date = new DateOnly(2026, 7, 20), // Monday, and a period start (the national anchor)
            RegionPrefix = "501",
            EmployeeCount = 20,
            RegisterCount = 4,
            ScoCount = 6,
            TradingOpen = 8 * 60 + 30,
            TradingClose = 20 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 20 * 60 + 30,
        };
        return over is null ? c : over(c);
    }

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    // ── isTaskDue - schedules ──

    [Fact]
    public void Daily_is_due_every_day()
    {
        var r = Rule(x => x with { ScheduleType = "daily" });
        Assert.True(AdminTasks.IsTaskDue(r, D("2026-07-20"), null));
        Assert.True(AdminTasks.IsTaskDue(r, D("2026-07-26"), null));
    }

    [Fact]
    public void Weekly_matches_its_day_of_week_only()
    {
        var mon = Rule(x => x with { ScheduleType = "weekly", DayOfWeek = 0 });
        Assert.True(AdminTasks.IsTaskDue(mon, D("2026-07-20"), null)); // Monday
        Assert.False(AdminTasks.IsTaskDue(mon, D("2026-07-21"), null)); // Tuesday
    }

    [Fact]
    public void Weekly_region_map_overrides_the_flat_day()
    {
        var r = Rule(x => x with
        {
            ScheduleType = "weekly",
            DayOfWeek = null,
            RegionDays = new() { ["501"] = 0, ["504"] = 1, ["503"] = 3 },
        });
        Assert.True(AdminTasks.IsTaskDue(r, D("2026-07-20"), "501")); // Mon for 501
        Assert.False(AdminTasks.IsTaskDue(r, D("2026-07-20"), "504")); // 504 does Tue
        Assert.True(AdminTasks.IsTaskDue(r, D("2026-07-21"), "504"));
        Assert.True(AdminTasks.IsTaskDue(r, D("2026-07-23"), "503")); // Thu for 503
        // Region not in the map and no flat day → never due.
        Assert.False(AdminTasks.IsTaskDue(r, D("2026-07-20"), "599"));
        Assert.False(AdminTasks.IsTaskDue(r, D("2026-07-20"), null));
    }

    [Fact]
    public void Monthly_nth_matches_the_nth_weekday_of_the_month()
    {
        var thirdTue = Rule(x => x with { ScheduleType = "monthly_nth", DayOfWeek = 1, NthWeek = 3 });
        Assert.True(AdminTasks.IsTaskDue(thirdTue, D("2026-07-21"), null)); // 3rd Tue of Jul 2026
        Assert.False(AdminTasks.IsTaskDue(thirdTue, D("2026-07-14"), null)); // 2nd Tue
        Assert.False(AdminTasks.IsTaskDue(thirdTue, D("2026-07-28"), null)); // 4th Tue
        Assert.True(AdminTasks.IsTaskDue(thirdTue, D("2026-08-18"), null)); // 3rd Tue of Aug
    }

    [Fact]
    public void Planning_week_matches_only_week_1_of_the_fortnight()
    {
        // Anchor period starts Mon 2026-07-20, so its planning week is 20-26 Jul.
        var monPw = Rule(x => x with { ScheduleType = "planning_week", DayOfWeek = 0 });
        Assert.True(AdminTasks.IsTaskDue(monPw, D("2026-07-20"), null)); // Mon, week 1
        Assert.False(AdminTasks.IsTaskDue(monPw, D("2026-07-27"), null)); // Mon, week 2
        Assert.True(AdminTasks.IsTaskDue(monPw, D("2026-08-03"), null)); // Mon, next period's week 1
        var tuePw = Rule(x => x with { ScheduleType = "planning_week", DayOfWeek = 1 });
        Assert.True(AdminTasks.IsTaskDue(tuePw, D("2026-07-21"), null));
        Assert.False(AdminTasks.IsTaskDue(tuePw, D("2026-07-28"), null));
    }

    // ── taskMinutes - durations ──

    [Fact]
    public void Fixed_returns_the_fixed_minutes() =>
        Assert.Equal(58.5, AdminTasks.TaskMinutes(Rule(x => x with { DurationType = "fixed", FixedMinutes = 58.5 }), Ctx()));

    [Fact]
    public void Per_employee_scales_by_EmployeeCount_with_the_minimum_as_a_floor()
    {
        var r = Rule(x => x with { DurationType = "per_employee", MinutesPerUnit = 1.32, MinMinutes = 20 });
        Assert.Equal(33, AdminTasks.TaskMinutes(r, Ctx(c => c with { EmployeeCount = 25 }))); // 1.32×25
        Assert.Equal(20, AdminTasks.TaskMinutes(r, Ctx(c => c with { EmployeeCount = 5 }))); // floor wins
    }

    [Fact]
    public void Per_register_scales_by_RegisterCount() =>
        Assert.Equal(12, AdminTasks.TaskMinutes(Rule(x => x with { DurationType = "per_register", MinutesPerUnit = 3 }), Ctx(c => c with { RegisterCount = 4 })));

    [Fact]
    public void Till_calibration_is_Registers_plus_SCOs_plus_1()
    {
        var r = Rule(x => x with { DurationType = "till_calibration" });
        Assert.Equal(11, AdminTasks.TaskMinutes(r, Ctx(c => c with { EmployeeCount = 20, RegisterCount = 4, ScoCount = 6 })));
        Assert.Equal(1, AdminTasks.TaskMinutes(r, Ctx(c => c with { EmployeeCount = 0, RegisterCount = 0, ScoCount = 0 })));
    }

    // ── window resolution ──

    [Fact]
    public void Clock_windows_resolve_to_their_times()
    {
        var r = Rule(x => x with { WindowStartKind = "clock", WindowStartTime = 9 * 60, WindowEndKind = "clock", WindowEndTime = 12 * 60 });
        var t = AdminTasks.ResolveAdminTasks([r], Ctx())[0];
        Assert.Equal(9 * 60, t.WindowStart);
        Assert.Equal(12 * 60, t.WindowEnd);
        Assert.Equal("09:00 – 12:00", t.WindowLabel);
    }

    [Fact]
    public void Open_close_anchors_apply_their_offsets()
    {
        var r = Rule(x => x with { WindowStartKind = "close", WindowStartOffsetMin = -135, WindowEndKind = "close", WindowEndOffsetMin = 15 });
        var t = AdminTasks.ResolveAdminTasks([r], Ctx())[0];
        Assert.Equal(20 * 60 - 135, t.WindowStart);
        Assert.Equal(20 * 60 + 15, t.WindowEnd);
        Assert.Equal("2h15 before close – 15m after close", t.WindowLabel);
    }

    [Fact]
    public void Open_ended_sides_fall_back_to_the_staffed_day()
    {
        var before830 = Rule(x => x with { WindowEndKind = "clock", WindowEndTime = 8 * 60 + 30 });
        var t = AdminTasks.ResolveAdminTasks([before830], Ctx())[0];
        Assert.Equal(6 * 60, t.WindowStart); // staffOpen
        Assert.Equal(8 * 60 + 30, t.WindowEnd);
        Assert.Equal("Before 08:30", t.WindowLabel);
    }

    [Fact]
    public void Before_open_and_after_close_label_and_resolve_correctly()
    {
        var beforeOpen = Rule(x => x with { WindowEndKind = "open" });
        var afterClose = Rule(x => x with { Name = "B", WindowStartKind = "close" });
        var res = AdminTasks.ResolveAdminTasks([beforeOpen, afterClose], Ctx());
        var (a, b) = (res[0], res[1]);
        Assert.Equal(8 * 60 + 30, a.WindowEnd);
        Assert.Equal("Before open", a.WindowLabel);
        Assert.Equal(20 * 60, b.WindowStart);
        Assert.Equal(20 * 60 + 30, b.WindowEnd); // staffClose
        Assert.Equal("After close", b.WindowLabel);
    }

    [Fact]
    public void An_inverted_window_widens_to_the_staffed_day_instead_of_vanishing()
    {
        // A bad config: open 17:00 (which follows the 16:00 close to 14:00) but
        // close at 13:00 - still inverted, so it widens rather than vanishes.
        var r = Rule(x => x with { WindowStartKind = "clock", WindowStartTime = 17 * 60, WindowEndKind = "clock", WindowEndTime = 13 * 60 });
        var shortDay = Ctx(c => c with { TradingClose = 16 * 60, StaffClose = 16 * 60 + 30 });
        var t = AdminTasks.ResolveAdminTasks([r], shortDay)[0];
        Assert.Equal(shortDay.StaffOpen, t.WindowStart);
        Assert.Equal(shortDay.StaffClose, t.WindowEnd);
    }

    [Fact]
    public void A_clock_start_follows_the_close_no_later_than_2_hours_before_it()
    {
        // The "after 17:00" date checks.
        var after17 = Rule(x => x with { WindowStartKind = "clock", WindowStartTime = 17 * 60 });
        ResolvedAdminTask At(double close) => AdminTasks.ResolveAdminTasks([after17], Ctx(c => c with { TradingClose = close, StaffClose = close + 30 }))[0];
        Assert.Equal(15 * 60, At(17 * 60).WindowStart);
        Assert.Equal(16 * 60, At(18 * 60).WindowStart);
        Assert.Equal(17 * 60, At(19 * 60).WindowStart);
        Assert.Equal(17 * 60, At(20 * 60).WindowStart);
        // An earlier clock start is never moved, and the end side never follows.
        var cgos = Rule(x => x with { WindowStartKind = "clock", WindowStartTime = 13 * 60, WindowEndKind = "clock", WindowEndTime = 16 * 60 });
        var c = AdminTasks.ResolveAdminTasks([cgos], Ctx(x => x with { TradingClose = 17 * 60, StaffClose = 17 * 60 + 30 }))[0];
        Assert.Equal((13 * 60.0, 16 * 60.0), (c.WindowStart, c.WindowEnd));
    }

    [Fact]
    public void Anytime_label_for_fully_open_windows() => Assert.Equal("Anytime", AdminTasks.WindowLabelFor(Rule()));

    // ── resolveAdminTasks - day assembly ──

    [Fact]
    public void Only_due_tasks_appear_sorted_by_window_start()
    {
        List<AdminTaskRule> rules =
        [
            Rule(x => x with { Id = "1", Name = "Mon only", ScheduleType = "weekly", DayOfWeek = 0 }),
            Rule(x => x with { Id = "2", Name = "Tue only", ScheduleType = "weekly", DayOfWeek = 1 }),
            Rule(x => x with { Id = "3", Name = "Evening", WindowStartKind = "clock", WindowStartTime = 17 * 60 }),
            Rule(x => x with { Id = "4", Name = "Morning", WindowEndKind = "clock", WindowEndTime = 8 * 60 + 30 }),
        ];
        var res = AdminTasks.ResolveAdminTasks(rules, Ctx()); // Monday
        // Same start (staffOpen fallback) sorts by earlier end: Morning first.
        Assert.Equal(["Morning", "Mon only", "Evening"], res.Select(t => t.Name));
    }

    [Fact]
    public void Zero_minute_tasks_are_dropped() =>
        Assert.Empty(AdminTasks.ResolveAdminTasks([Rule(x => x with { DurationType = "per_register", MinutesPerUnit = 3 })], Ctx(c => c with { RegisterCount = 0 })));
}
