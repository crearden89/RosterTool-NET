// Port of src/lib/till/borrow.ts.
// ─── Checkout rounding borrow (the old tool's "stealing") ───────────────────
// The till tier table rounds each 15-minute block UP to a whole till whenever
// the segment carries a remainder of 0.1 or more. Across a day that lands the
// checkout curve above the benchmark's checkout hours - at G242 on 17/08,
// 20.25h of tills against a 14.28h budget.
//
// The previous behaviour trimmed the curve back down to the budget, which
// silently understaffed the registers. The real rule is the one the old tool
// used: checkout KEEPS its rounded-up hours, and the excess is BORROWED from
// the other labour groups - a different mix depending on the time of day the
// excess occurred, since who can spare the time changes across the day.
//
// Blocks that round DOWN produce a negative excess, which nets off against the
// round-ups inside the same band before anything is borrowed - so a 1.1 that
// floors to 1 pays back some of what a 1.3 ceiling'd to 2 took.
//
// Labour groups are the window groups' strings (load_running, cleaning,
// specials, reset, admin); a Record<WindowGroup, number> is a JsMap keyed by
// them, in the TypeScript's key order.

using Roster.Engine.Core;

namespace Roster.Engine.Till;

public sealed record BorrowBand
{
    public string Label { get; init; } = "";
    public double FromMin { get; init; } // inclusive
    public double ToMin { get; init; } // exclusive
    /// <summary>Partial&lt;Record&lt;WindowGroup, number&gt;&gt;: each group's share, in the order they are taken from.</summary>
    public JsMap<string, double> Weights { get; init; } = new();
    // Some days draw on a different mix (Specials is only worked on delivery
    // days); when the day-of-week is listed here, AltWeights applies instead.
    public IReadOnlyList<int>? AltDays { get; init; } // Mon=0 .. Sun=6
    public JsMap<string, double>? AltWeights { get; init; }
}

/// <summary>One 15-minute block's checkout hours, planned and exact.</summary>
public sealed record BorrowBlock(
    double Time, // minutes from midnight
    double PlannedHours, // tills actually planned x 0.25
    double ExactHours); // un-rounded checkout demand for the block

/// <summary>A band's net excess (round-ups less round-downs), in hours.</summary>
public sealed record BandExcess(string Label, double ExcessHours);

public sealed record BorrowResult
{
    /// <summary>Hours taken from each group (all five groups, zero when none).</summary>
    public JsMap<string, double> Borrowed { get; init; } = new();
    // Hours handed back: a band whose tills came in UNDER their exact demand
    // (the SCO bank absorbing it, or the tier table rounding down) leaves
    // checkout hours the benchmark funds and nothing plans. They go to Reset -
    // floor work with the widest window - so the day stays on its benchmark
    // file rather than planning short (review RC-28: a band that rounded down
    // "took nothing", and the difference vanished; on the new SCO model that
    // put self-checkout stores more than an hour under, an error by R2).
    public JsMap<string, double> Lent { get; init; } = new();
    public double Requested { get; init; } // total excess the rounding created
    public double BorrowedTotal { get; init; } // what could actually be taken
    public double Shortfall { get; init; } // excess nothing could cover (floors reached)
    public List<BandExcess> ByBand { get; init; } = new();
}

public static class Borrow
{
    // The client's rules:
    //   before 10:00  → all from Load Running
    //   10:00-18:00   → Tue/Fri 65% Reset / 25% Admin / 10% Specials;
    //                   the other five days 70% Reset / 30% Admin (no Specials)
    //   from 18:00    → all from Cleaning
    public static readonly IReadOnlyList<BorrowBand> DefaultBorrowBands =
    [
        new() { Label = "Before 10:00", FromMin = 0, ToMin = 10 * 60, Weights = Weights((TaskGroups.LoadRunning, 1)) },
        new()
        {
            Label = "10:00-18:00",
            FromMin = 10 * 60,
            ToMin = 18 * 60,
            Weights = Weights((TaskGroups.Reset, 0.7), (TaskGroups.Admin, 0.3)),
            AltDays = [1, 4], // Tuesday, Friday
            AltWeights = Weights((TaskGroups.Reset, 0.65), (TaskGroups.Admin, 0.25), (TaskGroups.Specials, 0.1)),
        },
        new() { Label = "From 18:00", FromMin = 18 * 60, ToMin = 24 * 60, Weights = Weights((TaskGroups.Cleaning, 1)) },
    ];

    private static JsMap<string, double> Weights(params (string Group, double Weight)[] ws) =>
        new(ws.Select(w => (w.Group, w.Weight)));

    /// <summary>The same bands with their boundaries moved (Rules page settings, R15).</summary>
    public static List<BorrowBand> BorrowBandsAt(double morningEndMin, double eveningFromMin)
    {
        var am = DefaultBorrowBands[0];
        var day = DefaultBorrowBands[1];
        var pm = DefaultBorrowBands[2];
        static string Fmt(double m) =>
            $"{Js.PadStart(Js.NumberToString(Math.Floor(m / 60)), 2, '0')}:{Js.PadStart(Js.NumberToString(m % 60), 2, '0')}";
        return
        [
            am with { Label = $"Before {Fmt(morningEndMin)}", ToMin = morningEndMin },
            day with { Label = $"{Fmt(morningEndMin)}-{Fmt(eveningFromMin)}", FromMin = morningEndMin, ToMin = eveningFromMin },
            pm with { Label = $"From {Fmt(eveningFromMin)}", FromMin = eveningFromMin },
        ];
    }

    // "No labour group should ever run dry": a group is never borrowed below this
    // many hours. One 15-minute block by default - enough that the group still
    // exists on the plan, and small enough not to block ordinary borrowing.
    public const double MinRetainedHours = 0.25;

    public static readonly IReadOnlyList<string> Borrowable =
    [
        TaskGroups.LoadRunning,
        TaskGroups.Reset,
        TaskGroups.Admin,
        TaskGroups.Specials,
        TaskGroups.Cleaning,
    ];

    private static JsMap<string, double> Zeroed() => new()
    {
        [TaskGroups.LoadRunning] = 0,
        [TaskGroups.Cleaning] = 0,
        [TaskGroups.Specials] = 0,
        [TaskGroups.Reset] = 0,
        [TaskGroups.Admin] = 0,
    };

    private static JsMap<string, double> WeightsFor(BorrowBand band, int dowMon0) =>
        band.AltDays is not null && band.AltDays.Contains(dowMon0) && band.AltWeights is not null ? band.AltWeights : band.Weights;

    /// <summary>
    /// Work out how much to take from each labour group to fund the checkout
    /// rounding, honouring the per-group floor.
    ///
    /// <paramref name="available"/> is each group's benchmark hours for the day. Anything that can't
    /// be taken from a band's own groups spills, pro-rata by remaining headroom, to
    /// every other borrowable group - so one exhausted group doesn't strand the
    /// whole excess.
    /// </summary>
    /// <param name="bands">Null: <see cref="DefaultBorrowBands"/>.</param>
    /// <param name="minRetained">Null: <see cref="MinRetainedHours"/>.</param>
    /// <param name="committed">
    /// Hours a group has already promised to fixed work and therefore cannot
    /// lend. Admin is the live case: the task catalogue pins named jobs to the
    /// day, so only whatever Admin holds ABOVE its task total is really spare -
    /// borrowing into it just makes the day plan more Admin than it has.
    /// </param>
    public static BorrowResult ComputeBorrow(
        IReadOnlyList<BorrowBlock> blocks,
        int dowMon0,
        JsMap<string, double> available,
        IReadOnlyList<BorrowBand>? bands = null,
        double? minRetained = null,
        JsMap<string, double>? committed = null)
    {
        bands ??= DefaultBorrowBands;
        var floor = minRetained ?? MinRetainedHours;
        committed ??= new JsMap<string, double>();
        var borrowed = Zeroed();
        var lent = Zeroed();
        var headroom = new JsMap<string, double>();
        foreach (var g in Borrowable)
        {
            var reserved = Js.Max(floor, committed.GetOr(g, 0));
            headroom.Set(g, Js.Max(0, available.GetOr(g, 0) - reserved));
        }

        double Take(string g, double want)
        {
            // A missing entry reads as undefined, which is NaN in arithmetic - and a
            // NaN gets past `got <= 0`, as in the TypeScript.
            var room = headroom.GetOr(g, double.NaN);
            var got = Js.Min(want, room);
            if (got <= 0) return 0;
            headroom.Set(g, room - got);
            borrowed.Set(g, borrowed.GetOr(g, double.NaN) + got);
            return got;
        }

        // Share `amount` across `groups` in proportion to the headroom each has
        // left; returns whatever could not be placed.
        double Spill(double amount, IReadOnlyList<string> groups)
        {
            var pool = groups.Where(g => headroom.GetOr(g, double.NaN) > 0).ToList();
            var spare = pool.Aggregate(0.0, (s, g) => s + headroom.GetOr(g, 0));
            if (spare <= 0) return amount;
            var left = amount;
            foreach (var g in pool) left -= Take(g, amount * (headroom.GetOr(g, 0) / spare));
            return Js.Max(0, left);
        }

        double requested = 0;
        double shortfall = 0;
        var byBand = new List<BandExcess>();

        foreach (var band in bands)
        {
            // Net excess inside the band: round-ups less round-downs. Only a positive
            // net needs funding - a band that rounded down overall took nothing.
            var net = blocks
                .Where(b => b.Time >= band.FromMin && b.Time < band.ToMin)
                .Aggregate(0.0, (s, b) => s + (b.PlannedHours - b.ExactHours));
            byBand.Add(new BandExcess(band.Label, net));
            if (net < -1e-9) lent.Set(TaskGroups.Reset, lent.GetOr(TaskGroups.Reset, 0) + -net);
            if (net <= 1e-9) continue;
            requested += net;

            var weights = WeightsFor(band, dowMon0);
            var outstanding = net;
            foreach (var (g, w) in weights.Entries())
            {
                outstanding -= Take(g, net * w);
            }

            // Spill what a group couldn't cover onto the band's OTHER groups first -
            // staying with the sources the day allows, so Specials isn't touched on a
            // non-delivery day merely because Admin was fully committed. Only once the
            // band itself is exhausted does it reach outside.
            if (outstanding > 1e-9)
            {
                var bandGroups = weights.Keys().ToList();
                outstanding = Spill(outstanding, bandGroups);
                if (outstanding > 1e-9)
                {
                    outstanding = Spill(outstanding, Borrowable.Where(g => !bandGroups.Contains(g)).ToList());
                }
                // Still short: everything is at its floor or fully committed. Checkout
                // keeps the hours and the day runs over benchmark - flagged by Stage 1.
                if (outstanding > 1e-9) shortfall += outstanding;
            }
        }

        var borrowedTotal = Borrowable.Aggregate(0.0, (s, g) => s + borrowed.GetOr(g, 0));
        return new BorrowResult
        {
            Borrowed = borrowed,
            Lent = lent,
            Requested = requested,
            BorrowedTotal = borrowedTotal,
            Shortfall = shortfall,
            ByBand = byBand,
        };
    }
}
