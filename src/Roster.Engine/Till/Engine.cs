// Port of src/lib/till/engine.ts.
// ─── Till Allocation Engine (spec Section 6) ─────────────────────────────────
// The "v5 bounded-credit SCO model" (the "Updated" methodology). Governs the
// Registers + SCOs labour groups. Given a per-15-minute-block checkout demand
// figure (`Segment`, from the Quarter-Hour upload, already rescaled by any
// active sales override) plus the store's checkout Attributes, it returns the
// number of MCO registers and SCO lanes needed for that block.
//
// This module is intentionally PURE (no I/O) so it can be validated
// independently against the reference workbook's Calc_Blocks / Output_Daily
// figures before being wired into Stage 1 (spec build phasing, Section 14 #2).
//
// Only the "Updated" methodology is implemented here; the legacy "Original" /
// "SQL" methodologies from the reference workbook are historical-comparison
// only and are deliberately NOT built.

using Roster.Engine.Core;

namespace Roster.Engine.Till;

/// <summary>Store checkout attributes the engine needs (from store_attributes).</summary>
public sealed record CheckoutAttrs
{
    public bool HasSco { get; init; }
    public bool IsNewScoModel { get; init; }
    public double McoRegisters { get; init; } // physical register count - hard ceiling on MCO tier
    public double ScoLanes { get; init; } // physical SCO lane count - ceiling on SCO tier
    public double ScoEfficiency { get; init; } // per-lane demand-absorption credit (default 0.45)
    public double FirstStep { get; init; } // Segment threshold at which the SCO bank opens (default 1.2)
    public double ScoShare { get; init; } // store's SCO-eligible customer share, clamped 0.00–0.90
}

/// <summary>
/// One block's tills. Mutable, as the TypeScript object is: <see cref="TillEngine.CapToBudget"/>
/// trims tills off blocks in place.
/// </summary>
public record BlockResult
{
    public double Segment { get; set; }
    public double ScoOn { get; set; } // 0 | 1
    public double Credit { get; set; }
    public double Mco { get; set; } // MCO registers needed (after cap)
    public double McoWanted { get; set; } // MCO registers the demand asks for, before the register cap
    public double Sco { get; set; } // SCO lanes/attendant needed (after cap)
    public double Total { get; set; } // combined Till Allocation figure shown on the Task View

    public BlockResult() { }

    public BlockResult(double segment, double scoOn, double credit, double mco, double mcoWanted, double sco, double total)
    {
        Segment = segment;
        ScoOn = scoOn;
        Credit = credit;
        Mco = mco;
        McoWanted = mcoWanted;
        Sco = sco;
        Total = total;
    }
}

/// <summary>One rung of the till ladder: every segment in [From, To) yields these tills.</summary>
public sealed record TierBracket
{
    public double From { get; set; } // inclusive
    public double? To { get; set; } // exclusive; null = open-ended (register cap reached)
    public double Mco { get; set; }
    public double Sco { get; set; }
    public double Total { get; set; }
}

public static class TillEngine
{
    // Guards the tier boundaries against floating-point drift so a Segment landing
    // exactly on a boundary lands in the higher tier - matching the spec's
    // inclusive lower bound ("N + Credit ≤ Segment"). Without this, e.g. 2.1 + 1.8
    // evaluates to 3.9000000000000004 and a Segment of 3.9 would fall one tier low.
    private const double Eps = 1e-9;

    // MCO tier for the bounded-credit ("Updated") model. `Credit` shifts each
    // threshold upward, letting the open SCO bank absorb demand before another
    // manned register is required (spec Section 6, step 3).
    private static double McoNewModel(double segment, double firstStep, double credit)
    {
        if (segment <= 0) return 0;
        if (segment < firstStep + credit - Eps) return 1;
        if (segment < 2.1 + credit - Eps) return 2;
        if (segment < 3.1 + credit - Eps) return 3;
        if (segment < 4.1 + credit - Eps) return 4;
        if (segment < 5.1 + credit - Eps) return 5;
        return 6; // open-ended before capping
    }

    // MCO tier for the legacy path (Has_SCO false OR Is_NewSCOModel false):
    // CEILING(Segment) when the decimal remainder ≥ 0.1, else FLOOR(Segment).
    private static double McoLegacy(double segment)
    {
        if (segment <= 0) return 0;
        var remainder = segment - Math.Floor(segment);
        return remainder >= 0.1 ? Math.Ceiling(segment) : Math.Floor(segment);
    }

    /// <summary>
    /// Compute MCO + SCO needed for a single 15-minute block.
    /// Steps mirror spec Section 6 (SCO bank state → bounded credit → tier tables → cap).
    /// </summary>
    public static BlockResult ComputeBlock(double segment, CheckoutAttrs a)
    {
        // 1. SCO bank state - opens once demand reaches the first step.
        double scoOn = segment >= a.FirstStep ? 1 : 0;

        // 2. Bounded absorption credit (only while the bank is open).
        var laneCapacity = a.ScoEfficiency * a.ScoLanes;
        var routedDemand = a.ScoShare * segment;
        var credit = scoOn == 0 ? 0 : Js.Min(laneCapacity, routedDemand);

        double mco;
        double sco;

        if (a.HasSco && a.IsNewScoModel)
        {
            // 3. Updated methodology.
            mco = McoNewModel(segment, a.FirstStep, credit);
            sco = scoOn; // SCO here = whether the bank is staffed with an attendant (0/1)
        }
        else
        {
            // 4. Legacy path.
            mco = McoLegacy(segment);
            sco = 0;
        }

        // 5. Cap each tier at the physical count available for that type. What the
        //    cap drops is kept (mcoWanted) so the plan can say so (decision R2):
        //    demand above the register count used to vanish with no flag.
        var mcoWanted = Js.Max(0, mco);
        mco = Js.Max(0, Js.Min(mco, a.McoRegisters));
        sco = Js.Max(0, Js.Min(sco, a.ScoLanes));

        return new BlockResult(segment, scoOn, credit, mco, mcoWanted, sco, mco + sco);
    }

    /// <summary>Run the engine across a day's blocks (in time order).</summary>
    public static List<BlockResult> ComputeDay(IEnumerable<double> segments, CheckoutAttrs a) =>
        segments.Select(s => ComputeBlock(s, a)).ToList();

    /// <summary>
    /// At least one till in every trading quarter-hour - a register, or the SCO
    /// attendant where the store has lanes and no registers - even when the demand
    /// rounds to none (owner decision 18; review RC-38). This replaces the
    /// Minimum Checkout Hours setting (T10), which nothing ever read: a daily
    /// figure can't man a quarter-hour, a till in every one of them can.
    /// Returns <paramref name="r"/> itself when it already has a till, else a copy.
    /// </summary>
    public static BlockResult LiftToOneTill(BlockResult r, double mcoRegisters, double scoLanes)
    {
        if (r.Total >= 1) return r;
        if (mcoRegisters <= 0 && scoLanes > 0) return r with { Sco = 1, Total = 1 };
        return r with { Mco = 1, McoWanted = Js.Max(r.McoWanted, 1), Total = 1 };
    }

    /// <inheritdoc cref="LiftToOneTill(BlockResult, double, double)"/>
    public static BlockResult LiftToOneTill(BlockResult r, CheckoutAttrs a) => LiftToOneTill(r, a.McoRegisters, a.ScoLanes);

    /// <summary>
    /// Daily Checkout hours = Σ(combined per-block till counts) × 15/60. Every
    /// trading quarter-hour carries at least one till (LiftToOneTill), so the day
    /// never plans below its trading hours. Because each block total is an integer
    /// × 0.25h, the result is naturally a whole/quarter-hour figure.
    /// </summary>
    public static double DailyCheckoutHours(IEnumerable<BlockResult> blocks) =>
        blocks.Aggregate(0.0, (sum, b) => sum + b.Total) * (15.0 / 60);

    /// <summary>
    /// Cap the day's till hours to a budget, trimming above-floor tills from the
    /// QUIETEST blocks (lowest Segment) first and never dropping a block below 1
    /// till. Mutates <paramref name="blocks"/>; the SCO attendant is released before registers.
    ///
    /// NO LONGER IN THE PLANNING PIPELINE. Capping checkout to the benchmark's
    /// checkout hours understaffed the registers: the tier table's round-ups are
    /// real demand, and the tool this replaces funded them by borrowing from the
    /// other labour groups instead of trimming tills (see Till/Borrow.cs).
    /// Retained as a utility - the shaper audit fixture uses it to reproduce the
    /// pre-borrow curve - but ComputeTillForDate no longer calls it.
    /// </summary>
    public static void CapToBudget(IReadOnlyList<BlockResult> blocks, double budgetHours)
    {
        var totalHours = blocks.Aggregate(0.0, (s, b) => s + b.Total) * (15.0 / 60);
        if (totalHours <= budgetHours + 1e-9) return;
        var order = Js.Sorted(blocks, (a, b) => a.Segment - b.Segment);
        var trimmed = true;
        while (trimmed && totalHours > budgetHours + 1e-9)
        {
            trimmed = false;
            foreach (var b in order)
            {
                if (totalHours <= budgetHours + 1e-9) break;
                if (b.Total <= 1) continue;
                if (b.Sco > 0) b.Sco--;
                else b.Mco--;
                b.Total--;
                totalHours -= 0.25;
                trimmed = true;
            }
        }
    }

    /// <summary>
    /// Derive the store's till ladder - the segment ranges that produce 1, 2, 3 …
    /// tills - by RUNNING the engine across the range rather than re-deriving the
    /// tier algebra. Any change to ComputeBlock is reflected automatically, so the
    /// explainer on the Till Preview screen can never drift from what actually
    /// plans the day.
    ///
    /// The boundaries are not evenly spaced under the Updated model: while routed
    /// demand binds, `credit` grows WITH the segment, so the rung you are trying to
    /// cross moves as demand rises and the bracket stretches to
    /// `first_step / (1 - sco_share)`. Once `sco_efficiency x lanes` caps the credit
    /// the boundaries settle back to a flat 1.0 apart. Seeing both phases side by
    /// side is the point of showing this.
    /// </summary>
    public static List<TierBracket> TierBrackets(CheckoutAttrs a, double maxSegment = 12, double step = 0.001)
    {
        var out_ = new List<TierBracket>();
        TierBracket? cur = null;
        var steps = Js.Round(maxSegment / step);
        for (double i = 0; i <= steps; i++)
        {
            var segment = (i * step * 1000) / 1000;
            var r = ComputeBlock(segment, a);
            if (cur is not null && cur.Mco == r.Mco && cur.Sco == r.Sco) continue;
            if (cur is not null) cur.To = segment;
            cur = new TierBracket { From = segment, To = null, Mco = r.Mco, Sco = r.Sco, Total = r.Total };
            out_.Add(cur);
        }
        return out_;
    }

    /// <summary>
    /// The segment at which the SCO credit stops growing and the lane cap binds.
    /// Null when there is no SCO bank, or when the share is zero (never grows).
    /// </summary>
    public static double? CreditCapSegment(CheckoutAttrs a)
    {
        if (!a.HasSco || !a.IsNewScoModel || a.ScoShare <= 0) return null;
        return (a.ScoEfficiency * a.ScoLanes) / a.ScoShare;
    }
}
