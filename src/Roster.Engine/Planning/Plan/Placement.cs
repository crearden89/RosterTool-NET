// Port of src/lib/plan/placement.ts.
// ─── Planning Order: WHEN each labour group's hours are placed ───────────────
// Pure (admin page, tests, and the placement pass).
//
// CONNECTED: Stage 1 consults this configuration whenever the master switch
// (PlacementSettings.Enabled) is on - see Stage1/Compute.cs. While the switch
// is off the config is stored and shown but never read by a plan.
//
// The distinction that matters, and the reason this is separate from the
// existing Rules → Labour Group Rules priority list:
//
//   Planning Order (here)  - WHEN a group's benchmark hours sit in the day.
//                            Decides the demand curve, and therefore when
//                            shifts start and finish. This is what governs
//                            penalty-time exposure.
//   Task Priority (existing) - WHO picks a task up once several groups are live
//                            in the same block (the manager takes Admin, the
//                            store assistant takes the till).
//
// Groups are placed SEQUENTIALLY in the configured order, each one filling
// around what earlier groups have already claimed - so position 1 gets the
// best of the day and everything after works with what is left.

using Roster.Engine.Core;

namespace Roster.Engine.Planning.Plan;

/// <summary>
/// One group's Planning Order rule. Group: checkout, load_running, reset, admin,
/// specials, cleaning, non_op. Strategy: spread, follow_demand, backfill,
/// forward_fill, peak_split, avoid_peak. AnchorKind: none, clock, open, close -
/// a fixed clock time, or a time RELATIVE to the store's own trading hours
/// (close − 2h travels with the store instead of pinning every store to 18:00).
/// </summary>
public sealed record PlacementRule
{
    public string Group { get; init; } = "";
    public string Strategy { get; init; } = "spread";
    public string AnchorKind { get; init; } = "none";
    public double? AnchorTime { get; init; } // minutes from midnight, "clock" kind only
    public double AnchorOffsetMin { get; init; } // ± minutes against open/close
    public double? PrePeakPct { get; init; } // peak_split: share placed before the peak
    public double? PostPeakPct { get; init; } // peak_split: share placed after the peak
    public double? AvoidStrength { get; init; } // avoid_peak: 0 = ignore the curve, 100 = avoid hardest
    // Applied instead of Strategy when the day's curve marks the store a heavy
    // night trader (see PlacementSettings). Null = no override.
    public string? NightTraderStrategy { get; init; }
}

public sealed record PlacementSettings
{
    // MASTER SWITCH. While false the configuration is stored and shown but
    // never consulted by plan generation.
    public bool Enabled { get; init; }
    // How the busy period is identified ("hourly" or "rolling"). Hourly buckets
    // are deliberately coarse: at 15-minute granularity a single block can read
    // 3.7% between neighbours of 2.6% and 2.8%, which is sampling noise, not a rush.
    public string PeakBasis { get; init; } = "hourly";
    public double PeakSmoothingBlocks { get; init; } = 3; // rolling basis: window width in 15-min blocks
    public double PeakThresholdPct { get; init; } = 85; // blocks at ≥ this % of the day's busiest count as peak
    public double NightTraderFromMin { get; init; } = 17 * 60; // minutes from midnight, e.g. 17:00
    // No store in the current data set exceeds ~22%, so this threshold is a
    // placeholder to be calibrated once the national store set is loaded - not a
    // figure derived from evidence.
    public double NightTraderSharePct { get; init; } = 25; // ≥ this share of the day's trade after that time
}

public sealed record StrategyMeta(string Strategy, string Label, string Description, bool UsesAnchor, bool UsesPeakSplit, bool UsesStrength);

public sealed record DayBounds(double TradingOpen, double TradingClose, double StaffOpen, double StaffClose);

public sealed record CurveShare(double Time, double Share);

public static class Placement
{
    /// <summary>
    /// The groups this page actually decides.
    ///
    /// Reset and Load Running are NOT among them, and never really were. Both are
    /// placed by models richer than one strategy per group, and both live with the
    /// thing that defines them on Rules → Labour Group Rules:
    ///
    ///   Reset        - the task catalogue. Stage 1 places Reset task-first, so a
    ///                  group-level directive only ever reached whatever benchmark
    ///                  the named tasks did not consume; the shipped shares total
    ///                  100%, leaving that at zero. The "split 40/40 around the
    ///                  peak" shown here has never affected a plan. Placement now
    ///                  sits on each task, where it can differ per job.
    ///   Load Running - the start rule. Load size decides the start, the morning's
    ///                  customers decide the finish, per store-day. An anchor here
    ///                  could only restate that, badly.
    ///
    /// Admin is task-first for the same reason, but its catalogue has no placement
    /// setting yet, so its row stays live for the benchmark hours the named tasks
    /// leave over - which, unlike Reset's, is routinely more than zero.
    /// </summary>
    public static readonly IReadOnlyList<string> PlacementGroups = ["checkout", "admin", "specials", "cleaning"];

    /// <summary>
    /// The order the day is laid down in - the seven steps of the solve, as the
    /// engine now runs them. Steps 1-6 are the aisle work and ARE re-orderable;
    /// step 7, Checkout, is not PLACED at all: the till engine says how many
    /// registers are open in each fifteen minutes, that number is exact, and the
    /// shifts are cut around it. It comes last because it is the thing
    /// everything else has to fit.
    /// </summary>
    public static readonly IReadOnlyList<string> StepGroups = ["admin", "load_running", "reset", "specials", "cleaning"];

    public static readonly IReadOnlyDictionary<string, string> StepNote = new Dictionary<string, string>
    {
        ["admin"] = "The named jobs from the task catalogue, each inside its own window - the date checks due before open, the cash-up after close. The tightest deadlines on the day, so they choose first.",
        ["load_running"] = "Inside the window the Load Running start rule sets for the day: load size decides how early the crew starts, customers in the first hour decide how late load may still be on the floor.",
        ["reset"] = "The re-runs, the first declare and the full decard, each sized by its share of the day's Reset hours and placed inside its own window by its own preference - spread, away from the busy period, or hard against close.",
        ["specials"] = "Levelled across its window.",
        ["cleaning"] = "Levelled across its window, pulled as late as the day allows.",
        ["non_op"] = "Training, meetings and anything else booked off the floor for the day.",
        ["checkout"] = "Not placed - imposed. The till engine fixes the number of open registers in every fifteen minutes and the shifts are cut so each one has a body. Where a block is short of tills, aisle work moves aside within its own window rather than a register going unmanned.",
    };

    /// <summary>Groups this page shows as governed elsewhere, with where to go instead.</summary>
    public static readonly IReadOnlyList<(string Group, string Owner, string Why)> Elsewhere =
    [
        ("reset", "Rules → Labour Group Rules → Reset tasks",
            "Each named job - the re-runs, the first declare, the full decard - carries its own share, its own window and its own placement, including whether it keeps out of the busy period."),
        ("load_running", "Rules → Labour Group Rules → Load Running start rule",
            "The day's load size decides how early the crew starts and the customers in the first hour decide how late load may still be running, per store-day."),
    ];

    public static readonly IReadOnlyDictionary<string, string> GroupLabel = new Dictionary<string, string>
    {
        ["checkout"] = "Checkout",
        ["load_running"] = "Load Running",
        ["reset"] = "Reset",
        ["admin"] = "Admin (AdminMGmt)",
        ["specials"] = "Specials",
        ["cleaning"] = "Cleaning",
        ["non_op"] = "Non-Operational",
    };

    public static readonly IReadOnlyList<StrategyMeta> Strategies =
    [
        new("spread", "Spread evenly across the window",
            "Water-fill the group's hours over every legal block, levelling the day's total. This is the default whenever no other strategy is configured.",
            false, false, false),
        new("follow_demand", "Follow the trade curve",
            "Place hours in proportion to the quarter-hour curve, so the group scales with how busy the store is. Checkout already works this way - the till engine pins it block by block.",
            false, false, false),
        new("backfill", "Work backwards from a time",
            "Fill the block at the anchor first, then each earlier block in turn. Keeps hours as LATE as the anchor allows - the way to hold work out of pre-07:00 penalty time.",
            true, false, false),
        new("forward_fill", "Work forwards from a time",
            "Fill the block at the anchor first, then each later block in turn. Keeps hours as EARLY as the anchor allows.",
            true, false, false),
        new("peak_split", "Split around the peak",
            "Place a share immediately before the busy period and a share immediately after it, then work the remainder backwards from the anchor. Keeps the floor focused on customers through the peak.",
            true, true, false),
        new("avoid_peak", "Avoid the peak (weighted)",
            "Spread evenly, but weight each block against how busy it is. A softer version of the split with no fixed shares - useful where the busy period is a long plateau rather than a sharp peak.",
            false, false, true),
    ];

    public static StrategyMeta StrategyMetaFor(string s) => Strategies.First(m => m.Strategy == s);

    public static readonly PlacementSettings DefaultSettings = new();

    private static PlacementRule Rule(string group, string strategy) => new() { Group = group, Strategy = strategy };

    // The shipped starting point - the client's stated intent, written down so it
    // can be argued with on screen rather than living in someone's head:
    //   1. Registers follow the trade curve (the till engine already pins these).
    //   2. Specials and Admin keep today's even spread; Cleaning is pulled as
    //      late as the day allows.
    // Reset and Load Running are placed by their own models on Rules → Labour
    // Group Rules - see PlacementGroups.
    public static readonly IReadOnlyList<PlacementRule> DefaultRules =
    [
        Rule("checkout", "follow_demand"),
        Rule("specials", "spread"),
        Rule("admin", "spread"),
        Rule("cleaning", "backfill") with { AnchorKind = "close", AnchorOffsetMin = 0 },
    ];

    /// <summary>
    /// Resolve a rule's anchor to minutes from midnight for one store-day.
    /// "none" falls back to the natural edge for the strategy's direction: the end
    /// of the staffed day when working backwards, the start when working forwards.
    /// </summary>
    public static double ResolveAnchor(PlacementRule r, DayBounds bounds) => r.AnchorKind switch
    {
        "clock" => r.AnchorTime ?? bounds.StaffClose,
        "open" => bounds.TradingOpen + r.AnchorOffsetMin,
        "close" => bounds.TradingClose + r.AnchorOffsetMin,
        "none" => r.Strategy == "forward_fill" ? bounds.StaffOpen : bounds.StaffClose,
        // An unknown kind: the TypeScript switch falls through to undefined, which
        // the caller's Math.min/max turns into NaN.
        _ => double.NaN,
    };

    /// <summary>True when the day's curve makes this store a heavy night trader.</summary>
    public static bool IsNightTrader(IReadOnlyList<CurveShare> curve, PlacementSettings settings)
    {
        double total = 0;
        foreach (var c in curve) total += c.Share;
        if (total <= 0) return false;
        double late = 0;
        foreach (var c in curve)
            if (c.Time >= settings.NightTraderFromMin) late += c.Share;
        return late / total * 100 >= settings.NightTraderSharePct;
    }

    /// <summary>The strategy that actually applies to a rule on a given day.</summary>
    public static string EffectiveStrategy(PlacementRule r, bool nightTrader) =>
        nightTrader && !string.IsNullOrEmpty(r.NightTraderStrategy) ? r.NightTraderStrategy : r.Strategy;

    // Normalise an untrusted list (DB rows / admin input): drop unknown groups and
    // strategies, de-duplicate, and append any group the config forgot so no labour
    // group is ever left without a placement rule.
    public static List<PlacementRule> NormaliseRules(IReadOnlyList<PlacementRule>? rules)
    {
        if (rules is null || rules.Count == 0) return DefaultRules.Select(r => r with { }).ToList();
        var knownGroup = new HashSet<string>(PlacementGroups);
        var knownStrategy = new HashSet<string>(Strategies.Select(m => m.Strategy));
        var out_ = new List<PlacementRule>();
        var seen = new HashSet<string>();
        foreach (var r in rules)
        {
            if (!knownGroup.Contains(r.Group) || !knownStrategy.Contains(r.Strategy)) continue;
            if (!seen.Add(r.Group)) continue;
            out_.Add(r with { });
        }
        foreach (var g in PlacementGroups)
        {
            if (seen.Contains(g)) continue;
            out_.Add(DefaultRules.FirstOrDefault(d => d.Group == g) ?? Rule(g, "spread"));
        }
        return out_;
    }

    /// <summary>
    /// Human-readable warnings for a configuration, shown on the admin page before
    /// saving. These are advisory - none of them block a save, because a deliberate
    /// odd configuration is a legitimate thing to want to try.
    /// </summary>
    public static List<string> ValidatePlacement(IReadOnlyList<PlacementRule> rules, PlacementSettings settings)
    {
        var warnings = new List<string>();
        foreach (var r in rules)
        {
            var meta = StrategyMetaFor(r.Strategy);
            var name = GroupLabel[r.Group];
            if (meta.UsesPeakSplit)
            {
                var pre = r.PrePeakPct ?? 0;
                var post = r.PostPeakPct ?? 0;
                if (pre + post > 100)
                    warnings.Add($"{name}: the before-peak and after-peak shares add up to {Js.NumberToString(pre + post)}% - more than the group's hours.");
                else if (pre + post == 0)
                    warnings.Add($"{name}: both peak shares are 0%, so every hour falls through to the anchor - the same as \"work backwards from a time\".");
            }
            if (meta.UsesAnchor && r.AnchorKind == "clock" && r.AnchorTime is null)
                warnings.Add($"{name}: a fixed clock anchor is selected but no time is set.");
            if (meta.UsesStrength && (r.AvoidStrength ?? 0) == 0)
                warnings.Add($"{name}: avoidance strength is 0, which behaves exactly like an even spread.");
        }
        var checkoutIdx = rules.ToList().FindIndex(r => r.Group == "checkout");
        if (checkoutIdx > 0)
            warnings.Add("Checkout is not first. It is pinned block-by-block by the till engine, so its position in this order has no practical effect.");
        if (rules.All(r => r.Strategy == "spread"))
            warnings.Add("Every group is set to an even spread - this is identical to how plans are generated today.");
        if (settings.PeakThresholdPct >= 100)
            warnings.Add("A peak threshold of 100% marks only the single busiest block as peak, so peak-aware strategies will barely move anything.");
        if (settings.NightTraderSharePct <= 0)
            warnings.Add("A night-trader threshold of 0% classifies every store as a heavy night trader, so every override is always applied.");
        return warnings;
    }

    /// <summary>
    /// The day's busy span from the quarter-hour curve: the contiguous run of
    /// blocks around the busiest one whose (smoothed) share stays at or above
    /// PeakThresholdPct of the maximum. Null when there is no curve - strategies
    /// that need a peak then degrade to their anchor behaviour.
    /// </summary>
    public static (double Start, double End)? FindPeak(IReadOnlyList<CurveShare> curve, PlacementSettings settings)
    {
        if (curve.Count == 0) return null;
        var sorted = Js.Sorted(curve, (a, b) => a.Time - b.Time);

        // Smooth per the configured basis. Hourly buckets are deliberately coarse -
        // at 15-minute granularity a single block can read 3.7% between neighbours
        // of 2.6% and 2.8%, which is sampling noise, not a rush.
        List<double> value;
        if (settings.PeakBasis == "hourly")
        {
            var byHour = new JsMap<double, double>();
            foreach (var c in sorted)
            {
                var h = Math.Floor(c.Time / 60);
                byHour.Set(h, byHour.GetOr(h, 0) + c.Share);
            }
            value = sorted.Select(c => byHour.GetOr(Math.Floor(c.Time / 60), 0)).ToList();
        }
        else
        {
            var half = (int)Math.Max(0, Math.Floor(settings.PeakSmoothingBlocks / 2));
            value = sorted.Select((_, i) =>
            {
                double sum = 0;
                var n = 0;
                for (var j = Math.Max(0, i - half); j <= Math.Min(sorted.Count - 1, i + half); j++)
                {
                    sum += sorted[j].Share;
                    n++;
                }
                return n > 0 ? sum / n : 0;
            }).ToList();
        }

        var max = Js.Max(value);
        if (max <= 0) return null;
        // A NaN share: JavaScript's indexOf cannot find NaN and the TypeScript
        // fails on sorted[-1]; fail the same way rather than invent a peak.
        if (double.IsNaN(max)) throw new InvalidOperationException("findPeak: the trade curve holds a NaN share");
        var threshold = settings.PeakThresholdPct / 100 * max;
        var top = value.IndexOf(max);
        var lo = top;
        var hi = top;
        while (lo > 0 && value[lo - 1] >= threshold) lo--;
        while (hi < value.Count - 1 && value[hi + 1] >= threshold) hi++;
        // end is EXCLUSIVE (the block after the last busy one), matching windows.
        return (sorted[lo].Time, sorted[hi].Time + 15);
    }
}
