// Port of src/lib/stage2/engine.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Painter.PaintFixtures;

namespace Roster.Engine.Tests.Rostering.Painter;

public class EngineTests
{
    [Fact]
    public void Eligibility_only_managers_may_do_Admin_JSAs_cant_do_Checkout()
    {
        Assert.True(TaskGroups.EligibleFor("SM", "admin"));
        Assert.False(TaskGroups.EligibleFor("SA", "admin"));
        Assert.False(TaskGroups.EligibleFor("JSA", "checkout"));
        Assert.True(TaskGroups.EligibleFor("JSA", "cleaning"));
    }

    [Fact]
    public void Gives_the_SA_the_registers_and_the_manager_the_Admin()
    {
        var sa = Shift("SA", 9 * 60, 12 * 60);
        var asm = Shift("ASM", 9 * 60, 12 * 60);
        Paint([sa, asm], DemandRange(9 * 60, 12 * 60, 2, G(("checkout", 1), ("admin", 1))));
        // Break tiles are placed by the painter now; while the SA breaks, the
        // manager covers the till (a till is never unmanned).
        foreach (var b in sa.Blocks) Assert.Contains(b.Group, new[] { "checkout", "break" });
        foreach (var b in asm.Blocks) Assert.Contains(b.Group, new[] { "admin", "checkout", "break" });
        Assert.True(Count(asm, "admin") >= 8);
    }

    [Fact]
    public void Keeps_managers_off_the_registers_whenever_any_other_demanded_task_exists()
    {
        // One checkout + one cleaning body per block: the SA mans the till, the
        // manager takes the cleaning (checkout is a manager's LAST choice) - the
        // manager only covers the till in the SA's break blocks.
        var asm = Shift("ASM", 9 * 60, 12 * 60);
        var sa = Shift("SA", 9 * 60, 12 * 60);
        Paint([asm, sa], DemandRange(9 * 60, 12 * 60, 2, G(("checkout", 1), ("cleaning", 1))));
        var saBreaks = Count(sa, "break");
        foreach (var b in sa.Blocks) Assert.Contains(b.Group, new[] { "checkout", "break" });
        Assert.True(Count(asm, "checkout") <= saBreaks);
        Assert.True(Count(asm, "cleaning") >= 8);
    }

    [Fact]
    public void A_manager_still_mans_a_till_when_checkout_is_the_only_work_left()
    {
        // Two checkout bodies demanded, one SA + one manager: the manager's block
        // has nothing but checkout remaining - they take it (coverage wins). Even
        // the morning-admin mandate stands down here: every opening-hour block is
        // a till claim, and converting one would leave a register unmanned.
        var shifts = new List<PaintableShift> { Shift("ASM", 9 * 60, 12 * 60), Shift("SA", 9 * 60, 12 * 60) };
        Paint(shifts, DemandRange(9 * 60, 12 * 60, 2, G(("checkout", 2))));
        foreach (var b in shifts[0].Blocks) Assert.Contains(b.Group, new[] { "checkout", "break" });
        foreach (var b in shifts[1].Blocks) Assert.Contains(b.Group, new[] { "checkout", "break" });
    }

    [Fact]
    public void Marks_undirected_demand_as_explicit_non_op_tiles()
    {
        var s = Shift("SA", 9 * 60, 12 * 60);
        Paint([s], DemandRange(9 * 60, 12 * 60, 1, G()));
        foreach (var b in s.Blocks) Assert.Contains(b.Group, new[] { "non_op", "break" });
        Assert.True(Count(s, "non_op") >= 10);
    }

    [Fact]
    public void Never_paints_a_task_outside_its_labour_group_window()
    {
        // Load Running windowed to 09:00-10:30; reset legal 10:00+. No tile may
        // carry LR past its window, and no tile stays idle (flex).
        var d = new List<DemandBlock>();
        for (double m = 9 * 60; m < 12 * 60; m += 15)
            d.Add(D(m, 1, m < 10 * 60 + 30 ? G(("load_running", 1)) : G()));
        var allowed = new Dictionary<double, List<string>>();
        for (double m = 9 * 60; m < 12 * 60; m += 15)
        {
            var gs = new List<string>();
            if (m < 10 * 60 + 30) gs.Add("load_running");
            if (m >= 10 * 60) gs.Add("reset");
            allowed[m] = gs;
        }
        var s = Shift("SM", 9 * 60, 12 * 60);
        Paint([s], d, t => allowed.GetValueOrDefault(t) ?? []);
        foreach (var b in s.Blocks)
        {
            if (b.Group == "load_running") Assert.True(b.Time < 10 * 60 + 30);
            if (b.Group == "reset") Assert.True(b.Time >= 10 * 60);
            Assert.NotEqual("flex", b.Group); // no idle tiles
        }
    }

    [Fact]
    public void Respects_per_employee_restricted_tasks()
    {
        var s = Shift("SA", 9 * 60, 10 * 60, restricted: "cleaning");
        Paint([s], DemandRange(9 * 60, 10 * 60, 1, G(("cleaning", 1))));
        foreach (var b in s.Blocks) Assert.NotEqual("cleaning", b.Group);
    }

    [Fact]
    public void Leaves_UNFILLED_rows_without_tiles()
    {
        var s = Shift("SA", 9 * 60, 12 * 60, unfilled: true);
        Paint([s], DemandRange(9 * 60, 12 * 60, 1, G(("cleaning", 1))));
        Assert.Empty(s.Blocks);
    }

    // ── morning admin ──
    // The opening manager starts on admin - made of admin the day already has,
    // moved forward, never new admin hours on top of the benchmark (owner
    // decision 5; Roster Engine Review 07/11/2026).

    [Fact]
    public void Morning_admin_moves_the_opening_managers_own_admin_to_their_first_quarter_hour_and_only_theirs()
    {
        // One till (claimed by the SA - register etiquette) + cleaning for the
        // rest, and one quarter-hour of admin the opener holds at 09:00. It moves
        // to their 08:00 start, swapping with that block's cleaning.
        var mgr = Shift("ASM", 8 * 60, 12 * 60);
        var late = Shift("ASM", 10 * 60, 14 * 60);
        var sa = Shift("SA", 8 * 60, 12 * 60);
        var demand = DemandRange(8 * 60, 14 * 60, 3, G(("checkout", 1), ("cleaning", 2)));
        demand.Find(d => d.Time == 9 * 60)!.Groups = G(("checkout", 1), ("cleaning", 1), ("admin", 1));
        Paint([mgr, late, sa], demand);
        Assert.Equal("admin", mgr.Blocks.Find(b => b.Time == mgr.Start)?.Group);
        Assert.Equal(1, Count([mgr, late, sa], "admin"));
        Assert.NotEqual("admin", late.Blocks.Find(b => b.Time == late.Start)?.Group);
        Assert.NotEqual("admin", sa.Blocks.Find(b => b.Time == sa.Start)?.Group);
    }

    [Fact]
    public void Morning_admin_invents_no_admin_when_the_day_has_none()
    {
        var mgr = Shift("ASM", 8 * 60, 12 * 60);
        var late = Shift("ASM", 10 * 60, 14 * 60);
        var sa = Shift("SA", 8 * 60, 12 * 60);
        Paint([mgr, late, sa], DemandRange(8 * 60, 14 * 60, 3, G(("checkout", 1), ("cleaning", 2))));
        Assert.Equal(0, Count([mgr, late, sa], "admin"));
    }

    [Fact]
    public void Morning_admin_skips_a_checkout_claim_and_lands_on_the_first_free_opening_hour_block()
    {
        // Two tills, two bodies at open: the manager's 08:00 block is a till claim
        // (both bodies are needed). A third body arrives at 08:30 freeing the
        // manager's later opening-hour blocks - their 11:00 admin moves to the
        // first of those.
        var mgr = Shift("ASM", 8 * 60, 12 * 60);
        var sa = Shift("SA", 8 * 60, 12 * 60);
        var sa2 = Shift("SA", 8 * 60 + 30, 12 * 60);
        var demand = DemandRange(8 * 60, 12 * 60, 3, G(("checkout", 2), ("cleaning", 1)));
        demand.Find(d => d.Time == 11 * 60)!.Groups = G(("checkout", 2), ("admin", 1));
        Paint([mgr, sa, sa2], demand);
        var opening = mgr.Blocks.Where(b => b.Time < 9 * 60).ToList();
        Assert.Equal(1, opening.Count(b => b.Group == "admin"));
        Assert.Equal(1, Count([mgr, sa, sa2], "admin"));
        // The claims before the second SA arrives are untouched checkout.
        Assert.Equal("checkout", opening.Find(b => b.Time == mgr.Start)?.Group);
    }

    // ── break end-of-day cut-off ──

    [Fact]
    public void Pulls_an_afternoon_shifts_break_before_1800_when_the_store_closes_early()
    {
        // 12:00-20:00 SA: midpoint anchor is 16:00; without the cut-off a segment
        // could legally sit up to 19:00. With breakEndBy 18:00 it must end by then.
        var sa = Shift("SA", 12 * 60, 20 * 60);
        Paint([sa], DemandRange(12 * 60, 20 * 60, 1, G(("checkout", 0), ("cleaning", 3))), null, new PaintOptions { BreakEndBy = 18 * 60 });
        var breaks = BreakTimes(sa);
        Assert.NotEmpty(breaks);
        foreach (var t in breaks) Assert.True(t + 15 <= 18 * 60);
    }

    [Fact]
    public void Still_awards_the_break_late_when_no_legal_position_exists_before_the_cut_off()
    {
        // 17:00-23:00 shift: the first legal block starts 18:00 (first-hour edge),
        // already past the cut-off - the award break wins over the preference.
        var sa = Shift("SA", 17 * 60, 23 * 60);
        Paint([sa], DemandRange(17 * 60, 23 * 60, 1, G(("checkout", 0), ("cleaning", 3))), null, new PaintOptions { BreakEndBy = 18 * 60 });
        Assert.NotEmpty(BreakTimes(sa));
    }

    // ── scarce skills, continuity and gaps ──

    // Task changes only - a break is not a switch of job.
    private static int TaskSwitches(PaintableShift s)
    {
        var g = s.Blocks.Select(b => b.Group).Where(x => x != "break").ToList();
        return g.Where((x, i) => i > 0 && x != g[i - 1]).Count();
    }

    [Fact]
    public void Gives_the_manager_only_work_to_the_manager_not_the_bulk_job_anyone_can_do()
    {
        // A load-heavy morning: 1 Admin (manager-only, windowed to 08:30) and 2
        // Load Running per block, but only two bodies. Straight TASK_PRIORITY put
        // Load Running first for everyone, so the one manager was pulled onto the
        // load and the Admin behind it went unfilled - then got relocated to a
        // spare block hours later, outside the window the task had to finish in.
        var demand = new List<DemandBlock>();
        var allowed = new Dictionary<double, List<string>>();
        for (double m = 6 * 60; m < 8 * 60 + 30; m += 15)
        {
            demand.Add(D(m, 3, G(("admin", 1), ("load_running", 2))));
            allowed[m] = ["admin", "load_running"];
        }
        var mgr = Shift("ASM", 6 * 60, 8 * 60 + 30);
        var sa = Shift("SA", 6 * 60, 8 * 60 + 30);
        Paint([mgr, sa], demand, t => allowed.GetValueOrDefault(t) ?? []);
        // Every working block of the manager's shift is the admin only they can do.
        Assert.Equal(mgr.Blocks.Count(b => b.Group != "break"), Count(mgr, "admin"));
        Assert.DoesNotContain(mgr.Blocks, b => b.Group == "load_running");
        // …and the SA carries the load running, which anyone could have done.
        Assert.Contains(sa.Blocks, b => b.Group == "load_running");
    }

    [Fact]
    public void Keeps_each_person_on_one_job_instead_of_ping_ponging_between_two()
    {
        // Specials needs one body for an hour midday; Admin covers the rest. Only
        // the person who takes Specials should change job, and only in and out.
        var demand = new List<DemandBlock>();
        for (var i = 0; i < 12; i++)
        {
            var sp = i >= 4 && i < 8 ? 1 : 0;
            demand.Add(D(11 * 60 + i * 15, 2, G(("admin", 2 - sp), ("specials", sp))));
        }
        var a = Shift("ASM", 11 * 60, 14 * 60);
        var b = Shift("DSM", 11 * 60, 14 * 60);
        Paint([a, b], demand);
        Assert.True(TaskSwitches(a) + TaskSwitches(b) <= 2);
    }

    [Fact]
    public void Runs_the_job_on_through_a_mid_shift_hole_while_it_still_owes_hours()
    {
        // Three blocks in the middle of the day carry no demand of their own, but
        // cleaning is short elsewhere: 12 blocks are demanded across the day and
        // only 9 landed in the main pass. The person is rostered and present, so
        // the job either side simply runs on through the hole and the day comes
        // out whole - no blank tiles, no invented hours.
        var demand = new List<DemandBlock>();
        for (var i = 0; i < 12; i++)
        {
            var idle = i >= 4 && i < 7;
            demand.Add(D(9 * 60 + i * 15, idle ? 0 : 2, idle ? G() : G(("cleaning", 2))));
        }
        var s = Shift("SA", 9 * 60, 12 * 60);
        Paint([s], demand);
        Assert.DoesNotContain(s.Blocks, b => b.Group == "flex");
        Assert.All(s.Blocks, b => Assert.Contains(b.Group, new[] { "cleaning", "break" }));
    }

    [Fact]
    public void Never_takes_manager_only_work_to_do_work_anyone_could_do()
    {
        // The pre-open load: one manager, three SAs, and the date check only a
        // manager may do, windowed to finish by 08:30. Demand matches the bodies
        // exactly, so every award break leaves Load Running a body short - and the
        // day has spare blocks later where admin is legal but load is not.
        //
        // That is precisely the shape the steal cascade used to get wrong: it
        // closed the load gap by taking the manager OFF the date check to run load
        // an SA could have run, and re-homed the admin at 09:30 - past the
        // deadline the task was windowed to. Two hours of date checks came out as
        // one. Scarce skills are never robbed to serve abundant ones.
        var demand = new List<DemandBlock>();
        for (double m = 6 * 60; m < 8 * 60 + 30; m += 15) demand.Add(D(m, 4, G(("load_running", 3), ("admin", 1))));
        for (double m = 8 * 60 + 30; m < 9 * 60 + 30; m += 15) demand.Add(D(m, 4, G(("cleaning", 4))));
        var shifts = new List<PaintableShift>
        {
            Shift("ASM", 6 * 60, 10 * 60 + 30),
            Shift("SA", 6 * 60, 10 * 60 + 30),
            Shift("SA", 6 * 60, 10 * 60 + 30),
            Shift("SA", 6 * 60, 10 * 60 + 30),
        };
        Paint(shifts, demand, t =>
            t < 8 * 60 + 30 ? ["load_running", "admin"] : t < 9 * 60 + 30 ? ["cleaning"] : ["admin"]);
        var inWindow = shifts.SelectMany(s => s.Blocks).Count(b => b.Group == "admin" && b.Time < 8 * 60 + 30);
        // Every admin block the manager is free to work stays inside the window -
        // eight of the ten, the other two being the manager's own break. Before
        // the rule this was four, the rest having been traded away for load.
        Assert.Equal(8, inWindow);
    }

    [Fact]
    public void Takes_a_body_off_the_aisle_when_a_till_would_otherwise_be_unmanned()
    {
        // Two tills wanted from 10:00 and two bodies on the floor, both of them
        // mid-way through a reset that runs 10:00-14:00. The registers are the one
        // thing that cannot move - a till is manned in its own block or not at all
        // - so one of them comes off the aisle, and the reset it was doing goes
        // back on the pile to be picked up later in its own window.
        var demand = new List<DemandBlock>();
        for (double m = 10 * 60; m < 14 * 60; m += 15)
            demand.Add(D(m, 2, m < 11 * 60 ? G(("checkout", 2)) : G(("reset", 2))));
        var shifts = new List<PaintableShift> { Shift("SA", 10 * 60, 14 * 60), Shift("SA", 10 * 60, 14 * 60) };
        Paint(shifts, demand, _ => ["checkout", "reset"]);
        for (double m = 10 * 60; m < 11 * 60; m += 15)
        {
            var tills = shifts.SelectMany(s => s.Blocks).Count(b => b.Time == m && b.Group == "checkout");
            Assert.True(tills == 2, $"{m}");
        }
    }

    [Fact]
    public void Makes_Reset_up_anywhere_in_the_Reset_window_Admin_only_inside_its_own_jobs_window()
    {
        // Ten hours of window, two jobs: one in the morning (10:00-14:00) and one
        // in the evening (17:30-20:00). The morning has one body for two units of
        // demand; the evening has a spare body.
        static bool Morning(double m) => m < 12 * 60;
        static bool Evening(double m) => m >= 18 * 60 && m < 19 * 60;
        static List<DemandBlock> DemandOf(string g)
        {
            var out_ = new List<DemandBlock>();
            for (double m = 10 * 60; m < 20 * 60; m += 15)
            {
                var n = Morning(m) ? 2 : Evening(m) ? 1 : 0;
                out_.Add(D(m, n, n != 0 ? G((g, n)) : G()));
            }
            return out_;
        }
        var jobs = new List<MinuteWindow> { new(10 * 60, 14 * 60), new(17 * 60 + 30, 20 * 60) };

        // Reset is loose (owner decision 11): the morning's unmet re-run is made
        // up by the evening body, still inside the Reset window.
        var resetShifts = new List<PaintableShift> { Shift("SA", 10 * 60, 13 * 60), Shift("SA", 17 * 60, 20 * 60) };
        var taskWindows = new JsMap<string, List<MinuteWindow>>();
        taskWindows.Set("reset", jobs);
        Paint(resetShifts, DemandOf("reset"), null, new PaintOptions { TaskWindows = taskWindows });
        Assert.True(Count(resetShifts[1], "reset") > 4);

        // Admin is strict: each quarter-hour belongs to its job, and the evening
        // manager does the evening job's four blocks and no more - the morning's
        // unmet Admin stays unmet rather than being "made up" outside its window,
        // even though a due "any time" task's window contains both minutes (review
        // RC-9: that is how date checks owed at 07:45 were "made up" at 12:15).
        var unit = new JsMap<double, List<JobWindow>>();
        for (double m = 10 * 60; m < 20 * 60; m += 15)
        {
            if (Morning(m)) unit.Set(m, [new JobWindow(jobs[0].Start, jobs[0].End)]);
            if (Evening(m)) unit.Set(m, [new JobWindow(jobs[1].Start, jobs[1].End)]);
        }
        var adminShifts = new List<PaintableShift> { Shift("ASM", 10 * 60, 13 * 60), Shift("ASM", 17 * 60, 20 * 60) };
        var adminWindows = new JsMap<string, List<MinuteWindow>>();
        adminWindows.Set("admin", [.. jobs, new MinuteWindow(10 * 60, 20 * 60)]);
        var unitWindows = new JsMap<string, JsMap<double, List<JobWindow>>>();
        unitWindows.Set("admin", unit);
        Paint(adminShifts, DemandOf("admin"), null, new PaintOptions { TaskWindows = adminWindows, UnitWindows = unitWindows });
        Assert.Equal(4, Count(adminShifts[1], "admin"));
    }

    [Fact]
    public void Never_invents_labour_group_hours_or_Non_Op_to_fill_a_gap()
    {
        // Two bodies present all morning, but cleaning is only doubled at each end
        // of the day. The second shift therefore has real work either side of a
        // long hole - exactly the shape the gap pass fills - and no cleaning hours
        // left owing to fill it with.
        //
        // Continuing the job through the hole would read as two hours of cleaning
        // nobody asked for, and the Benchmark table counts tiles. Re-colouring it
        // Non-Op is no better: Non-Op is a benchmarked group in its own right, and
        // is usually zero. The spare time stays BLANK - it is over-cover, and it
        // belongs on the shaper's over-cover flag rather than inside a labour
        // group's hours.
        var demand = new List<DemandBlock>();
        for (var i = 0; i < 12; i++)
        {
            var ends = i < 2 || i >= 10;
            demand.Add(D(9 * 60 + i * 15, ends ? 2 : 1, G(("cleaning", ends ? 2 : 1))));
        }
        var shifts = new List<PaintableShift> { Shift("SA", 9 * 60, 12 * 60), Shift("SA", 9 * 60, 12 * 60) };
        Paint(shifts, demand);
        var demanded = demand.Sum(d => d.GroupCount("cleaning"));
        var blocks = shifts.SelectMany(s => s.Blocks).ToList();
        Assert.True(blocks.Count(b => b.Group == "cleaning") <= demanded);
        Assert.DoesNotContain(blocks, b => b.Group == "non_op");
        Assert.Contains(blocks, b => b.Group == "flex");
    }

    [Fact]
    public void Leaves_an_over_long_shifts_trailing_empty_blocks_visible()
    {
        // Only the FIRST hour carries demand. The tail is not a painting glitch -
        // the shift is longer than the day's work - so it stays blank instead of
        // being padded with hours nothing asked for.
        var demand = new List<DemandBlock>();
        for (var i = 0; i < 12; i++)
            demand.Add(D(9 * 60 + i * 15, i < 4 ? 1 : 0, i < 4 ? G(("cleaning", 1)) : G()));
        var s = Shift("SA", 9 * 60, 12 * 60);
        Paint([s], demand);
        Assert.Contains(s.Blocks, b => b.Group == "flex");
    }

    [Fact]
    public void Reassigns_within_a_block_without_changing_what_the_block_contains()
    {
        // The continuity pass may only move jobs BETWEEN people at the same time -
        // never add, drop or retime one, or the plan would stop reconciling.
        var demand = new List<DemandBlock>();
        for (var i = 0; i < 16; i++)
            demand.Add(D(10 * 60 + i * 15, 3, G(("cleaning", i % 2 == 0 ? 2 : 1), ("reset", i % 2 == 0 ? 1 : 2))));
        var shifts = new List<PaintableShift>
        {
            Shift("SA", 10 * 60, 14 * 60),
            Shift("SA", 10 * 60, 14 * 60),
            Shift("JSA", 10 * 60, 14 * 60),
        };
        Paint(shifts, demand);
        foreach (var d in demand)
        {
            var at = shifts.SelectMany(s => s.Blocks.Where(b => b.Time == d.Time)).ToList();
            foreach (var g in new[] { "cleaning", "reset" })
            {
                var painted = at.Count(b => b.Group == g);
                var onBreak = at.Count(b => b.Group == "break");
                // Everything the block asked for is painted, unless a break took a body.
                Assert.True(painted <= d.GroupCount(g) + onBreak);
            }
        }
    }
}
