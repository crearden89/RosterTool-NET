// Port of src/lib/stage1/engine.adminTasks.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Stage1;

public class Stage1AdminTasksTests
{
    // A plain 08:00-18:00 day, staffed 06:00-18:30, with a national admin window
    // 09:00-17:00. Only admin hours are in play - the other groups are zeroed.
    private static Stage1Input Base(Func<Stage1Input, Stage1Input>? over = null)
    {
        var b = new Stage1Input
        {
            TradingOpen = 8 * 60,
            TradingClose = 18 * 60,
            StaffOpen = 6 * 60,
            StaffClose = 18 * 60 + 30,
            Benchmark90 = S1.Bench(("admin", 4)),
            Windows = [S1.W("admin", 9 * 60, 17 * 60)],
            Checkout = [],
        };
        return over is null ? b : over(b);
    }

    private static List<PlannedBlock> AdminBlocks(Stage1Result r) => S1.Blocks(r, "admin");

    private static double Heads(IEnumerable<PlannedBlock> bs) => bs.Aggregate(0.0, (n, b) => n + b.Headcount);

    private static Stage1Task T(string name, double minutes, double start, double end) => new(name, minutes, start, end);

    [Fact]
    public void Never_lets_the_rounding_carry_shorten_Mondays_date_checks_RC36()
    {
        // Two 8-minute evening jobs each take a whole block, and used to hand
        // -14 minutes on to the next job: 120 minutes of date checks got seven
        // blocks (105).
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            StaffClose = 20 * 60 + 30,
            TradingClose = 20 * 60,
            Benchmark90 = S1.Bench(("admin", 3)),
            Windows = [S1.W("admin", 6 * 60, 20 * 60 + 30)],
            AdminTasks =
            [
                T("Register Assignment", 8, 19 * 60 + 30, 20 * 60),
                T("UKG End of Day", 8, 20 * 60, 20 * 60 + 30),
                T("Longlife Date Checks", 120, 6 * 60, 8 * 60 + 30),
            ],
        }));
        Assert.Equal(8, Heads(AdminBlocks(r).Where(b => b.Time < 8 * 60 + 30)));
    }

    [Fact]
    public void Each_tasks_blocks_land_inside_its_own_window()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            AdminTasks = [T("APM", 15, 9 * 60, 12 * 60), T("CGOS", 60, 13 * 60, 16 * 60)],
            Benchmark90 = S1.Bench(("admin", 1.25)),
        }));
        var blocks = AdminBlocks(r);
        // 1.25h benchmark = exactly the two tasks (15 + 60 min) - no remainder.
        Assert.Equal(5, Heads(blocks));
        Assert.All(blocks, b => Assert.True((b.Time >= 9 * 60 && b.Time < 12 * 60) || (b.Time >= 13 * 60 && b.Time < 16 * 60)));
        // The 60-min task is one contiguous run.
        var cgos = blocks.Where(b => b.Time >= 13 * 60).ToList();
        for (var i = 1; i < cgos.Count; i++) Assert.Equal(cgos[i - 1].Time + 15, cgos[i].Time);
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void A_task_window_outside_the_national_admin_window_still_places_after_close()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 0.25)),
            // 20 min rounds to ONE block (nearest, not ceiling) - the 5-minute
            // tail carries forward rather than claiming a whole extra block.
            AdminTasks = [T("Register Cash-Up", 20, 18 * 60, 18 * 60 + 30)],
        }));
        // One block inside the 18:00-18:30 task window (the placer prefers the
        // latest equal-cost slot, so it lands at 18:15).
        Assert.Equal([18 * 60 + 15.0], AdminBlocks(r).Select(b => b.Time));
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void Rounds_task_blocks_with_a_running_carry_so_the_total_tracks_the_minutes()
    {
        // Three 20-min tasks = 60 min. Per-task ceiling would claim 6 blocks
        // (1.5h) and falsely flag the benchmark short; the carry yields 4 blocks.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 1)),
            AdminTasks = [T("A", 20, 9 * 60, 12 * 60), T("B", 20, 12 * 60, 15 * 60), T("C", 20, 14 * 60, 17 * 60)],
        }));
        Assert.Equal(4, Heads(AdminBlocks(r)));
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void A_tiny_task_still_claims_one_block_even_when_the_carry_argues_it_away()
    {
        // 17 min then 5 min: the first task rounds down to one block carrying +2
        // forward, so the 5-min task's 7 wanted minutes would round to zero
        // blocks - but real work always gets a body for at least one block.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 0.5)),
            AdminTasks = [T("Long", 17, 9 * 60, 12 * 60), T("Tiny", 5, 13 * 60, 16 * 60)],
        }));
        var blocks = AdminBlocks(r);
        Assert.Equal(2, Heads(blocks));
        // One block in each task's own window.
        Assert.Contains(blocks, b => b.Time >= 9 * 60 && b.Time < 12 * 60);
        Assert.Contains(blocks, b => b.Time >= 13 * 60 && b.Time < 16 * 60);
    }

    [Fact]
    public void Remaining_benchmark_admin_tops_up_in_the_national_windows()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 3)),
            AdminTasks = [T("APM", 60, 9 * 60, 12 * 60)],
        }));
        Assert.Equal(12, Heads(AdminBlocks(r))); // full 3h placed: 1h task + 2h generic
        // Generic remainder stays inside the national admin window 09:00-17:00.
        Assert.All(AdminBlocks(r), b => Assert.True(b.Time >= 9 * 60 && b.Time < 17 * 60));
        Assert.Equal(3, S1.Alloc(r, "admin").AllocatedHours);
    }

    // Every Admin task is planned, at its own time, even past the benchmark
    // (owner decision 1, 12/11/2026): a benchmark short of the tasks is an hours
    // issue, not a task problem. (From 07/11 to 13/11 these tests asserted the
    // opposite - tasks refused once the benchmark was used up.)
    [Fact]
    public void Plans_a_task_in_full_past_the_benchmark_and_a_rounding_block_is_not_an_hours_issue()
    {
        // Benchmark 0.6h is 2 blocks; a 38-min task rounds to 3 - 0.15h over.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 0.6)),
            AdminTasks = [T("Snug", 38, 9 * 60, 17 * 60)],
        }));
        Assert.Equal(3, Heads(AdminBlocks(r)));
        Assert.DoesNotContain(r.Flags, f => f.Contains("not planned"));
        // Counted as planned over the benchmark on purpose, but no warning for it.
        Assert.Equal([("admin", 0.15)], r.HoursIssues!.Select(h => (h.Group, Js.Number(Js.ToFixed(h.Hours, 2)))));
        Assert.DoesNotContain(r.Flags, f => f.StartsWith("Admin hours issue", StringComparison.Ordinal));
    }

    [Fact]
    public void Plans_every_task_past_the_benchmark_and_flags_the_gap_as_an_hours_issue()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 0.5)),
            AdminTasks = [T("Big", 120, 9 * 60, 17 * 60)],
        }));
        Assert.Equal(8, Heads(AdminBlocks(r)));
        Assert.DoesNotContain(r.Flags, f => f.Contains("not planned"));
        Assert.Contains("Admin hours issue: 1.50h over the benchmark (0.50h) - the Admin tasks due today need more than it holds", r.Flags);
        Assert.Equal([new HoursIssue("admin", 1.5)], r.HoursIssues!);
        // An hours issue is not a breach of the ±1 hour check.
        Assert.True(r.WithinTolerance);
    }

    [Fact]
    public void Packs_short_tasks_that_share_a_window_into_shared_quarter_hours()
    {
        // 6 + 17 + 30 minutes of close-down work: an hour when each claimed its
        // own blocks, four quarter-hours as one job.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 1)),
            AdminTasks =
            [
                T("UKG End of Day", 6, 16 * 60, 17 * 60),
                T("End of Day", 17, 16 * 60, 17 * 60),
                T("Cash-Up", 30, 16 * 60, 17 * 60),
            ],
        }));
        Assert.Equal(4, Heads(AdminBlocks(r).Where(b => b.Time >= 16 * 60 && b.Time < 17 * 60)));
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void Flags_a_task_whose_window_misses_the_staffed_day()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with { AdminTasks = [T("Ghost", 30, 20 * 60, 21 * 60)] }));
        Assert.Contains(r.Flags, f => f.Contains("admin task 'Ghost'"));
    }

    [Fact]
    public void Without_tasks_the_flat_behaviour_is_unchanged() =>
        Assert.Equal(16, Heads(AdminBlocks(Stage1Engine.RunStage1(Base())))); // 4h in the 09:00-17:00 window

    [Fact]
    public void Tasks_whose_windows_overlap_run_one_after_the_other_not_on_top()
    {
        // Three half-hour jobs, all free to sit anywhere in the admin window.
        // Placed independently they each chose the same rank-best run and the day
        // demanded three managers for one 30-minute slice - which is how G248's
        // Monday came to ask for five managers on admin at 17:45, and why the
        // shaper then cut a handful of unusable evening shifts.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 1.5)),
            AdminTasks = [T("A", 30, 9 * 60, 17 * 60), T("B", 30, 9 * 60, 17 * 60), T("C", 30, 9 * 60, 17 * 60)],
        }));
        var blocks = AdminBlocks(r);
        Assert.Equal(6, Heads(blocks)); // all 1.5h kept
        Assert.Equal(1, blocks.Max(b => b.Headcount));
        Assert.Equal(6, blocks.Count); // six distinct minutes, not two stacked
    }

    [Fact]
    public void Plans_Admin_one_manager_deep_two_only_for_the_handover_and_keeps_a_task_in_its_window()
    {
        // Six hours of admin due inside a two-hour window is three managers at
        // once. Admin is planned one manager deep, and two deep only where the
        // job has no room otherwise - for the handover to cover (owner decision
        // 7). The rest used to move to any other admin minute of the day (Roster
        // Engine Review 07/11/2026, RC5: after-close cash-up planned at 07:00); a
        // task's window is kept, and what cannot fit in it is flagged by name.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 6)),
            AdminTasks = [T("Crunch", 360, 10 * 60, 12 * 60)],
        }));
        var blocks = AdminBlocks(r);
        Assert.Equal(2, blocks.Max(b => b.Headcount));
        Assert.All(blocks, b => Assert.True(b.Time >= 10 * 60 && b.Time < 12 * 60));
        Assert.Equal(16, Heads(blocks)); // 2 deep x 2h
        Assert.Contains(r.Flags, f => f.Contains("admin task 'Crunch': 120 min could not be planned inside its own window"));
    }

    [Fact]
    public void Outside_trading_hours_only_one_manager_is_in_the_building()
    {
        // Pre-open and post-close the store has exactly one manager, so admin
        // cannot be two-deep there however the task windows fall.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("admin", 2)),
            Windows = [S1.W("admin", 6 * 60, 18 * 60 + 30)],
            AdminTasks = [T("Open prep", 60, 6 * 60, 8 * 60), T("Cash-up", 60, 18 * 60, 18 * 60 + 30)],
        }));
        foreach (var b in AdminBlocks(r))
            if (b.Time < 8 * 60 || b.Time >= 18 * 60) Assert.Equal(1, b.Headcount);
    }
}
