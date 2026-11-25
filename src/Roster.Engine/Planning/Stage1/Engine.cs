// Port of src/lib/stage1/engine.ts.
// ─── Stage 1: Labour-Group Task Planning Engine (spec Section 7) ─────────────
// Converts a day's demand into 15-minute headcount-over-time blocks across the
// window-based labour groups (Load Running, Cleaning, Specials, Reset, Admin),
// placed within their national time windows ∩ the store's STAFFED window
// (pre-open prep .. post-close wrap, not just trading hours), and combines them
// with the demand-driven Checkout blocks from the Till engine.
//
// Placement objective (spec Section 7): maximise contiguity first (fewer, longer
// continuous runs), minimise cost second. The only cost lever here is the
// pre-07:00 pay premium (§3/§4): normal-cost slots fill first, pre-07:00 slots
// are a last resort. Overlap between groups is the expected normal case, so
// groups are placed independently - the priority order matters at Stage 2 (who
// covers what), not here.

using Roster.Engine.Core;

namespace Roster.Engine.Planning.Stage1;

/// <summary>The five window-placed labour groups (WindowGroup).</summary>
public static class WindowGroups
{
    public static readonly IReadOnlyList<string> All = ["load_running", "cleaning", "specials", "reset", "admin"];

    /// <summary>
    /// The order the day is laid down in, least free work first.
    ///
    /// Admin leads because its jobs are the most tightly bound - a date check due
    /// before 08:30 has one place it can go - then Load Running inside the window
    /// the start rule set for the day, then Reset by its per-task shares, and the
    /// groups with a whole evening to play with last. Each group fills AROUND what
    /// the groups before it already claimed, which is the point of the order and
    /// the thing that never actually happened before: every group used to place
    /// into an empty canvas of its own, so five jobs could land on the same
    /// fifteen minutes and the "Planning Order" screen described a sequence the
    /// engine did not run.
    ///
    /// Configurable on Process → Planning Order; Checkout is not in the list
    /// because it is not placed at all - the till engine pins it block by block
    /// and the shifts are cut around it.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultStepOrder = ["admin", "load_running", "reset", "specials", "cleaning"];
}

/// <summary>
/// One group's Planning Order rule, pre-resolved by the caller (effective
/// strategy chosen, anchor in concrete minutes) so the engine stays pure.
/// Strategy: spread, follow_demand, backfill, forward_fill, peak_split,
/// avoid_peak - and "level" and "avoid_busy", a reset TASK's own settings
/// ("Spread evenly", "Avoid the busy period").
/// </summary>
public sealed record PlacementDirective(string Strategy, double Anchor, double? PrePeakPct, double? PostPeakPct, double? AvoidStrength);

public sealed record PlacementInput
{
    public JsMap<string, PlacementDirective> Rules { get; init; } = new();
    // Quarter-hour trade share by block start; may be empty when no curve is
    // uploaded (curve-dependent strategies degrade to their anchor behaviour).
    public JsMap<double, double> Curve { get; init; } = new();
    public double? PeakStart { get; init; } // pre-computed busy span (Placement.FindPeak)
    public double? PeakEnd { get; init; }
}

/// <summary>A named admin or reset task, resolved to concrete minutes and window for the day.</summary>
public sealed record Stage1Task(string Name, double Minutes, double WindowStart, double WindowEnd, string? Placement = null);

public sealed record AdminDepth(double OneDeep, double Handover);

public sealed record CheckoutDemand(double Time, double Headcount);

public sealed record Stage1Input
{
    public double TradingOpen { get; init; }
    public double TradingClose { get; init; }
    // How many managers Stage 1 plans on Admin at once, and inside the
    // handover (owner decision 7; Rules page settings, R15). 1 and 2 if absent.
    public AdminDepth? AdminDepth { get; init; }
    // The full staffed window (pre-open prep .. post-close wrap). Window-based
    // groups place inside this, not just trading hours. Defaults to trading hours
    // when omitted (keeps pure-unit callers simple).
    public double? StaffOpen { get; init; }
    public double? StaffClose { get; init; }
    public JsMap<string, double> Benchmark90 { get; init; } = new(); // planning hours per window group
    public List<WindowRule> Windows { get; init; } = new();
    // Checkout headcount per 15-min block start (minutes) from the Till engine.
    public List<CheckoutDemand> Checkout { get; init; } = new();
    // Employee-specific non-operational hours for the day (training/meetings).
    // These are ADDITIVE to the hours the plan must cover (spec: 35 benchmark +
    // 5 non-op ⇒ 40 to roster), not a reallocation of task time.
    public double? NonOpHours { get; init; }
    // PH reallocation (spec §4.6): Load Running moved IN from the NEXT day's public
    // holiday, placed in this (prior) evening after 18:00 to dodge the PH rate.
    public double? EveningLoadRunningHours { get; init; }
    // Named admin tasks (from the catalogue) pre-resolved to concrete minutes
    // and windows for this day. When present, Admin places TASK-FIRST: each
    // task's blocks land inside its own window, and only the remaining benchmark
    // admin (if any) falls back to the national admin windows. The benchmark
    // AdminMGmt hours already fund the tasks, so totals are unchanged unless the
    // tasks outgrow the benchmark - which is flagged.
    public List<Stage1Task>? AdminTasks { get; init; }
    // Named reset tasks (re-runs, first declare, full decard), same task-first
    // model as AdminTasks, funded out of the benchmark Reset hours.
    public List<Stage1Task>? ResetTasks { get; init; }
    // Planning Order directives (only supplied when the master switch is on).
    public PlacementInput? Placement { get; init; }
    /// <summary>
    /// The order the window groups are laid on the canvas, least free work first.
    /// Omitted = DefaultStepOrder. Any group left out of a supplied order is
    /// appended, so a stale configuration can never silently drop a group's hours.
    /// </summary>
    public List<string>? StepOrder { get; init; }
}

/// <summary>A group's headcount in one quarter-hour. Mutable: the Reset balance takes heads off in place.</summary>
public sealed class PlannedBlock
{
    public string Group { get; set; } = ""; // a window group, "checkout" or "non_op"
    public double Time { get; set; } // minutes
    public double Headcount { get; set; }

    public PlannedBlock() { }

    public PlannedBlock(string group, double time, double headcount)
    {
        Group = group;
        Time = time;
        Headcount = headcount;
    }

    public PlannedBlock Clone() => new(Group, Time, Headcount);
}

public sealed class GroupAllocation
{
    public string Group { get; set; } = "";
    public double BenchmarkHours { get; set; }
    public double AllocatedHours { get; set; }

    public GroupAllocation() { }

    public GroupAllocation(string group, double benchmarkHours, double allocatedHours)
    {
        Group = group;
        BenchmarkHours = benchmarkHours;
        AllocatedHours = allocatedHours;
    }
}

public sealed record HoursIssue(string Group, double Hours);

/// <summary>
/// Which job a planned quarter-hour belongs to: the window of that job's
/// task(s), or the whole staffed day for generic Admin time. The painter makes
/// displaced Admin up only inside its own job's window (owner decision 11;
/// review RC-9: it used to accept any due task's window). Named is false for
/// the generic time: Admin hours past the tasks, tied to no task, which may be
/// done anywhere in the staffed day. Reset's planned quarter-hours are recorded
/// too, with the names of the task(s) each belongs to (Names), for the sub-task
/// labels on the Plan page (Re-runs or Decarding). Only Admin's records are read
/// for planning.
/// </summary>
public sealed record UnitWindow(string Group, double Time, double Start, double End, bool Named, IReadOnlyList<string>? Names = null);

public class Stage1Result
{
    public List<PlannedBlock> Blocks { get; set; } = new();
    public List<GroupAllocation> Allocations { get; set; } = new();
    // Day totals (excludes Non-Op which mirrors itself, spec Section 7).
    public double TotalBenchmark { get; set; }
    public double TotalAllocated { get; set; }
    public double Variance { get; set; } // allocated - benchmark (+ = over-allocated, − = short)
    public bool WithinTolerance { get; set; } // |variance - hours issues| ≤ 1 hour
    // Hours planned over a group's benchmark ON PURPOSE (owner decisions 1 and
    // R2): today the Admin tasks a benchmark is too small for. A warning, not a
    // rule broken, so it is left out of the ±1 hour check.
    public List<HoursIssue>? HoursIssues { get; set; }
    public List<UnitWindow>? UnitWindows { get; set; }
    // Planning conflicts the user must resolve (e.g. a group's hours don't fit
    // inside its allowed window ∩ the staffed day). Windows are never violated.
    public List<string> Flags { get; set; } = new();
}

public static class Stage1Engine
{
    // Hourly roles carry pay premiums before 07:00 and from 18:00, so Stage 1
    // treats those slots as a last resort - work fills normal-rate daytime blocks
    // first and only dips into early-morning / evening premium time when a group's
    // window has no remaining normal-cost capacity (cost-effective time-of-day).
    public const double PremiumBeforeMin = 7 * 60;
    public const double PremiumFromMin = 18 * 60;
    private const double PremiumRankOffset = 100000; // pushes premium slots after every normal one

    // PH-reallocated Load Running lands in the prior evening, no earlier than 18:00.
    public const double PhEveningAfterMin = 18 * 60;

    private const double B = Grid.BlockMin;

    /// <summary>Two load maps read as one, without disturbing either.</summary>
    private static JsMap<double, double> MergeLoad(JsMap<double, double> a, JsMap<double, double> b)
    {
        var out_ = b.Copy();
        foreach (var (m, n) in a) out_.Set(m, out_.GetOr(m, 0) + n);
        return out_;
    }

    // A named task's placement preference → the directive PlaceGroup understands.
    // "spread" returns null so the task takes the premium-aware default fill,
    // which is what every task did before the setting existed - a store that never
    // touches this sees no change. avoid_peak goes in at full strength: a task
    // told to keep out of the rush means it, and a half-hearted version is just
    // the even spread with extra steps.
    private static PlacementDirective? TaskDirective(double windowStart, double windowEnd, string? placement, double staffOpen, double staffClose, string group)
    {
        double Clamp(double m) => Math.Min(Math.Max(m, staffOpen), staffClose);
        switch (placement)
        {
            case "avoid_peak":
                // The store's busy period, then premium time, then the curve - see
                // BusyAwareOrder (Roster Engine Review 07/11/2026, P8).
                return new PlacementDirective("avoid_busy", Clamp(windowEnd), null, null, 100);
            case "follow_demand":
                return new PlacementDirective("follow_demand", Clamp(windowEnd), null, null, null);
            case "late":
                // backfill fills the blocks BEFORE its anchor, latest first, so the
                // anchor is the window's end rather than its last block.
                return new PlacementDirective("backfill", Clamp(windowEnd), null, null, null);
            case "early":
                return new PlacementDirective("forward_fill", Clamp(windowStart), null, null, null);
            default:
                // "Spread evenly" said "level the task's blocks across its whole
                // window" and did something else: the default fill lays a part-layer as
                // one run at the latest non-premium minute, so on every G999 day it was
                // identical to "As late as the window allows" - the re-runs hard up
                // against 14:00. A reset task now levels the day, as it says. Admin
                // tasks keep their one contiguous run: a manager does the date checks
                // in one go.
                return group == "reset" ? new PlacementDirective("level", Clamp(windowEnd), null, null, null) : null;
        }
    }

    // "Spread evenly" for a reset task: level the day. Each block goes to the
    // slot of the window with the least work on it - everything already on the
    // canvas, the registers, earlier groups and sibling tasks alike - then a
    // normal-cost minute over a premium one, then the one nearest the window's
    // middle. Levelling against the tills keeps the task out of the busy period
    // on its own.
    private static double FillLevel(double blocksNeeded, JsSet<double> slotSet, JsMap<double, double> headcount, JsMap<double, double>? load)
    {
        if (!double.IsFinite(blocksNeeded)) return blocksNeeded;
        var slots = Js.Sorted(slotSet, (a, b) => a - b);
        if (blocksNeeded <= 0 || slots.Count == 0) return blocksNeeded;
        var mid = (slots[0] + slots[^1]) / 2;
        double At(double m) => (load?.GetOr(m, 0) ?? 0) + headcount.GetOr(m, 0);
        double Premium(double m) => SlotRank(m) >= PremiumRankOffset ? 1 : 0;
        for (var n = blocksNeeded; n > 0; n--)
        {
            var best = slots[0];
            foreach (var m in slots)
            {
                var d = (At(m) - At(best)).Or(Premium(m) - Premium(best)).Or(Math.Abs(m - mid) - Math.Abs(best - mid));
                if (d < 0) best = m;
            }
            headcount.Set(best, headcount.GetOr(best, 0) + 1);
        }
        return 0;
    }

    // "Avoid the busy period" for a reset task: outside the store's busy period
    // first (the one defined on Process -> Planning Order), then normal-cost
    // minutes before premium ones, then the quietest by the trade curve. It read
    // the raw curve alone, so an open window's quietest minutes were the pre-open
    // ones - 06:00-07:45, in premium time - and it ignored the busy-period
    // definition the store had set.
    private static List<double> BusyAwareOrder(JsSet<double> slotSet, PlacementInput? placement)
    {
        var curve = placement?.Curve ?? new JsMap<double, double>();
        double Busy(double m) =>
            placement?.PeakStart is not null && placement.PeakEnd is not null && m >= placement.PeakStart && m < placement.PeakEnd ? 1 : 0;
        double Premium(double m) => SlotRank(m) >= PremiumRankOffset ? 1 : 0;
        return Js.Sorted(slotSet, (a, b) =>
            (Busy(a) - Busy(b)).Or(Premium(a) - Premium(b)).Or(curve.GetOr(a, 0) - curve.GetOr(b, 0)).Or(a - b));
    }

    // Admin is planned one manager deep, all day (owner decision 7, 12/11/2026):
    // "Admin only where a manager is on - one at a time". A store rosters one
    // opening and one closing manager, and the two overlap only for the handover,
    // which the shift cutter places after this step - so two on Admin "during
    // trading" landed where one manager was on, on 56 of 264 sample days, and that
    // half went on nobody's shift. One deep always has a manager there; in the
    // handover the second manager is free for the floor or to take over Admin
    // while the first is on a break.
    private const double MaxConcurrentAdmin = 1;
    // The latest an opening manager can stay: a 9.5-hour shift from the staffed
    // open. A second layer of Admin is only planned where the handover can reach.
    private const double OpenerReachMin = 9.5 * 60;

    // How deep Specials and Cleaning may stack in one quarter-hour (review RC-4).
    // Their evening windows hang off the close; when a window is too narrow for
    // the day's hours, Stage 1 used to stack them all into what was left - the
    // whole day's Specials 16 to 31 people deep in the one quarter-hour a 17:00
    // close left - and the shift cutter then opened a 3-hour shift for each body.
    // Hours beyond this depth are not placed; they are flagged, naming the window,
    // so a window too short for its work shows as an error instead of a flood of
    // blank shifts. Set above the deepest an ordinary sample day uses.
    public static readonly IReadOnlyDictionary<string, double> MaxBulkDepth = new Dictionary<string, double> { ["specials"] = 6, ["cleaning"] = 6 };

    // Trim every quarter-hour of a group's placement to `cap`, re-homing the
    // excess in the window's shallowest quarter-hours that still have room
    // (normal-cost before premium). Returns the blocks that fit nowhere.
    private static double CapDepth(JsMap<double, double> placed, JsSet<double> slotSet, double cap)
    {
        double excess = 0;
        foreach (var (m, h) in placed)
        {
            if (h > cap)
            {
                excess += h - cap;
                placed.Set(m, cap);
            }
        }
        while (excess > 0)
        {
            double? best = null;
            foreach (var m in slotSet)
            {
                var h = placed.GetOr(m, 0);
                if (h >= cap) continue;
                var b = best is null ? double.PositiveInfinity : placed.GetOr(best.Value, 0);
                if (best is null || h < b || (h == b && SlotRank(m) < SlotRank(best.Value))) best = m;
            }
            if (best is null) break;
            placed.Set(best.Value, placed.GetOr(best.Value, 0) + 1);
            excess--;
        }
        return excess;
    }

    // Rank a slot for fill order: time order (contiguity-first), but every premium
    // slot (pre-07:00 or 18:00+) sorts after every normal-cost slot so premium
    // capacity is used only when nothing else remains (cost-second objective).
    private static double SlotRank(double minute) =>
        minute < PremiumBeforeMin || minute >= PremiumFromMin ? minute + PremiumRankOffset : minute;

    // Usable slots of a set of windows (each window ∩ staffed day), de-duplicated
    // so overlapping windows don't double a slot.
    private static JsSet<double> SlotsFor(IEnumerable<WindowRule> windows, double staffOpen, double staffClose)
    {
        var slotSet = new JsSet<double>();
        foreach (var w in windows)
        {
            var start = Math.Max(w.Start, staffOpen);
            var end = Math.Min(w.End, staffClose);
            for (var m = start; m < end; m += B) slotSet.Add(m);
        }
        return slotSet;
    }

    // The DEFAULT fill (premium-aware even spread): full layers over every slot,
    // then the final partial layer as ONE CONTIGUOUS run, choosing the run with
    // the least premium-time cost first, and on equal cost the LATEST placement -
    // so the overflow abuts the rest of the day's demand (or the close-down) and
    // downstream shift-cutting can flow it into longer shifts instead of
    // stranding a short orphan run at the window's opening edge. (13h of Load
    // Running in a 6:00-10:00 window puts its 4th layer at 9:00-10:00, feeding
    // the 10:00+ work; a partial cleaning layer reaches the staffed close so the
    // closers leave together.) Returns how many blocks could NOT be placed.
    //
    // `load` is what SIBLING work has already claimed in each slot. Named tasks
    // in one group are windowed independently, so several can be live at the
    // same minute; placing each into a fresh map stacked them all onto the same
    // rank-best blocks. Slots a sibling already holds therefore sort LAST, and
    // two tasks whose windows overlap run one after the other instead of
    // demanding two bodies for the same 15 minutes.
    private static double FillDefault(double blocksNeeded, JsSet<double> slotSet, JsMap<double, double> headcount, JsMap<double, double>? load)
    {
        // Non-finite counts (a corrupt benchmark) must fail loudly as a shortfall,
        // never spin the layer arithmetic forever.
        if (!double.IsFinite(blocksNeeded)) return blocksNeeded;
        if (blocksNeeded <= 0 || slotSet.Size == 0) return blocksNeeded;
        double BusyAt(double m) => load?.GetOr(m, 0) ?? 0;
        var slots = Js.Sorted(slotSet, (a, b) => (BusyAt(a) - BusyAt(b)).Or(SlotRank(a) - SlotRank(b)));

        // Whole layers by arithmetic, not repeated passes.
        var layers = Math.Floor(blocksNeeded / slots.Count);
        if (layers > 0)
            foreach (var m in slots) headcount.Set(m, headcount.GetOr(m, 0) + layers);
        var remaining = blocksNeeded - layers * slots.Count;

        if (remaining > 0)
        {
            var sorted = Js.Sorted(slotSet, (a, b) => a - b);
            List<double>? best = null;
            var bestBusy = double.PositiveInfinity;
            var bestPrem = double.PositiveInfinity;
            var bestStart = double.NegativeInfinity;
            var rem = (int)remaining;
            for (var i = 0; i + remaining <= sorted.Count; i++)
            {
                var contiguous = true;
                for (var j = 1; j < remaining; j++)
                {
                    if (sorted[i + j] != sorted[i + j - 1] + B)
                    {
                        contiguous = false;
                        break;
                    }
                }
                if (!contiguous) continue;
                var run = sorted.GetRange(i, Math.Min(rem, sorted.Count - i));
                // A run is scored on how much sibling work it collides with FIRST, so
                // an unwindowed task takes free time rather than doubling up on the
                // block a windowed sibling already needs; then on premium minutes, then
                // latest-start as before. Without the first term every open-window task
                // chose the same run - the latest non-premium one - and the day grew a
                // 17:00-17:45 tower of admin no store could ever staff.
                double busy = 0;
                foreach (var m in run) busy += BusyAt(m);
                double prem = run.Count(m => SlotRank(m) >= PremiumRankOffset);
                if (busy < bestBusy || (busy == bestBusy && (prem < bestPrem || (prem == bestPrem && run[0] > bestStart))))
                {
                    best = run;
                    bestBusy = busy;
                    bestPrem = prem;
                    bestStart = run[0];
                }
            }
            if (best is not null)
            {
                foreach (var m in best) headcount.Set(m, headcount.GetOr(m, 0) + 1);
                remaining = 0;
            }
            else
            {
                foreach (var m in slots)
                {
                    if (remaining <= 0) break;
                    headcount.Set(m, headcount.GetOr(m, 0) + 1);
                    remaining--;
                }
            }
        }
        return remaining;
    }

    // Layered fill in a FIXED slot order: each pass adds one body per slot until
    // the count is spent, so depth grows evenly and the final partial pass hugs
    // the front of the ordering. Returns how many blocks could NOT be placed.
    //
    // `load`: sibling work already placed for this group - see FillDefault. Slots
    // a sibling holds drop to a later tier, and the strategy's own order is kept
    // WITHIN each tier (the sort is stable). Two reset tasks sharing a window and
    // a strategy therefore run one after the other instead of both landing on the
    // quietest block and demanding two bodies for it.
    private static double FillOrdered(double blocksNeeded, List<double> ordered, JsMap<double, double> headcount, JsMap<double, double>? load = null)
    {
        if (!double.IsFinite(blocksNeeded)) return blocksNeeded;
        if (blocksNeeded <= 0 || ordered.Count == 0) return blocksNeeded;
        double BusyAt(double m) => load?.GetOr(m, 0) ?? 0;
        var seq = load is not null ? Js.Sorted(ordered, (a, b) => BusyAt(a) - BusyAt(b)) : ordered;
        // Whole layers by arithmetic; only the final partial pass walks the order.
        var layers = Math.Floor(blocksNeeded / seq.Count);
        if (layers > 0)
            foreach (var m in seq) headcount.Set(m, headcount.GetOr(m, 0) + layers);
        var remaining = blocksNeeded - layers * seq.Count;
        foreach (var m in seq)
        {
            if (remaining <= 0) break;
            headcount.Set(m, headcount.GetOr(m, 0) + 1);
            remaining--;
        }
        return 0;
    }

    // Slot ordering for a Planning Order strategy. Strategies deliberately IGNORE
    // the premium ranking: backfilling cleaning from close pays the evening
    // premium because that is the configured intent, stated on the admin page.
    private static List<double> OrderedForStrategy(JsSet<double> slotSet, PlacementDirective d, JsMap<double, double> curve)
    {
        var asc = Js.Sorted(slotSet, (a, b) => a - b);
        switch (d.Strategy)
        {
            case "backfill":
            {
                var pre = asc.Where(m => m < d.Anchor).Reverse();
                var post = asc.Where(m => m >= d.Anchor);
                return [.. pre, .. post];
            }
            case "forward_fill":
            {
                var post = asc.Where(m => m >= d.Anchor);
                var pre = asc.Where(m => m < d.Anchor).Reverse();
                return [.. post, .. pre];
            }
            case "follow_demand":
                // Busiest blocks first; a missing curve degrades to plain time order.
                return Js.Sorted(asc, (a, b) => (curve.GetOr(b, 0) - curve.GetOr(a, 0)).Or(a - b));
            case "avoid_peak":
            {
                // Quietest blocks first, BLENDED with time order by strength: scaling a
                // comparator never changes its sign, so the old form made every
                // strength above zero behave identically. Rank each slot in both
                // orderings and mix the ranks - strength 0 is pure time order, 100 is
                // pure quietness.
                var w = Math.Min(100, Math.Max(0, d.AvoidStrength ?? 0)) / 100;
                var byShare = Js.Sorted(asc, (a, b) => (curve.GetOr(a, 0) - curve.GetOr(b, 0)).Or(a - b));
                var shareRank = new JsMap<double, double>();
                for (var i = 0; i < byShare.Count; i++) shareRank.Set(byShare[i], i);
                var timeRank = new JsMap<double, double>();
                for (var i = 0; i < asc.Count; i++) timeRank.Set(asc[i], i);
                return Js.Sorted(asc, (a, b) =>
                    (w * (shareRank[a] - shareRank[b]) + (1 - w) * (timeRank[a] - timeRank[b])).Or(a - b));
            }
            default:
                return asc;
        }
    }

    // Place one window group's required block-count into its allowed slots.
    //
    // Three phases:
    //   A. QUOTED windows (window_hours set) fill first, in start order, each up
    //      to its quota - this is what pins "1 hour of cleaning at 09:00-10:00".
    //   B. The remainder goes to the unquoted windows: by the group's Planning
    //      Order strategy when one is supplied, else the premium-aware default.
    //   C. Anything that still doesn't fit spills back into quoted windows'
    //      remaining legal capacity - a quota steers placement, the WINDOW is the
    //      hard barrier, and a flagged shortfall helps nobody when legal time
    //      remains.
    //
    // `load`: blocks already claimed by earlier NAMED TASKS of the same group -
    // see FillDefault. Only the task-first caller supplies it.
    private static JsMap<double, double> PlaceGroup(
        double blocksNeeded,
        List<WindowRule> windows,
        double staffOpen,
        double staffClose,
        PlacementDirective? directive = null,
        PlacementInput? placement = null,
        JsMap<double, double>? load = null)
    {
        var headcount = new JsMap<double, double>();
        if (blocksNeeded <= 0) return headcount;

        var quoted = Js.Sorted(windows.Where(w => (w.Hours ?? 0) > 0), (a, b) => a.Start - b.Start);
        var unquoted = windows.Where(w => !((w.Hours ?? 0) > 0)).ToList();
        var remaining = blocksNeeded;

        // Phase A: quoted windows, oldest-first inside each (a 1h quota in a 1h
        // window is exact; a bigger window takes its quota from the front). The
        // budget is capped at ONE LAYER of the window's real slots: a 2h quota on a
        // clamped 30-minute window must spill to the open windows, not stack four
        // bodies into two blocks.
        foreach (var w in quoted)
        {
            if (remaining <= 0) break;
            var slots = Js.Sorted(SlotsFor([w], staffOpen, staffClose), (a, b) => a - b);
            if (slots.Count == 0) continue;
            var budget = Math.Min(Math.Min(Js.Round((w.Hours ?? 0) * (60 / B)), remaining), slots.Count);
            remaining = FillOrdered(budget, slots, headcount) + (remaining - budget);
        }

        // Phase B: the remainder in the unquoted windows.
        var openSlots = SlotsFor(unquoted.Count > 0 ? unquoted : windows, staffOpen, staffClose);
        if (remaining > 0 && openSlots.Size > 0)
        {
            if (directive is not null && directive.Strategy == "peak_split")
            {
                // Shares before/after the identified busy span; the rest works
                // backwards from the anchor. No identified peak = all to the anchor.
                var pk = placement;
                var asc = Js.Sorted(openSlots, (a, b) => a - b);
                if (pk?.PeakStart is not null && pk.PeakEnd is not null)
                {
                    // Shares are priced off what PHASE B actually has to place, not the
                    // group's full count - otherwise a quota that absorbed half the group
                    // silently turns a symmetric 40/40 split into 4:1.
                    var @base = remaining;
                    var pre = Math.Min(remaining, Js.Round((directive.PrePeakPct ?? 0) / 100 * @base));
                    var preOrder = asc.Where(m => m < pk.PeakStart.Value).Reverse().ToList();
                    remaining -= pre - FillOrdered(pre, preOrder, headcount);
                    var post = Math.Min(remaining, Js.Round((directive.PostPeakPct ?? 0) / 100 * @base));
                    var postOrder = asc.Where(m => m >= pk.PeakEnd.Value).ToList();
                    remaining -= post - FillOrdered(post, postOrder, headcount);
                }
                remaining = FillOrdered(
                    remaining,
                    OrderedForStrategy(openSlots, directive with { Strategy = "backfill" }, pk?.Curve ?? new JsMap<double, double>()),
                    headcount,
                    load);
            }
            else if (directive?.Strategy == "level")
            {
                remaining = FillLevel(remaining, openSlots, headcount, load);
            }
            else if (directive?.Strategy == "avoid_busy")
            {
                remaining = FillOrdered(remaining, BusyAwareOrder(openSlots, placement), headcount, load);
            }
            else if (
                directive is not null &&
                directive.Strategy != "spread" &&
                // A curve-dependent strategy with no uploaded curve would degrade to a
                // plain front-fill - which walks straight into pre-07:00 premium time.
                // The premium-aware default is the documented degradation instead.
                !((directive.Strategy == "follow_demand" || directive.Strategy == "avoid_peak") && (placement?.Curve.Size ?? 0) == 0))
            {
                remaining = FillOrdered(remaining, OrderedForStrategy(openSlots, directive, placement?.Curve ?? new JsMap<double, double>()), headcount, load);
            }
            else
            {
                remaining = FillDefault(remaining, openSlots, headcount, load);
            }
        }

        // Phase C: spill into whatever legal capacity the quoted windows still have.
        if (remaining > 0 && quoted.Count > 0) FillDefault(remaining, SlotsFor(windows, staffOpen, staffClose), headcount, load);
        return headcount;
    }

    // The groups that may give time up to balance Reset (decision 4.3): never the
    // tills, Admin or the quoted Cleaning hour.
    private static readonly string[] BalanceDonors = ["load_running", "specials", "cleaning"];

    private static readonly IReadOnlyDictionary<string, string> BalanceLabel = new Dictionary<string, string>
    {
        ["load_running"] = "Load Running",
        ["specials"] = "Specials",
        ["cleaning"] = "Cleaning",
    };

    private sealed class Job
    {
        public List<string>? Names;
        public double? Minutes;
        public double Start;
        public double End;
        public JsMap<double, double> Placed = new();
    }

    private sealed class TaskJob
    {
        public List<string> Names = new();
        public double Minutes;
        public double WindowStart;
        public double WindowEnd;
        public string? Placement;
    }

    public static Stage1Result RunStage1(Stage1Input input)
    {
        var blocks = new List<PlannedBlock>();
        var unitWindows = new List<UnitWindow>();
        var allocations = new List<GroupAllocation>();
        var flags = new List<string>();
        var hoursIssues = new List<HoursIssue>();
        // Quarter-hours the Reset tasks took past the Reset benchmark (decision 4.3).
        double resetOver = 0;
        // Window-based groups place across the full staffed window (pre-open .. post-
        // close); defaults to trading hours when the staffed bounds aren't supplied.
        var staffOpen = input.StaffOpen ?? input.TradingOpen;
        var staffClose = input.StaffClose ?? input.TradingClose;

        // Window-based groups, in the configured order. `dayLoad` is what every
        // earlier group has already put on the canvas: each group fills around it
        // rather than on top of it, so the order is a real decision about who gets
        // the best of the day.
        var dayLoad = new JsMap<double, double>();
        // The registers the till engine already needs, block by block - the
        // biggest thing on any canvas, and what makes the busy period busy.
        var tillLoad = new JsMap<double, double>();
        foreach (var c in input.Checkout)
            if (c.Headcount > 0) tillLoad.Set(c.Time, tillLoad.GetOr(c.Time, 0) + c.Headcount);
        var stepOrder = input.StepOrder ?? (IReadOnlyList<string>)WindowGroups.DefaultStepOrder;
        var order = stepOrder.Where(g => WindowGroups.All.Contains(g)).Concat(WindowGroups.All.Where(g => !stepOrder.Contains(g))).ToList();
        foreach (var group in order)
        {
            var hours = input.Benchmark90.GetOr(group, 0);
            var blocksNeeded = Js.Round(hours * (60 / B)); // hours → 15-min blocks
            var groupWindows = input.Windows.Where(w => w.Group == group && !w.MakeUpOnly).ToList();
            var directive = input.Placement?.Rules.Get(group);
            // Admin and Reset both place TASK-FIRST when their catalogue supplies
            // named tasks for the day: each task claims a contiguous run inside ITS
            // OWN window (tightest windows first, so tasks with no slack aren't
            // crowded out), then whatever benchmark remains is generic group time
            // placed in the national windows as before.
            var namedTasks = group == "admin" ? input.AdminTasks : group == "reset" ? input.ResetTasks : null;
            var taskMode = (namedTasks?.Count ?? 0) > 0;
            JsMap<double, double> placed;
            double depthCapped = 0; // blocks the depth limit kept out (MaxBulkDepth)
            // Every placement of this group, by the job it serves: each named job
            // (one or more tasks sharing a window), and the generic group time.
            var jobs = new List<Job>();
            if (taskMode)
            {
                placed = new JsMap<double, double>();
                // Short tasks that share a window share quarter-hours (owner decision
                // 3). Every task used to claim at least one 15-minute block of its own,
                // so the close-down's three small jobs - 6, 17 and 30 minutes - asked
                // for an hour between them. Tasks with the same window, placed the same
                // way, are one job of their combined minutes: the same work in fewer
                // quarter-hours.
                var byWindow = new JsMap<string, TaskJob>();
                foreach (var t in namedTasks!)
                {
                    if (t.Minutes <= 0) continue;
                    var key = $"{Js.NumberToString(t.WindowStart)}|{Js.NumberToString(t.WindowEnd)}|{t.Placement ?? ""}";
                    if (byWindow.TryGet(key, out var j))
                    {
                        j.Names.Add(t.Name);
                        j.Minutes += t.Minutes;
                    }
                    else
                    {
                        byWindow.Set(key, new TaskJob { Names = [t.Name], Minutes = t.Minutes, WindowStart = t.WindowStart, WindowEnd = t.WindowEnd, Placement = t.Placement });
                    }
                }
                // Tightest window first, so jobs with no slack aren't crowded out.
                var taskJobs = Js.Sorted(byWindow.Values(), (a, b) => a.WindowEnd - a.WindowStart - (b.WindowEnd - b.WindowStart));
                // Jobs round to whole blocks with a running carry, not a per-job
                // ceiling, so the day's task blocks track the true minute total; every
                // job still claims at least one block - a 5-minute job needs a body
                // present. The carry never pushes a job past its ceiling: minutes left
                // over from the morning made 150 minutes of Fresh Ordering eleven
                // blocks, one more than its window holds. And it never shortens Monday's
                // date checks, the one job held to its full minutes by a deadline (A12,
                // 08:30): they inherited -14 from the small jobs before them and got
                // 105 of their 120 (review RC-36). Everywhere else the carry stays: each
                // small job rounds up to a whole block, and the carry is what packs
                // those back down - sizing every job alone asked the managers for more
                // Admin than they can work (Stage 1 errors on 38 -> 88 of the sample's
                // 264 days; with every job floored at its own rounding, 102).
                //
                // Every job is planned, Reset as well as Admin. A Reset job keeps at
                // least a quarter-hour: on the smallest store the checkout borrow left
                // Reset about half an hour, which four Reset tasks cannot share, and
                // "Re-run 2: 8 min not planned" was an error (master plan decision
                // 4.3). What the jobs' quarter-hours need past the Reset left after the
                // borrow is an hours issue, a warning, as for Admin. Admin jobs are ALL
                // planned, each at its own time, even past the Admin benchmark (owner
                // decision 1, 12/11/2026): every Admin task is required, and a benchmark
                // short of the tasks is an hours issue - shown as a warning further
                // down - not a task problem. Refusing them also refused tasks for time
                // Stage 1 later dropped (review RC-6, RC-11).
                // The jobs due inside the date checks' window, closing no later, are
                // held with them: the checks share those quarter-hours with whatever
                // closes first, so a small job rounded down there came out of the
                // checks' 120 minutes (105 of 120 on Mondays once the Admin windows
                // open with the load - decision 3).
                var llcJob = group == "admin" ? taskJobs.FirstOrDefault(j => j.Names.Contains("Longlife Date Checks")) : null;
                bool HeldJob(TaskJob j) =>
                    llcJob is not null &&
                    (j == llcJob || (j.WindowEnd <= llcJob.WindowEnd && j.WindowEnd > llcJob.WindowStart && j.WindowStart < llcJob.WindowEnd));
                double carryMin = 0;
                var budget = blocksNeeded;
                foreach (var j in taskJobs)
                {
                    var want = j.Minutes + carryMin;
                    var ceil = Math.Max(1, Math.Ceiling(j.Minutes / B - 1e-9));
                    var held = HeldJob(j);
                    var floor = held ? ceil : 1;
                    var n = Math.Min(ceil, Math.Max(floor, Js.Round(want / B)));
                    carryMin = want - n * B;
                    var take = n;
                    var label = $"{(group == "admin" ? "admin" : "reset")} task '{string.Join("', '", j.Names)}'";
                    budget -= take;
                    // Each job carries its own placement preference, applied INSIDE its
                    // own window - the re-run keeps out of the lunchtime rush, the decard
                    // runs as late as its window allows.
                    var tDirective = TaskDirective(j.WindowStart, j.WindowEnd, j.Placement, staffOpen, staffClose, group);
                    var tp = PlaceGroup(
                        take,
                        [new WindowRule(group, j.WindowStart, j.WindowEnd)],
                        staffOpen,
                        staffClose,
                        tDirective,
                        tDirective is not null ? input.Placement : null,
                        // Spread around the jobs already placed, don't stack on them - and a
                        // task levelling the day levels it against everything on the
                        // canvas, the registers included.
                        tDirective?.Strategy == "level" ? MergeLoad(MergeLoad(placed, dayLoad), tillLoad) : placed);
                    var mine = new JsMap<double, double>();
                    double got = 0;
                    foreach (var (m, h) in tp)
                    {
                        placed.Set(m, placed.GetOr(m, 0) + h);
                        mine.Set(m, mine.GetOr(m, 0) + h);
                        got += h;
                    }
                    jobs.Add(new Job { Names = j.Names, Minutes = j.Minutes, Start = j.WindowStart, End = j.WindowEnd, Placed = mine });
                    if (got < take)
                        flags.Add($"{label}: {Js.ToFixed((take - got) * B / 60, 2)}h could not be placed - its window falls outside the staffed day");
                }
                // Whatever the benchmark has left is generic group time, placed in the
                // group's own windows as before.
                if (budget > 0)
                {
                    var rp = PlaceGroup(budget, groupWindows, staffOpen, staffClose, directive, input.Placement, MergeLoad(placed, dayLoad));
                    var mine = new JsMap<double, double>();
                    double placedRemainder = 0;
                    foreach (var (m, h) in rp)
                    {
                        placed.Set(m, placed.GetOr(m, 0) + h);
                        mine.Set(m, mine.GetOr(m, 0) + h);
                        placedRemainder += h;
                    }
                    jobs.Add(new Job { Names = null, Start = staffOpen, End = staffClose, Placed = mine });
                    // The per-job flags above only cover NAMED tasks; the generic
                    // remainder must flag its own shortfall or hours vanish silently.
                    if (placedRemainder < budget)
                        flags.Add($"{Js.ReplaceFirst(group, "_", " ")}: {Js.ToFixed((budget - placedRemainder) * B / 60, 2)}h could not be placed - its allowed window falls outside the staffed day (adjust the window in Labour Rules or the trading hours)");
                }
            }
            else
            {
                placed = PlaceGroup(blocksNeeded, groupWindows, staffOpen, staffClose, directive, input.Placement, dayLoad);
                if (MaxBulkDepth.TryGetValue(group, out var cap)) depthCapped = CapDepth(placed, SlotsFor(groupWindows, staffOpen, staffClose), cap);
                jobs.Add(new Job { Names = null, Start = staffOpen, End = staffClose, Placed = placed.Copy() });
            }

            // ── Admin concurrency: a store has two managers, not five ─────────────
            // Admin is manager-only, and the day's shape is one opening manager and
            // one closing manager who overlap through trading. Outside trading hours
            // exactly one of them is in the building. Nothing above enforces that:
            // each named job is windowed on its own, so jobs whose windows overlap
            // all land on the same minute and ask for a headcount no store can
            // staff - G248's Monday demanded FIVE managers on admin at 17:45 and
            // FOUR after close. The shaper reads those totals as bodies to cut
            // shifts for, so a phantom tower of admin opens extra evening shifts.
            //
            // So a block over the cap gives a unit up - and that unit moves only
            // inside ITS OWN job's window, to the least-loaded minute there. It used
            // to go to the least-loaded minute of ANY admin window, which is how 45
            // minutes of after-close cash-up came to be planned at 07:00 (Roster
            // Engine Review 07/11/2026, RC5). Generic admin time has no job, so it may
            // go anywhere in admin's windows. A unit with no room left in its own
            // window is not planned, and its job is flagged by name.
            if (group == "admin")
                PlanAdminConcurrency(input, group, groupWindows, staffOpen, staffClose, placed, jobs, taskMode, flags, unitWindows);

            // Reset's jobs by name: a label for the Plan page, read by nothing that plans.
            if (group == "reset" && taskMode)
            {
                foreach (var j in jobs)
                    foreach (var (m, n) in j.Placed)
                        for (var k = 0; k < n; k++)
                            unitWindows.Add(new UnitWindow(group, m, j.Names is not null ? j.Start : staffOpen, j.Names is not null ? j.End : staffClose, j.Names is not null, j.Names));
            }

            double allocatedBlocks = 0;
            foreach (var (time, headcount) in placed)
            {
                blocks.Add(new PlannedBlock(group, time, headcount));
                dayLoad.Set(time, dayLoad.GetOr(time, 0) + headcount);
                allocatedBlocks += headcount;
            }
            var allocatedHours = allocatedBlocks * (B / 60);
            allocations.Add(new GroupAllocation(group, hours, allocatedHours));
            // Admin past its benchmark is the tasks needing more than it holds (owner
            // decision 1): an hours issue the store and EMSO should see, worded so the
            // Review grades it a warning, and left out of the ±1 hour check.
            // Every overage counts (a task's quarter-hour rounding included); the
            // warning is raised for a whole quarter-hour or more.
            if (group == "admin" && taskMode && allocatedHours - hours > 1e-9)
            {
                var over = allocatedHours - hours;
                hoursIssues.Add(new HoursIssue(group, over));
                if (over >= B / 60 - 1e-9)
                    flags.Add($"Admin hours issue: {Js.ToFixed(over, 2)}h over the benchmark ({Js.ToFixed(hours, 2)}h) - the Admin tasks due today need more than it holds");
            }
            // Reset tasks past the Reset the checkout borrow left (decision 4.3):
            // each task keeps at least a quarter-hour, and the quarter-hours that
            // takes past the group's own are balanced against another group below.
            if (group == "reset" && taskMode && allocatedBlocks > blocksNeeded) resetOver = allocatedBlocks - blocksNeeded;
            // Only a real shortfall (at least one whole block) is a conflict; sub-block
            // rounding must never surface as a "0.00h could not be placed" flag. In
            // task mode the per-task flags above already name the culprit precisely.
            var cappedHours = depthCapped * (B / 60);
            if (depthCapped > 0)
                flags.Add($"{Js.ReplaceFirst(group, "_", " ")}: {Js.ToFixed(cappedHours, 2)}h could not be placed - its window is too short for the day's hours (it would need more than {Js.NumberToString(MaxBulkDepth[group])} people at once; widen the window in Labour Rules)");
            if (!taskMode && hours - allocatedHours - cappedHours >= B / 60 - 1e-9)
                flags.Add($"{Js.ReplaceFirst(group, "_", " ")}: {Js.ToFixed(hours - allocatedHours - cappedHours, 2)}h could not be placed - its allowed window falls outside the staffed day (adjust the window in Labour Rules or the trading hours)");
        }

        // ── Reset over, balanced (owner decision 4.3, 14/11/2026) ──────────────
        // "If it runs slightly over this has to be balanced by another labour group
        // being slightly under." The same quarter-hours come off Load Running,
        // Specials or Cleaning - never the tills, an Admin task or the quoted
        // Cleaning hour (decisions 1 and 10) - so the day's total stays where it
        // was: the group Stage 1's rounding left furthest above its own benchmark
        // first, then the one with the most hours. Only when no group can give the
        // time up does the day go over, as an hours issue.
        if (resetOver > 0)
        {
            bool Quoted(string g, double t) => input.Windows.Any(w => w.Group == g && (w.Hours ?? 0) > 0 && t >= w.Start && t < w.End);
            List<PlannedBlock> Free(string g) => blocks.Where(b => b.Group == g && b.Headcount > 0 && !Quoted(g, b.Time)).ToList();
            var donors = Js.Sorted(
                BalanceDonors.Select(g =>
                {
                    var a = allocations.FirstOrDefault(x => x.Group == g);
                    return (G: g, A: a, Above: a is not null ? a.AllocatedHours - a.BenchmarkHours : 0);
                }).Where(d => d.A is not null && Free(d.G).Count > 0),
                (x, y) =>
                {
                    var ax = x.Above > 1e-9 ? x.Above : double.NegativeInfinity;
                    var ay = y.Above > 1e-9 ? y.Above : double.NegativeInfinity;
                    return (ay - ax).Or(y.A!.AllocatedHours - x.A!.AllocatedHours);
                });
            var gave = new List<(string G, double N)>();
            var left = resetOver;
            foreach (var d in donors)
            {
                double n = 0;
                while (left > 0)
                {
                    // From the quarter-hour where the group is deepest (the latest of
                    // equals), outside its quoted hour.
                    var b = Js.Sorted(Free(d.G), (x, y) => (y.Headcount - x.Headcount).Or(y.Time - x.Time)).FirstOrDefault();
                    if (b is null) break;
                    b.Headcount--;
                    n++;
                    left--;
                }
                if (n > 0)
                {
                    d.A!.AllocatedHours -= n * (B / 60);
                    gave.Add((d.G, n));
                }
                if (left == 0) break;
            }
            for (var i = blocks.Count - 1; i >= 0; i--)
                if (blocks[i].Headcount <= 0) blocks.RemoveAt(i);
            static string Hrs(double n) => Js.ToFixed(n * B / 60, 2);
            if (gave.Count > 0)
                flags.Add($"Reset is {Hrs(resetOver - left)} h over so every Reset task has its time; {string.Join(" and ", gave.Select(x => $"{BalanceLabel[x.G]} is {Hrs(x.N)} h under"))} to balance it");
            if (left > 0)
            {
                hoursIssues.Add(new HoursIssue("reset", left * (B / 60)));
                flags.Add($"Reset hours issue: {Hrs(left)}h over the Reset left after the checkout borrow - each Reset task due today keeps at least a quarter-hour, and no other group had the time to give up");
            }
        }

        // PH reallocation: Load Running moved in from the next day's public holiday
        // is placed in this evening (after 18:00) and folded into the Load Running
        // allocation, so the day's totals/variance stay consistent.
        var eveningLr = input.EveningLoadRunningHours ?? 0;
        if (eveningLr > 0 && staffClose <= PhEveningAfterMin)
        {
            // The PH day already shed these hours (keepFactor) - if this evening
            // cannot host them, they would vanish from the fortnight without a trace.
            flags.Add($"{Js.ToFixed(eveningLr, 2)}h of public-holiday Load Running could not be moved into this evening - the staffed day ends at or before 18:00");
        }
        if (eveningLr > 0 && staffClose > PhEveningAfterMin)
        {
            List<WindowRule> eveningWindow = [new WindowRule("load_running", Math.Max(PhEveningAfterMin, staffOpen), staffClose)];
            var placed = PlaceGroup(Js.Round(eveningLr * (60 / B)), eveningWindow, staffOpen, staffClose);
            double evBlocks = 0;
            foreach (var (time, headcount) in placed)
            {
                blocks.Add(new PlannedBlock("load_running", time, headcount));
                evBlocks += headcount;
            }
            var evHours = evBlocks * (B / 60);
            // Depth check: 6h of moved load in a 45-minute evening stacks bodies no
            // real shift structure can honour - say so instead of quietly planning it.
            var evSlots = Math.Max(1, Math.Floor((staffClose - Math.Max(PhEveningAfterMin, staffOpen)) / B));
            if (evBlocks / evSlots > 3)
                flags.Add($"public-holiday Load Running needs {Js.NumberToString(Math.Ceiling(evBlocks / evSlots))} people at once in this evening's {Js.ToFixed(evSlots * B / 60, 2)}h window - review the reallocation");
            var lrAlloc = allocations.FirstOrDefault(a => a.Group == "load_running");
            if (lrAlloc is not null)
            {
                lrAlloc.BenchmarkHours += evHours;
                lrAlloc.AllocatedHours += evHours;
            }
        }

        // Checkout - demand-driven, placed as-is from the Till engine.
        double checkoutBlocks = 0;
        foreach (var c in input.Checkout)
        {
            if (c.Headcount > 0)
            {
                blocks.Add(new PlannedBlock("checkout", c.Time, c.Headcount));
                checkoutBlocks += c.Headcount;
            }
        }
        var checkoutHours = checkoutBlocks * (B / 60);
        // Till-derived figure is both benchmark & allocated.
        allocations.Add(new GroupAllocation("checkout", checkoutHours, checkoutHours));

        // Non-Op - additive coverage with no fixed time-of-day. Spread it evenly
        // across trading hours as generic (flex) coverage so the plan sizes for the
        // extra bodies/hours without inflating any one peak.
        var nonOpHours = input.NonOpHours ?? 0;
        if (nonOpHours > 0)
        {
            var tradingBlocks = new List<double>();
            for (var m = input.TradingOpen; m < input.TradingClose; m += B) tradingBlocks.Add(m);
            var nonOpBlocks = Js.Round(nonOpHours * (60 / B));
            var placed = new JsMap<double, double>();
            for (var i = 0; i < nonOpBlocks && tradingBlocks.Count > 0; i++)
            {
                var m = tradingBlocks[i % tradingBlocks.Count];
                placed.Set(m, placed.GetOr(m, 0) + 1);
            }
            double nonOpPlaced = 0;
            foreach (var (time, headcount) in placed)
            {
                blocks.Add(new PlannedBlock("non_op", time, headcount));
                nonOpPlaced += headcount;
            }
            allocations.Add(new GroupAllocation("non_op", nonOpHours, nonOpPlaced * (B / 60)));
        }

        // Merge any duplicate (group, time) blocks - e.g. evening PH-reallocated Load
        // Running overlapping the morning placement - so downstream demand sums and
        // persisted planned_blocks rows stay unique.
        var mergedByKey = new JsMap<string, PlannedBlock>();
        foreach (var b in blocks)
        {
            var key = $"{b.Group}:{Js.NumberToString(b.Time)}";
            if (mergedByKey.TryGet(key, out var ex)) ex.Headcount += b.Headcount;
            else mergedByKey.Set(key, b.Clone());
        }

        double totalBenchmark = 0;
        foreach (var a in allocations) totalBenchmark += a.BenchmarkHours;
        double totalAllocated = 0;
        foreach (var a in allocations) totalAllocated += a.AllocatedHours;
        var variance = totalAllocated - totalBenchmark;
        double issues = 0;
        foreach (var h in hoursIssues) issues += h.Hours;

        return new Stage1Result
        {
            Blocks = mergedByKey.Values().ToList(),
            Allocations = allocations,
            TotalBenchmark = totalBenchmark,
            TotalAllocated = totalAllocated,
            Variance = variance,
            WithinTolerance = Math.Abs(variance - issues) <= 1 + 1e-9,
            HoursIssues = hoursIssues,
            UnitWindows = unitWindows,
            Flags = flags,
        };
    }

    private static void PlanAdminConcurrency(
        Stage1Input input,
        string group,
        List<WindowRule> groupWindows,
        double staffOpen,
        double staffClose,
        JsMap<double, double> placed,
        List<Job> jobs,
        bool taskMode,
        List<string> flags,
        List<UnitWindow> unitWindows)
    {
        var genericSlots = Js.Sorted(SlotsFor(groupWindows, staffOpen, staffClose), (a, b) => a - b);
        var dropped = new JsMap<Job, double>();
        // Quarter-hours planned two deep for the handover (below).
        var pairOk = new JsSet<double>();
        var depthOne = input.AdminDepth?.OneDeep ?? MaxConcurrentAdmin;
        var depthPair = Math.Max(depthOne, input.AdminDepth?.Handover ?? 2);
        List<double> JobSlots(Job j) => Js.Sorted(SlotsFor([new WindowRule(group, j.Start, j.End)], staffOpen, staffClose), (a, b) => a - b);
        IEnumerable<double> OwnSlots(Job j) => j.Names is not null ? SlotsFor([new WindowRule(group, j.Start, j.End)], staffOpen, staffClose) : genericSlots;
        foreach (var m in Js.Sorted(placed.Keys(), (a, b) => a - b))
        {
            while (placed.GetOr(m, 0) > (pairOk.Has(m) ? depthPair : depthOne))
            {
                // Generic time first (it can go anywhere), then the widest window.
                var holders = Js.Sorted(
                    jobs.Where(j => j.Placed.GetOr(m, 0) > 0),
                    (a, b) => ((double)(a.Names is not null ? 1 : 0) - (b.Names is not null ? 1 : 0)).Or(b.End - b.Start - (a.End - a.Start)));
                var moved = false;
                foreach (var j in holders)
                {
                    var slots = j.Names is not null ? JobSlots(j) : genericSlots;
                    double target = -1;
                    var best = double.PositiveInfinity;
                    foreach (var t in slots)
                    {
                        if (t == m) continue;
                        var n = placed.GetOr(t, 0);
                        if (n >= depthOne) continue;
                        var rank = n * 1e7 + SlotRank(t);
                        if (rank < best)
                        {
                            best = rank;
                            target = t;
                        }
                    }
                    if (target == -1) continue;
                    j.Placed.Set(m, j.Placed.GetOr(m, 0) - 1);
                    j.Placed.Set(target, j.Placed.GetOr(target, 0) + 1);
                    placed.Set(m, placed.GetOr(m, 0) - 1);
                    placed.Set(target, placed.GetOr(target, 0) + 1);
                    moved = true;
                    break;
                }
                if (moved) continue;
                // No free minute in any holder's own window: make one by sliding a
                // unit of ANOTHER job along its own window to the free minute
                // nearest it. CGOS Ordering sitting late in its 13:00-16:00 window
                // took Fresh Ordering's first half-hour while 13:00-15:00 stood
                // empty, and a Fresh Ordering block was dropped for it.
                foreach (var j in holders)
                {
                    foreach (var t in OwnSlots(j))
                    {
                        if (t == m || placed.GetOr(t, 0) < depthOne) continue;
                        foreach (var k in jobs)
                        {
                            if (k == j || k.Placed.GetOr(t, 0) <= 0) continue;
                            double free = -1;
                            foreach (var u in OwnSlots(k))
                            {
                                if (u == t || u == m || placed.GetOr(u, 0) >= depthOne) continue;
                                var d = Math.Abs(u - t);
                                var bestD = Math.Abs(free - t);
                                if (free == -1 || d < bestD || (d == bestD && SlotRank(u) < SlotRank(free))) free = u;
                            }
                            if (free == -1) continue;
                            k.Placed.Set(t, k.Placed.GetOr(t, 0) - 1);
                            k.Placed.Set(free, k.Placed.GetOr(free, 0) + 1);
                            placed.Set(free, placed.GetOr(free, 0) + 1);
                            j.Placed.Set(m, j.Placed.GetOr(m, 0) - 1);
                            j.Placed.Set(t, j.Placed.GetOr(t, 0) + 1);
                            placed.Set(m, placed.GetOr(m, 0) - 1);
                            moved = true;
                            break;
                        }
                        if (moved) break;
                    }
                    if (moved) break;
                }
                if (moved) continue;
                // Still nothing: a longer chain of slides, each unit along its own
                // job's window, ending on a free quarter-hour - the shortest such
                // chain, then the best-ranked free quarter-hour. G252's evenings
                // needed three: Fresh Ordering into CGOS Ordering's 15:30, CGOS
                // into a quarter-hour of generic time, and that to a free one; with
                // two at most, 15 minutes of Fresh Ordering went unplanned every day.
                {
                    var parent = new JsMap<double, (double From, Job Job)>();
                    parent.Set(m, (-1, holders[0]));
                    var layer = new List<double> { m };
                    var found = new List<double>();
                    while (layer.Count > 0 && found.Count == 0)
                    {
                        var next = new List<double>();
                        foreach (var s in layer)
                            foreach (var k in jobs)
                            {
                                if (k.Placed.GetOr(s, 0) <= 0) continue;
                                foreach (var u in OwnSlots(k))
                                {
                                    if (parent.Has(u)) continue;
                                    parent.Set(u, (s, k));
                                    if (placed.GetOr(u, 0) < depthOne) found.Add(u);
                                    else next.Add(u);
                                }
                            }
                        layer = next;
                    }
                    if (found.Count > 0)
                    {
                        found = Js.Sort(found, (a, b) => SlotRank(a) - SlotRank(b));
                        var u = found[0];
                        placed.Set(u, placed.GetOr(u, 0) + 1);
                        placed.Set(m, placed.GetOr(m, 0) - 1);
                        while (u != m)
                        {
                            var (from, job) = parent[u];
                            job.Placed.Set(from, job.Placed.GetOr(from, 0) - 1);
                            job.Placed.Set(u, job.Placed.GetOr(u, 0) + 1);
                            u = from;
                        }
                        continue;
                    }
                }
                var jh = holders[0];
                jh.Placed.Set(m, jh.Placed.GetOr(m, 0) - 1);
                placed.Set(m, placed.GetOr(m, 0) - 1);
                // Two on Admin only inside the handover (owner decision 7): a named
                // job with no room one deep goes in as a second layer at the first
                // quarter-hour of its window that an opening manager can still be
                // on for, and the shift cutter puts the managers' overlap over it.
                // One deep everywhere left 15 minutes of Fresh Ordering unplanned
                // on 36 of the sample's 264 days.
                var reach = staffOpen + OpenerReachMin;
                // ...and only while trading: before the doors open and after they
                // close there is one manager in the building.
                double? second = null;
                if (jh.Names is not null)
                    foreach (var t in JobSlots(jh))
                        if (t >= input.TradingOpen && t < input.TradingClose && t + B <= reach && placed.GetOr(t, 0) < depthPair)
                        {
                            second = t;
                            break;
                        }
                if (second is not null)
                {
                    pairOk.Add(second.Value);
                    jh.Placed.Set(second.Value, jh.Placed.GetOr(second.Value, 0) + 1);
                    placed.Set(second.Value, placed.GetOr(second.Value, 0) + 1);
                    continue;
                }
                dropped.Set(jh, dropped.GetOr(jh, 0) + 1);
            }
        }
        foreach (var (j, n) in dropped)
        {
            flags.Add(j.Names is not null
                ? $"admin task '{string.Join("', '", j.Names)}': {Js.NumberToString(Js.Round(Math.Min(j.Minutes ?? double.PositiveInfinity, n * B)))} min could not be planned inside its own window - more admin is due then than the managers on can work"
                : $"admin: {Js.ToFixed(n * B / 60, 2)}h could not be placed - more admin is due today than one manager at a time can work inside its windows");
        }
        foreach (var (m, n) in placed.Entries())
            if (n <= 0) placed.Delete(m);
        if (taskMode)
        {
            foreach (var j in jobs)
                foreach (var (m, n) in j.Placed)
                    if (n > 0)
                        for (var k = 0; k < n; k++)
                            unitWindows.Add(new UnitWindow(group, m, j.Names is not null ? j.Start : staffOpen, j.Names is not null ? j.End : staffClose, j.Names is not null));
        }
    }
}
