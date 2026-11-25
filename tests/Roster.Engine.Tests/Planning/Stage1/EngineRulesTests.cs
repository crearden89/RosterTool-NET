// Port of src/lib/stage1/engine.rules.test.ts.
// ─── Operating-rules engine behaviour: quotas, strategies, reset tasks ───────

using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Stage1;

public class Stage1RulesTests
{
    private const double H = 60;

    private static Stage1Input Base(Func<Stage1Input, Stage1Input>? over = null)
    {
        var b = new Stage1Input
        {
            TradingOpen = 8 * H + 30,
            TradingClose = 20 * H,
            StaffOpen = 6 * H,
            StaffClose = 20 * H + 15,
            Benchmark90 = S1.Bench(),
            Windows = [],
            Checkout = [],
        };
        return over is null ? b : over(b);
    }

    private static double HoursAt(Stage1Result r, string group, double from, double to) =>
        r.Blocks.Where(b => b.Group == group && b.Time >= from && b.Time < to).Aggregate(0.0, (s, b) => s + b.Headcount) / 4;

    private static WindowRule Quota(string group, double start, double end, double hours) => new(group, start, end, hours);

    // ── per-window hour quotas ──

    [Fact]
    public void Pins_1h_of_cleaning_at_0900_and_sends_the_rest_to_the_evening()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("cleaning", 3)),
            Windows = [Quota("cleaning", 9 * H, 10 * H, 1), S1.W("cleaning", 18 * H, 20 * H + 15)],
        }));
        Assert.Equal(1, HoursAt(r, "cleaning", 9 * H, 10 * H));
        Assert.Equal(2, HoursAt(r, "cleaning", 18 * H, 21 * H));
    }

    [Fact]
    public void Keeps_the_quota_window_at_its_quota_even_when_the_group_has_spare_hours()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("cleaning", 2)),
            Windows = [Quota("cleaning", 9 * H, 11 * H, 1), S1.W("cleaning", 18 * H, 20 * H)],
        }));
        // 1h in the (bigger) quoted window, from its front; 1h in the evening.
        Assert.Equal(1, HoursAt(r, "cleaning", 9 * H, 11 * H));
        Assert.Equal(1, HoursAt(r, "cleaning", 9 * H, 10 * H));
        Assert.Equal(1, HoursAt(r, "cleaning", 18 * H, 20 * H));
    }

    [Fact]
    public void Spills_past_the_quota_rather_than_flagging_when_the_open_windows_are_full()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("cleaning", 4)),
            // Quota 1h but the only other window holds 2h - the last hour must
            // legally spill back into the quoted window.
            Windows = [Quota("cleaning", 9 * H, 12 * H, 1), S1.W("cleaning", 18 * H, 20 * H)],
        }));
        Assert.Equal(4, S1.Alloc(r, "cleaning").AllocatedHours);
        Assert.Empty(r.Flags);
    }

    // ── placement strategies ──

    private static PlacementInput Placing(JsMap<string, PlacementDirective> rules, double? peakStart = null, double? peakEnd = null) =>
        new() { Rules = rules, Curve = new(), PeakStart = peakStart, PeakEnd = peakEnd };

    private static JsMap<string, PlacementDirective> Rule(string group, PlacementDirective d)
    {
        var m = new JsMap<string, PlacementDirective>();
        m.Set(group, d);
        return m;
    }

    [Fact]
    public void Backfill_packs_the_hours_against_the_anchor()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("load_running", 2)),
            Windows = [S1.W("load_running", 6 * H, 10 * H)],
            Placement = Placing(Rule("load_running", new("backfill", 10 * H, null, null, null))),
        }));
        // 2h ending at the 10:00 anchor: 08:00-10:00, nothing at 6-8am.
        Assert.Equal(2, HoursAt(r, "load_running", 8 * H, 10 * H));
        Assert.Equal(0, HoursAt(r, "load_running", 6 * H, 8 * H));
    }

    [Fact]
    public void Forward_fill_packs_the_hours_from_the_anchor_onwards()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("reset", 2)),
            Windows = [S1.W("reset", 10 * H, 20 * H)],
            Placement = Placing(Rule("reset", new("forward_fill", 14 * H, null, null, null))),
        }));
        Assert.Equal(2, HoursAt(r, "reset", 14 * H, 16 * H));
        Assert.Equal(0, HoursAt(r, "reset", 10 * H, 14 * H));
    }

    [Fact]
    public void Peak_split_places_shares_either_side_of_the_peak_and_the_rest_at_the_anchor()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("reset", 4)),
            Windows = [S1.W("reset", 10 * H, 20 * H)],
            Placement = Placing(Rule("reset", new("peak_split", 18 * H, 25, 25, null)), 12 * H, 14 * H),
        }));
        // 1h immediately before the 12:00 peak, 1h from 14:00, 2h ending 18:00.
        Assert.Equal(1, HoursAt(r, "reset", 11 * H, 12 * H));
        Assert.Equal(1, HoursAt(r, "reset", 14 * H, 15 * H));
        Assert.Equal(2, HoursAt(r, "reset", 16 * H, 18 * H));
        Assert.Equal(0, HoursAt(r, "reset", 12 * H, 14 * H)); // never inside the peak
    }

    [Fact]
    public void Absent_directives_keep_todays_premium_aware_spread_untouched()
    {
        Stage1Result Mk(PlacementInput? p) => Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("load_running", 3)),
            Windows = [S1.W("load_running", 6 * H, 10 * H)],
            Placement = p,
        }));
        var legacy = Mk(null);
        var withEmptyRules = Mk(Placing(new()));
        Assert.Equal(
            legacy.Blocks.Select(b => (b.Group, b.Time, b.Headcount)),
            withEmptyRules.Blocks.Select(b => (b.Group, b.Time, b.Headcount)));
    }

    // ── reset task-first placement ──

    private static ResetTaskRule Task(string id, string name, double? sharePct, string startKind, double? startTime, double startOffset,
        string endKind, double? endTime, double endOffset, double sortOrder) => new()
    {
        Id = id,
        Name = name,
        SharePct = sharePct,
        FixedMinutes = null,
        WindowStartKind = startKind,
        WindowStartTime = startTime,
        WindowStartOffsetMin = startOffset,
        WindowEndKind = endKind,
        WindowEndTime = endTime,
        WindowEndOffsetMin = endOffset,
        Placement = "spread",
        SortOrder = sortOrder,
    };

    private static List<Stage1Task> AsStage1(IEnumerable<ResolvedResetTask> tasks) =>
        tasks.Select(t => new Stage1Task(t.Name, t.Minutes, t.WindowStart, t.WindowEnd, t.Placement)).ToList();

    [Fact]
    public void Places_reruns_by_their_deadlines_and_the_decard_against_close()
    {
        var ctx = new ResetTaskContext(8 * H + 30, 20 * H, 6 * H, 20 * H + 15);
        var tasks = ResetTasks.ResolveResetTasks(
            [
                Task("1", "Re-run 1", 20, "clock", 10 * H, 0, "clock", 14 * H, 0, 10),
                Task("2", "Re-run 2", 20, "clock", 14 * H, 0, "clock", 18 * H, 0, 20),
                Task("4", "Full decard", null, "close", null, -150, "close", null, 0, 40),
            ],
            5, // 5h of reset: 1h + 1h + 3h remainder
            ctx);
        Assert.Equal([("Re-run 1", 60.0), ("Re-run 2", 60.0), ("Full decard", 180.0)], tasks.Select(t => (t.Name, t.Minutes)));
        // Full decard: close 20:00 − 2.5h = 17:30 → 20:00.
        Assert.Equal(17 * H + 30, tasks[2].WindowStart);
        Assert.Equal(20 * H, tasks[2].WindowEnd);

        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("reset", 5)),
            Windows = [S1.W("reset", 10 * H, 20 * H)],
            ResetTasks = AsStage1(tasks),
        }));
        Assert.Equal(1, HoursAt(r, "reset", 10 * H, 14 * H)); // Re-run 1 by 2pm
        Assert.True(HoursAt(r, "reset", 14 * H, 18 * H) >= 1); // Re-run 2
        Assert.True(HoursAt(r, "reset", 17 * H + 30, 20 * H) >= 2.5); // decard vs close
        Assert.Equal(5, S1.Alloc(r, "reset").AllocatedHours);
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void Drops_the_remainder_task_when_sized_tasks_consume_the_whole_benchmark()
    {
        var ctx = new ResetTaskContext(9 * H, 20 * H, 6 * H, 20 * H);
        var tasks = ResetTasks.ResolveResetTasks(
            [
                Task("1", "Re-run 1", 100, "none", null, 0, "none", null, 0, 10),
                Task("2", "Full decard", null, "close", null, -150, "close", null, 0, 40),
            ],
            2,
            ctx);
        Assert.Single(tasks);
        Assert.Equal(120, tasks[0].Minutes);
    }

    // G351: the checkout borrow left Reset 0.55 h (two quarter-hours) for four
    // tasks. Each keeps a quarter-hour (decision 4.3), never "min not planned".
    private static List<Stage1Task> G351Tasks() =>
    [
        new("Re-run 1", 5, 8 * H, 11 * H, "spread"),
        new("Re-run 2", 8.25, 13 * H, 16 * H, "spread"),
        new("First declare", 8.25, 9 * H, 12 * H, "spread"),
        new("Full decard", 11.5, 17 * H, 20 * H, "spread"),
    ];

    [Fact]
    public void Gives_every_Reset_task_a_quarter_hour_and_balances_it_against_another_group()
    {
        // The owner: "if it runs slightly over this has to be balanced by another
        // labour group being slightly under". Specials was rounded up past its own
        // benchmark, so it gives the half-hour up; the quoted Cleaning hour never.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("cleaning", 1), ("specials", 1.9), ("reset", 0.55)),
            Windows = [S1.W("reset", 8 * H, 20 * H), S1.W("specials", 17 * H, 20 * H), Quota("cleaning", 9 * H, 10 * H, 1)],
            ResetTasks = G351Tasks(),
        }));
        Assert.Equal(0.25, HoursAt(r, "reset", 13 * H, 16 * H));
        Assert.Equal(0.25, HoursAt(r, "reset", 17 * H, 20 * H));
        Assert.Equal(1, S1.Alloc(r, "reset").AllocatedHours);
        Assert.Equal(1.5, S1.Alloc(r, "specials").AllocatedHours);
        Assert.Equal(1.5, HoursAt(r, "specials", 0, 24 * H));
        Assert.Equal(1, HoursAt(r, "cleaning", 9 * H, 10 * H));
        Assert.Empty(r.HoursIssues ?? []);
        Assert.Equal(["Reset is 0.50 h over so every Reset task has its time; Specials is 0.50 h under to balance it"], r.Flags);
    }

    [Fact]
    public void Goes_over_as_an_hours_issue_when_no_other_group_can_give_the_time_up()
    {
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("cleaning", 1), ("reset", 0.55)),
            Windows = [S1.W("reset", 8 * H, 20 * H), Quota("cleaning", 9 * H, 10 * H, 1)],
            ResetTasks = G351Tasks(),
        }));
        Assert.Equal(1, S1.Alloc(r, "reset").AllocatedHours);
        Assert.Equal(1, HoursAt(r, "cleaning", 9 * H, 10 * H));
        Assert.DoesNotContain(r.Flags, f => f.Contains("not planned") || f.Contains("could not be"));
        Assert.Equal([new HoursIssue("reset", 0.5)], r.HoursIssues!);
        Assert.Contains(
            "Reset hours issue: 0.50h over the Reset left after the checkout borrow - each Reset task due today keeps at least a quarter-hour, and no other group had the time to give up",
            r.Flags);
    }

    [Fact]
    public void Clamps_an_out_of_hours_window_instead_of_widening_it_to_the_whole_day()
    {
        var ctx = new ResetTaskContext(9 * H, 20 * H, 7 * H, 20 * H);
        var tasks = ResetTasks.ResolveResetTasks(
            [
                // Valid raw window, but wholly before staff open: clamps to nothing
                // (the engine flags the unplaceable minutes) - it must NOT silently
                // spread across the staffed day.
                Task("1", "Early pass", 50, "clock", 5 * H, 0, "clock", 7 * H, 0, 10),
                // Genuinely back-to-front config: the work is still due, so THIS one
                // widens to the staffed day.
                Task("2", "Backwards", 50, "clock", 18 * H, 0, "clock", 10 * H, 0, 20),
            ],
            2,
            ctx);
        Assert.Equal(7 * H, tasks[0].WindowStart);
        Assert.Equal(7 * H, tasks[0].WindowEnd); // clamped empty
        Assert.Equal(7 * H, tasks[1].WindowStart);
        Assert.Equal(20 * H, tasks[1].WindowEnd);

        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            StaffOpen = 7 * H,
            StaffClose = 20 * H,
            Benchmark90 = S1.Bench(("reset", 2)),
            Windows = [S1.W("reset", 10 * H, 20 * H)],
            ResetTasks = AsStage1(tasks),
        }));
        Assert.Contains(r.Flags, f => f.Contains("Early pass") && f.Contains("could not be placed"));
    }

    // ── findPeak ──

    [Fact]
    public void FindPeak_identifies_the_busy_span_around_the_maximum()
    {
        var curve = new List<CurveShare>();
        for (var m = 8 * H; m < 20 * H; m += 15)
        {
            // A lunchtime hump: busiest at 12:00-13:00.
            var share = m >= 12 * H && m < 13 * H ? 3 : m >= 11 * H && m < 14 * H ? 2.9 : 1;
            curve.Add(new(m, share));
        }
        var peak = Placement.FindPeak(curve, Placement.DefaultSettings with { PeakBasis = "rolling", PeakThresholdPct = 90 });
        Assert.NotNull(peak);
        Assert.True(peak!.Value.Start >= 11 * H);
        Assert.True(peak.Value.End <= 14 * H + 15);
        Assert.True(peak.Value.Start < peak.Value.End);
    }

    [Fact]
    public void FindPeak_returns_null_with_no_curve() => Assert.Null(Placement.FindPeak([], Placement.DefaultSettings));

    // ── per-task reset placement ──
    // One reset task, 1 hour of work inside a 4-hour window, over a curve whose
    // busy period is the middle of it. Where those four blocks land is the whole
    // question the setting answers - and the setting the Planning Order page
    // used to offer per GROUP, which Stage 1 never read once a catalogue existed.

    private static readonly (double Start, double End) Window = (10 * H, 14 * H);

    private static JsMap<double, double> Curve()
    {
        var curve = new JsMap<double, double>();
        // A single sharp rush 11:30-12:30, quiet either side.
        for (var m = 6 * H; m < 21 * H; m += 15) curve.Set(m, m >= 11 * H + 30 && m < 12 * H + 30 ? 0.05 : 0.005);
        return curve;
    }

    private static (double Start, double End)? Peak() =>
        Placement.FindPeak(Curve().Select(e => new CurveShare(e.Key, e.Value)).ToList(), Placement.DefaultSettings);

    private static PlacementInput CurvePlacement()
    {
        var peak = Peak();
        return new PlacementInput { Rules = new(), Curve = Curve(), PeakStart = peak?.Start, PeakEnd = peak?.End };
    }

    private static Stage1Result Run(string placement, List<CheckoutDemand>? checkout = null) =>
        Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("reset", 1)),
            Windows = [S1.W("reset", Window.Start, Window.End)],
            Checkout = checkout ?? [],
            ResetTasks = [new("Re-run", 60, Window.Start, Window.End, placement)],
            Placement = CurvePlacement(),
        }));

    private static List<double> Times(Stage1Result r) => S1.Blocks(r, "reset").Select(b => b.Time).ToList();

    [Fact]
    public void Keeps_the_same_hours_whatever_the_preference()
    {
        foreach (var p in new[] { "spread", "avoid_peak", "follow_demand", "late", "early" })
            Assert.Equal(1, HoursAt(Run(p), "reset", Window.Start, Window.End));
    }

    [Fact]
    public void Late_finishes_against_the_windows_end() => Assert.Equal(Window.End - 15, Times(Run("late")).Max());

    [Fact]
    public void Early_starts_at_the_windows_open() => Assert.Equal(Window.Start, Times(Run("early")).Min());

    [Fact]
    public void Avoid_peak_keeps_out_of_the_rush_and_follow_sits_in_it()
    {
        var peak = Peak()!.Value;
        int InPeak(List<double> t) => t.Count(m => m >= peak.Start && m < peak.End);
        Assert.Equal(0, InPeak(Times(Run("avoid_peak"))));
        Assert.True(InPeak(Times(Run("follow_demand"))) > 0);
    }

    [Fact]
    public void Spread_evenly_levels_the_day_around_the_tills()
    {
        // Roster Engine Review 07/11/2026, P8: "Spread evenly" laid the task as
        // one run at the latest minute it could, identical to "late" on every
        // G999 day. Levelling now: the registers peak 11:30-12:30, so the task
        // goes either side of them.
        var tills = new List<CheckoutDemand>();
        for (var m = 8 * H + 30; m < 20 * H; m += 15) tills.Add(new(m, m >= 11 * H + 30 && m < 12 * H + 30 ? 3 : 1));
        var spread = Times(Run("spread", tills));
        Assert.Equal(4, spread.Count);
        Assert.Empty(spread.Where(m => m >= 11 * H + 30 && m < 12 * H + 30));
        Assert.NotEqual(Times(Run("late", tills)), spread);
    }

    [Fact]
    public void Avoid_peak_stays_out_of_premium_time_when_the_window_reaches_it()
    {
        // An open-ended task's quietest minutes by the raw curve were the pre-open
        // ones - 06:00-07:45, premium time (A01-9).
        var peak = Peak()!.Value;
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("reset", 1)),
            Windows = [S1.W("reset", 6 * H, 20 * H)],
            ResetTasks = [new("Open", 60, 6 * H, 20 * H, "avoid_peak")],
            Placement = CurvePlacement(),
        }));
        foreach (var m in Times(r))
        {
            Assert.True(m >= 7 * H && m < 18 * H, $"{m / H}");
            Assert.False(m >= peak.Start && m < peak.End);
        }
    }

    [Fact]
    public void Two_tasks_sharing_a_window_and_a_preference_run_one_after_the_other()
    {
        // Both told to avoid the rush, they would otherwise both pick the same
        // quietest blocks and demand two bodies for them.
        var r = Stage1Engine.RunStage1(Base(b => b with
        {
            Benchmark90 = S1.Bench(("reset", 2)),
            Windows = [S1.W("reset", Window.Start, Window.End)],
            ResetTasks =
            [
                new("A", 60, Window.Start, Window.End, "avoid_peak"),
                new("B", 60, Window.Start, Window.End, "avoid_peak"),
            ],
            Placement = CurvePlacement(),
        }));
        var reset = r.Blocks.Where(b => b.Group == "reset").ToList();
        Assert.Equal(8, reset.Sum(b => b.Headcount)); // 2h kept
        Assert.Equal(1, reset.Max(b => b.Headcount)); // never doubled up
    }
}
