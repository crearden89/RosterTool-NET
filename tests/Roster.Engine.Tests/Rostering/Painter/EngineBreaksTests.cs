// Port of src/lib/stage2/engine.breaks.test.ts.
// Breaks go in AFTER the registers and the tasks (owner's sequence, U6; owner
// decision 4), each where the work it displaces can be handed on. Both cases
// are the review's (Roster Engine Review 07/11/2026, A02-8): placed first, on
// head counts, the breaks found neither answer.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Painter.PaintFixtures;

namespace Roster.Engine.Tests.Rostering.Painter;

public class EngineBreaksTests
{
    private static List<DemandBlock> Copy(List<DemandBlock> demand) => demand.Select(d => d.Clone()).ToList();

    [Fact]
    public void Keeps_every_till_manned_where_an_exact_answer_exists()
    {
        // Tills: two all day, three from 10:00 to 14:00 except five quarter-hours
        // - a pair at 11:00, one at 12:00, a pair at 13:00 - and one quarter of
        // cleaning at 12:00 that made that single spare look busy to a count.
        var spare = new HashSet<double> { 11 * H, 11 * H + 15, 12 * H, 13 * H, 13 * H + 15 };
        var demand = new List<DemandBlock>();
        for (var m = 9 * H; m < 15 * H; m += 15)
        {
            var tills = m >= 10 * H && m < 14 * H && !spare.Contains(m) ? 3 : 2;
            var extra = m == 12 * H ? 1 : 0;
            demand.Add(D(m, tills + extra, extra != 0 ? G(("checkout", tills), ("cleaning", extra)) : G(("checkout", tills))));
        }
        var shifts = new List<PaintableShift> { Shift("SA", 9 * H, 15 * H), Shift("SA", 9 * H, 15 * H), Shift("SA", 10 * H, 14 * H) };
        Paint(shifts, Copy(demand));
        foreach (var d in demand)
        {
            var on = shifts.Count(s => s.Blocks.Find(b => b.Time == d.Time)?.Group == "checkout");
            Assert.True(on == d.GroupCount("checkout"), $"{d.Time / H}");
        }
        LegalBreaks(shifts);
    }

    [Fact]
    public void Does_not_send_the_only_manager_on_a_break_during_the_date_checks_while_an_assistant_idles()
    {
        // Monday: the manager's two-and-a-half hours of date checks before 08:30,
        // load running beside them; then tills and cleaning. An assistant with
        // nothing to do at some minute makes it LOOK spare - but nobody else can
        // do the date checks.
        var demand = new List<DemandBlock>();
        for (var m = 6 * H; m < 12 * H; m += 15)
        {
            if (m < 8 * H + 30) demand.Add(D(m, 3, G(("admin", 1), ("load_running", 2))));
            else if (m < 11 * H) demand.Add(D(m, 4, G(("checkout", 2), ("cleaning", 2))));
            else demand.Add(D(m, 2, G(("checkout", 2))));
        }
        IReadOnlyList<string> Allowed(double t) =>
        [
            "cleaning",
            .. t < 8 * H + 30 ? new[] { "admin" } : [],
            .. t < 10 * H ? new[] { "load_running" } : [],
            .. t >= 8 * H + 30 ? new[] { "checkout" } : [],
        ];
        var shifts = new List<PaintableShift>
        {
            Shift("ASM", 6 * H, 12 * H), Shift("SA", 6 * H, 12 * H), Shift("SA", 6 * H, 12 * H), Shift("SA", 6 * H, 12 * H),
        };
        var taskWindows = new JsMap<string, List<MinuteWindow>>();
        taskWindows.Set("admin", [new MinuteWindow(6 * H, 8 * H + 30)]);
        Paint(shifts, Copy(demand), Allowed, new PaintOptions { TaskWindows = taskWindows });
        var dateChecks = shifts.SelectMany(s => s.Blocks).Count(b => b.Group == "admin" && b.Time < 8 * H + 30);
        Assert.Equal(10, dateChecks);
        LegalBreaks(shifts);
    }

    // The closing manager and the evening Admin (owner decisions 7 and R1,
    // 13/11/2026): Fresh Ordering runs from 3h30 before close to close, and the
    // cash-up and End of Day to the end of the close-down, one manager deep, with
    // a few spare minutes for the closing manager. A break inside that pushes
    // Admin out; before it, the manager's floor work is handed on.

    [Fact]
    public void Puts_the_closing_managers_meal_break_before_a_tight_Fresh_Ordering_window()
    {
        // Close 20:00, staffed to 20:30. Before 16:30 the closer is on Reset
        // beside a till and a second Reset - nobody idle. From 16:30 Admin runs
        // one deep to 20:30 except 18:00: one spare quarter-hour. Priced one
        // quarter-hour at a time, that one spare "made up" BOTH halves of a break
        // at 16:30, the break went in there, and a quarter-hour of Admin was lost.
        var demand = new List<DemandBlock>();
        for (var m = 13 * H; m < 20 * H + 30; m += 15)
        {
            var g = new JsMap<string, double>();
            if (m < 16 * H + 30)
            {
                g.Set("checkout", 1);
                g.Set("reset", 2);
            }
            else
            {
                if (m != 18 * H) g.Set("admin", 1);
                if (m < 20 * H) g.Set("checkout", 1);
                if (m >= 17 * H) g.Set("cleaning", 1);
            }
            demand.Add(D(m, g.Values().Sum(), g));
        }
        var closer = Shift("ASM", 13 * H, 20 * H + 30);
        var shifts = new List<PaintableShift>
        {
            closer,
            Shift("SA", 13 * H, 17 * H),
            Shift("SA", 13 * H, 17 * H),
            Shift("SA", 16 * H + 30, 20 * H + 30),
            Shift("SA", 16 * H + 30, 20 * H + 30),
        };
        var taskWindows = new JsMap<string, List<MinuteWindow>>();
        taskWindows.Set("admin", [new MinuteWindow(16 * H + 30, 20 * H), new MinuteWindow(19 * H + 30, 20 * H + 30)]);
        var r = Paint(shifts, demand, null, new PaintOptions { BreakEndBy = 18 * H, TaskWindows = taskWindows });
        var brk = BreakTimes(closer);
        Assert.Equal(2, brk.Count);
        foreach (var t in brk) Assert.True(t + 15 <= 16 * H + 30, "the break ends by 16:30");
        Assert.Equal(15, Count(shifts, "admin"));
        Assert.DoesNotContain(r.Lost, l => l.Group == "admin");
        LegalBreaks(shifts);
    }

    [Fact]
    public void Makes_up_Admin_the_only_managers_break_displaces_inside_the_tasks_own_window()
    {
        // The only manager, Admin wall to wall except two quarter-hours in the
        // first hour, where no break may sit. Every legal break displaces Admin,
        // and it moves to those two quarter-hours - inside the same task window.
        var demand = new List<DemandBlock>();
        for (var m = 9 * H; m < 15 * H; m += 15)
        {
            var spare = m == 9 * H + 15 || m == 9 * H + 30;
            demand.Add(D(m, spare ? 0 : 1, spare ? G() : G(("admin", 1))));
        }
        var mgr = Shift("ASM", 9 * H, 15 * H);
        var taskWindows = new JsMap<string, List<MinuteWindow>>();
        taskWindows.Set("admin", [new MinuteWindow(9 * H, 15 * H)]);
        var r = Paint([mgr], demand, null, new PaintOptions { TaskWindows = taskWindows });
        Assert.Equal(2, BreakTimes(mgr).Count);
        Assert.Equal(22, Count(mgr, "admin"));
        Assert.Empty(r.Lost);
        LegalBreaks([mgr]);
    }

    [Fact]
    public void Reports_the_Admin_the_only_managers_break_displaces_when_nothing_in_its_window_is_free()
    {
        var demand = new List<DemandBlock>();
        for (var m = 9 * H; m < 15 * H; m += 15) demand.Add(D(m, 1, G(("admin", 1))));
        var mgr = Shift("ASM", 9 * H, 15 * H);
        var taskWindows = new JsMap<string, List<MinuteWindow>>();
        taskWindows.Set("admin", [new MinuteWindow(9 * H, 15 * H)]);
        var r = Paint([mgr], demand, null, new PaintOptions { TaskWindows = taskWindows });
        Assert.Equal(22, Count(mgr, "admin"));
        // Lost, for the caller to flag by name (lostWorkFlag) - not dropped silently.
        Assert.Equal([new LostWork("admin", 2)], r.Lost);
        LegalBreaks([mgr]);
    }
}
