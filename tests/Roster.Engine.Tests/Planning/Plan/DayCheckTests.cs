// Port of src/lib/plan/dayCheck.test.ts.

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Plan;

// The day checker reads the finished shifts (owner decisions 8, R2, R4).
// A small day: trading 08:00-17:00, staffed 07:00-17:30.
public class DayCheckTests
{
    private const double H = 60;
    private static double T(double h, double m = 0) => h * H + m;

    private static Stage1Computation S1(Action<Stage1Computation>? over = null)
    {
        var s = new Stage1Computation
        {
            Blocks = [],
            Allocations = [],
            TotalBenchmark = 0,
            TotalAllocated = 0,
            Variance = 0,
            WithinTolerance = true,
            Flags = [],
            HasData = true,
            Closed = false,
            TradingOpen = T(8),
            TradingClose = T(17),
            StaffOpen = T(7),
            StaffClose = T(17, 30),
            Windows =
            [
                new("admin", T(7), T(17, 30)),
                new("reset", T(10), T(17)),
                new("load_running", T(7), T(10)),
                new("cleaning", T(9), T(10), 1),
            ],
        };
        over?.Invoke(s);
        return s;
    }

    /// <summary>A shift painted with one group, breaks at the given times.</summary>
    private static CheckedShift Shift(string planRole, double start, double end, string group, double[]? breaks = null, Dictionary<double, string>? paint = null)
    {
        var blocks = new List<ITile>();
        for (var m = start; m < end; m += 15)
            blocks.Add(new CheckedBlock(m, paint is not null && paint.TryGetValue(m, out var p) ? p : (breaks ?? []).Contains(m) ? "break" : group));
        return new CheckedShift { PlanRole = planRole, Start = start, End = end, Blocks = blocks };
    }

    private static List<string> Errors(IEnumerable<string> flags) => flags.Where(f => ReviewChecks.SeverityOf(f) == Severity.Error).ToList();
    private static List<string> Warnings(IEnumerable<string> flags) => flags.Where(f => ReviewChecks.SeverityOf(f) == Severity.Warning).ToList();

    private static JsMap<string, double> File(double reset = 8)
    {
        var m = new JsMap<string, double>();
        foreach (var (k, v) in new[] { ("checkout", 6.0), ("load_running", 0), ("cleaning", 0), ("specials", 0), ("reset", reset), ("admin", 0) }) m.Set(k, v);
        return m;
    }

    [Fact]
    public void Finds_the_reviews_deliberate_edit_breaks_RC15_each_an_error()
    {
        // Saved through an edit, each of these passed unremarked before.
        var day = S1();
        var cases = new (string Name, CheckedShift[] Shifts, Regex Want)[]
        {
            ("a manager alone", [Shift("MGR", T(9), T(17), "reset", [T(12)])], new("someone works alone")),
            ("no manager all day", [Shift("SA", T(9), T(17), "reset"), Shift("SA", T(9), T(17), "reset")], new("^No manager rostered")),
            ("Admin on a store assistant", [Shift("MGR", T(9), T(17), "reset"), Shift("SA", T(9), T(17), "admin")], new("only managers may do Admin")),
            ("a junior on a register",
                [Shift("MGR", T(9), T(17), "reset"), Shift("SA", T(9), T(17), "reset"), Shift("SA", T(9), T(17), "reset"), Shift("SA", T(9), T(17), "reset"), Shift("JSA", T(16), T(17), "checkout")],
                new("juniors may not work a till")),
            ("a 14.5-hour shift", [Shift("MGR", T(7), T(17, 30), "reset"), Shift("SA", T(3), T(17, 30), "reset")], new(@"longer than the 9\.50-hour maximum")),
            ("work outside its window", [Shift("MGR", T(7), T(17), "reset"), Shift("SA", T(7), T(17), "reset")], new("Reset at 07:00–10:00 is outside its window")),
        };
        foreach (var (name, shifts, want) in cases)
        {
            var flags = DayChecker.CheckPlanDay(day, shifts, new DayCheckOptions { MaxClock = new Dictionary<string, double> { ["SA"] = 570, ["MGR"] = 720 } }).Flags;
            Assert.True(Errors(flags).Any(want.IsMatch), $"{name}: {string.Join(" | ", Errors(flags))}");
        }
    }

    [Fact]
    public void Names_the_till_nobody_is_on_and_why()
    {
        var day = S1(s => s.Blocks = [new("checkout", T(12), 2)]);
        var shifts = new[] { Shift("MGR", T(8), T(16), "reset", [T(12)]), Shift("SA", T(8), T(16), "checkout") };
        Assert.Contains("Tills: 0.25h of register time has nobody on it - the crew's breaks left nobody to take the till (12:00–12:15)", DayChecker.CheckPlanDay(day, shifts).Flags);
    }

    [Fact]
    public void Finds_work_on_nobodys_shift_with_its_real_cause_RC29()
    {
        // Two on Admin at 13:00 with one manager on: more than can do it.
        var day = S1(s => s.Blocks = [new("admin", T(13), 2)]);
        var shifts = new[] { Shift("MGR", T(8), T(16), "reset", [], new() { [T(13)] = "admin" }), Shift("SA", T(8), T(16), "reset") };
        var lost = DayChecker.CheckPlanDay(day, shifts).Flags.First(f => f.StartsWith("Admin:", StringComparison.Ordinal));
        Assert.Equal("Admin: 0.25h of today's plan is on nobody's shift - the plan asks for more people at once than can do it (13:00–13:15)", lost);
        Assert.Equal(Severity.Error, ReviewChecks.SeverityOf(lost));
    }

    [Fact]
    public void Holds_a_named_task_to_its_own_window_Admin_done_elsewhere_is_still_missing()
    {
        var day = S1(s =>
        {
            s.Blocks = [new("admin", T(16), 1)];
            s.AdminTasks = [new ResolvedAdminTask { Name = "Fresh Ordering", Minutes = 15, WindowStart = T(15, 30), WindowEnd = T(17), WindowLabel = "" }];
        });
        // The manager does Admin at 09:00 instead - outside the task's window.
        var shifts = new[] { Shift("MGR", T(8), T(16, 30), "reset", [], new() { [T(9)] = "admin" }), Shift("SA", T(8), T(16, 30), "reset") };
        Assert.Contains(DayChecker.CheckPlanDay(day, shifts).Flags, f => f.StartsWith("Admin: 0.25h of today's plan is on nobody's shift", StringComparison.Ordinal));
    }

    [Fact]
    public void Holds_each_planned_Admin_quarter_hour_to_its_own_jobs_window_not_a_longer_jobs()
    {
        // Safe Handover's window runs all day; that is no licence to do Fresh
        // Ordering's quarter-hour in the morning (G001 13/11/2026).
        var day = S1(s =>
        {
            s.Blocks = [new("admin", T(10), 1), new("admin", T(16), 1)];
            s.AdminTasks =
            [
                new ResolvedAdminTask { Name = "Safe Handover", Minutes = 15, WindowStart = T(9), WindowEnd = T(17, 30), WindowLabel = "" },
                new ResolvedAdminTask { Name = "Fresh Ordering", Minutes = 15, WindowStart = T(15, 30), WindowEnd = T(17), WindowLabel = "" },
            ];
            s.UnitWindows = [new("admin", T(10), T(9), T(17, 30), true), new("admin", T(16), T(15, 30), T(17), true)];
        });
        bool Missing(Dictionary<double, string> paint) =>
            DayChecker.CheckPlanDay(day, [Shift("MGR", T(8), T(16, 30), "reset", [], paint), Shift("SA", T(8), T(16, 30), "reset")]).Flags
                .Any(f => f.StartsWith("Admin: 0.25h of today's plan is on nobody's shift", StringComparison.Ordinal));
        Assert.False(Missing(new() { [T(10)] = "admin", [T(16)] = "admin" }));
        Assert.True(Missing(new() { [T(10)] = "admin", [T(11)] = "admin" }));
    }

    [Fact]
    public void Checks_Mondays_date_checks_against_their_deadline()
    {
        var day = S1(s => s.AdminTasks = [new ResolvedAdminTask { Name = "Longlife Date Checks", Minutes = 120, WindowStart = T(6), WindowEnd = T(8, 30), WindowLabel = "" }]);
        var flags = DayChecker.CheckPlanDay(day, [Shift("MGR", T(7), T(15), "admin"), Shift("SA", T(7), T(15), "reset")]).Flags;
        Assert.Contains("Monday date checks: 90 of 120 min done by 08:30", flags);
        Assert.Contains("Monday date checks: 90 of 120 min done by 08:30", Errors(flags));
    }

    [Fact]
    public void Errors_on_the_Cleaning_hour_and_the_handover_and_a_late_break_only_warns()
    {
        var day = S1(s =>
        {
            s.TradingClose = T(20);
            s.StaffClose = T(20, 30);
            s.Windows = [new("cleaning", T(9), T(10), 1)];
        });
        var shifts = new[]
        {
            Shift("MGR", T(7), T(13), "reset"),
            Shift("MGR", T(12, 30), T(20, 30), "reset", [T(18), T(18, 15)]),
            Shift("SA", T(7), T(20, 30), "reset", [T(10), T(10, 15), T(18), T(18, 15)]),
        };
        var flags = DayChecker.CheckPlanDay(day, shifts).Flags;
        foreach (var f in new[]
        {
            "Cleaning 09:00–10:00: 0.00h of its 1.00h is worked in its hour",
            "Manager handover is 30 minutes (12:30–13:00) - the rule is at least an hour",
        })
        {
            Assert.Contains(f, flags);
            Assert.Equal(Severity.Error, ReviewChecks.SeverityOf(f));
        }
        const string late = "Manager 12:30–20:30: break ends 18:30 - it could have been taken by 18:00";
        Assert.Contains(late, flags);
        Assert.Equal(Severity.Warning, ReviewChecks.SeverityOf(late));
    }

    [Fact]
    public void Says_why_shift_time_has_no_work_cover_the_3_hour_minimum_or_nothing()
    {
        var day = S1();
        // Two people 13:00-16:00; the assistant has nothing at all to do.
        var flags = DayChecker.CheckPlanDay(day, [Shift("MGR", T(13), T(16), "reset"), Shift("SA", T(13), T(16), "flex")]).Flags;
        Assert.Contains("3.00h of shift time is held for cover - nobody alone, a manager on, three adults with a junior (13:00–16:00)", flags);
        // Three people: the third's blank edge time is held by the 3-hour minimum.
        var three = new[]
        {
            Shift("MGR", T(9), T(17), "reset"),
            Shift("SA", T(9), T(17), "reset"),
            Shift("SA", T(13), T(16), "reset", [], new() { [T(15)] = "flex", [T(15, 15)] = "flex", [T(15, 30)] = "flex", [T(15, 45)] = "flex" }),
        };
        Assert.Contains("1.00h of shift time is held by the 3-hour minimum (15:00–16:00)", DayChecker.CheckPlanDay(day, three).Flags);
        // ...and interior blank time no rule holds is named on its shift.
        var four = new[]
        {
            Shift("MGR", T(9), T(17), "reset"),
            Shift("SA", T(9), T(17), "reset"),
            Shift("SA", T(9), T(17), "reset", [], new() { [T(12)] = "flex" }),
        };
        Assert.Contains("Store assistant 09:00–17:00: 1 quarter-hour with no work (12:00–12:15)", DayChecker.CheckPlanDay(day, four).Flags);
    }

    [Fact]
    public void Names_cover_time_where_the_day_has_less_work_than_the_people_who_must_be_on_as_an_hours_issue()
    {
        // Two must be on 13:00-16:00 (nobody alone); Stage 1 has work for one.
        var one = new List<PlannedBlock>();
        for (var m = T(13); m < T(16); m += 15) one.Add(new("reset", m, 1));
        var shifts = new[] { Shift("MGR", T(13), T(16), "reset"), Shift("SA", T(13), T(16), "flex") };
        var thin = DayChecker.CheckPlanDay(S1(s => s.Blocks = one), shifts).Flags;
        const string note = "Hours issue: 3.00h of shift time has no work because there is less work than the people who must be on (13:00–16:00) - a benchmark matter (owner decision 3), not a planning fault";
        Assert.Contains(note, thin);
        Assert.Equal(Severity.Warning, ReviewChecks.SeverityOf(note));
        // Work for two, painted for one: idle time the cut or the painter left,
        // not an hours issue.
        var two = one.Select(b => new PlannedBlock(b.Group, b.Time, 2)).ToList();
        Assert.DoesNotContain(DayChecker.CheckPlanDay(S1(s => s.Blocks = two), shifts).Flags, f => f.Contains("less work than the people"));
    }

    [Fact]
    public void Checks_worked_hours_against_the_benchmark_file_R2()
    {
        var shifts = new[] { Shift("MGR", T(9), T(17), "reset", [T(12), T(12, 15)]), Shift("SA", T(9), T(17), "checkout", [T(12), T(12, 15)]) };
        // A manager's 8h less 30 minutes, an assistant's 8h less 60: 14.5h worked
        // against 14h - inside the hour.
        Assert.Equal("ok", DayChecker.CheckPlanDay(S1(s => s.BenchmarkFile = File()), shifts).Hours.Status);
        // Against 12h: 2.5h over with nothing to explain it - an error.
        var over = DayChecker.CheckPlanDay(S1(s => s.BenchmarkFile = File(6)), shifts).Hours;
        Assert.Equal("error", over.Status);
        Assert.Equal(
            "Planned hours are 2.50 h over the benchmark (14.50 h planned, 12.00 h benchmark) - 2.50 h of it is not explained by an hours issue, cover or the 3-hour minimum",
            over.Message);
        // ...unless Admin past its benchmark explains it: a warning naming it.
        var issue = DayChecker.CheckPlanDay(S1(s => { s.BenchmarkFile = File(6); s.HoursIssues = [new("admin", 2.5)]; }), shifts).Hours;
        Assert.Equal("warning", issue.Status);
        Assert.Contains("Admin tasks past the Admin benchmark 2.50 h", issue.Message);
        // Reset tasks past the Reset the borrow left (decision 4.3) are named apart.
        var reset = DayChecker.CheckPlanDay(S1(s => { s.BenchmarkFile = File(6); s.HoursIssues = [new("admin", 2), new("reset", 0.5)]; }), shifts).Hours;
        Assert.Equal("warning", reset.Status);
        Assert.Contains("Admin tasks past the Admin benchmark 2.00 h, Reset tasks past the Reset benchmark 0.50 h", reset.Message);
        // Time the planner added so breaks don't cost work (decision 4.2) is explained too.
        var added = DayChecker.CheckPlanDay(S1(s => { s.BenchmarkFile = File(6); s.HoursIssues = [new("admin", 2)]; }), shifts, new DayCheckOptions { BreakTimeAdded = 0.5 }).Hours;
        Assert.Equal("warning", added.Status);
        Assert.Contains("time added so breaks don't cost work 0.50 h", added.Message);
        // Under by more than an hour is an error.
        // ...and it says where the hours went: what Stage 1 planned below the file.
        var under = DayChecker.CheckPlanDay(
            S1(s => { s.BenchmarkFile = File(10); s.Allocations = [new("checkout", 6, 6), new("reset", 8, 8)]; }), shifts).Hours;
        Assert.Equal("error", under.Status);
        Assert.Equal(
            "Planned hours are 1.50 h under the benchmark (14.50 h planned, 16.00 h benchmark) - planned below the benchmark file: reset 2.00 h",
            under.Message);
    }

    [Fact]
    public void Calls_a_day_OK_when_no_step_raised_an_error()
    {
        Assert.True(DayChecker.DayIsOk(["Hours issue: 1.50 h over the benchmark (x) - cover 1.50 h", "Very short day - both manager shifts cover the whole span"]));
        Assert.False(DayChecker.DayIsOk(["admin task 'Fresh Ordering': 15 min could not be planned inside its own window - more admin is due then than the managers on can work"]));
        Assert.Single(Warnings(["Manager 12:30–20:30: break ends 18:30 - it could have been taken by 18:00"]));
        // A manager under six hours is an error (owner decision R7).
        var flags = DayChecker.CheckPlanDay(S1(), [Shift("MGR", T(9), T(14), "reset"), Shift("MGR", T(13), T(17), "reset"), Shift("SA", T(9), T(17), "reset")]).Flags;
        Assert.Contains(Errors(flags), f => f.Contains("is under the 6-hour minimum for a manager"));
    }
}
