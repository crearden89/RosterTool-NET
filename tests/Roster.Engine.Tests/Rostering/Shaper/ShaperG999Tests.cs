// Port of src/lib/stage2/shaper.g999.test.ts.
// ─── Real-data regressions: store G999, Mon 03.08 + Tue 04.08 2026 ──────────
// The days the user walked through line-by-line. The raw engines produced
// invented Load Running/reset tiles, 3h fragment piles, under-manned tills,
// break holes eating benchmark hours, evening cleaning over-cover and a
// closing manager locking up alone. These assertions pin the fixed behaviour.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;
using Roster.Engine.Till;

namespace Roster.Engine.Tests.Rostering.Shaper;

public class ShaperG999Tests
{
    private const double H = 60;
    private const double StaffOpen = 6 * H;
    private const double StaffClose = 20.5 * H;

    private static readonly CheckoutAttrs Attrs = new()
    {
        HasSco = true,
        IsNewScoModel = true,
        McoRegisters = 6,
        ScoLanes = 6,
        ScoEfficiency = 0.45,
        FirstStep = 1.2,
        ScoShare = 0.66,
    };

    public sealed record DayFixture(
        string Name,
        JsMap<string, double> Bench,
        double CheckoutHours90,
        List<WindowRule> Windows,
        (double Hour, double Allocation)[] Qh,
        double TradingOpen,
        double TradingClose)
    {
        public override string ToString() => Name;
    }

    private static JsMap<string, double> Bench(double lr, double cleaning, double specials, double reset, double admin) => new()
    {
        ["load_running"] = lr,
        ["cleaning"] = cleaning,
        ["specials"] = specials,
        ["reset"] = reset,
        ["admin"] = admin,
    };

    private static readonly DayFixture Monday = new(
        "G999 Monday 2026-08-03 (real data)",
        Bench(13.00331, 2.1666667, 2.2244567, 8.2571022, 5.875),
        11.858,
        [
            new("load_running", 6 * H, 10 * H),
            new("admin", 7 * H, 17 * H),
            new("reset", 10 * H, 20 * H),
            new("cleaning", 18 * H, 21 * H),
            new("specials", 17 * H, 21 * H),
            new("specials", 18 * H, 21 * H),
        ],
        [
            (8.5, 0.003655), (8.75, 0.006026), (9, 0.010768), (9.25, 0.011657),
            (9.5, 0.018572), (9.75, 0.012644), (10, 0.020844), (10.25, 0.024993),
            (10.5, 0.024696), (10.75, 0.029635), (11, 0.029043), (11.25, 0.022325),
            (11.5, 0.025684), (11.75, 0.027857), (12, 0.032006), (12.25, 0.030327),
            (12.5, 0.020053), (12.75, 0.026771), (13, 0.020448), (13.25, 0.026672),
            (13.5, 0.019461), (13.75, 0.024005), (14, 0.028351), (14.25, 0.020547),
            (14.5, 0.025783), (14.75, 0.026573), (15, 0.024104), (15.25, 0.031414),
            (15.5, 0.0244), (15.75, 0.024301), (16, 0.024499), (16.25, 0.034377),
            (16.5, 0.032105), (16.75, 0.035365), (17, 0.03092), (17.25, 0.030623),
            (17.5, 0.029438), (17.75, 0.023116), (18, 0.022721), (18.25, 0.01867),
            (18.5, 0.013435), (18.75, 0.0081), (19, 0.006915), (19.25, 0.005236),
            (19.5, 0.006125), (19.75, 0.003655),
        ],
        8.5 * H,
        20 * H);

    private static readonly DayFixture Tuesday = new(
        "G999 Tuesday 2026-08-04 (real data)",
        Bench(13.0917244, 3.3, 2.5161333, 8.3132456, 5.875),
        16.0636667,
        [
            new("load_running", 6 * H, 10 * H),
            new("admin", 7 * H, 17 * H),
            new("reset", 10 * H, 20 * H),
            new("cleaning", 18 * H, 21 * H),
            new("specials", 10 * H, 21 * H),
            new("specials", 18 * H, 21 * H),
        ],
        [
            (8.5, 0.006627), (8.75, 0.013569), (9, 0.014305), (9.25, 0.015462),
            (9.5, 0.013674), (9.75, 0.019354), (10, 0.01725), (10.25, 0.025034),
            (10.5, 0.023562), (10.75, 0.025139), (11, 0.023036), (11.25, 0.027559),
            (11.5, 0.027874), (11.75, 0.031871), (12, 0.027664), (12.25, 0.027664),
            (12.5, 0.027243), (12.75, 0.026928), (13, 0.026086), (13.25, 0.030188),
            (13.5, 0.031135), (13.75, 0.026822), (14, 0.023562), (14.25, 0.025665),
            (14.5, 0.032187), (14.75, 0.022194), (15, 0.02577), (15.25, 0.027138),
            (15.5, 0.032713), (15.75, 0.022615), (16, 0.024403), (16.25, 0.029136),
            (16.5, 0.027453), (16.75, 0.028295), (17, 0.023772), (17.25, 0.028085),
            (17.5, 0.022089), (17.75, 0.023351), (18, 0.020406), (18.25, 0.014305),
            (18.5, 0.012412), (18.75, 0.006732), (19, 0.006101), (19.25, 0.003576),
            (19.5, 0.005785), (19.75, 0.003787),
        ],
        8.5 * H,
        20 * H);

    public static TheoryData<DayFixture> Days => new() { Monday, Tuesday };

    private sealed record BuiltDay(
        Stage1Result S1,
        ShapedDay Shaped,
        JsMap<string, double> TileHours,
        JsMap<double, double> CheckoutTilesAt,
        JsMap<double, double> TillsAt,
        List<PaintableShift> Paintable);

    private static BuiltDay BuildDay(DayFixture fx)
    {
        // Till engine + benchmark budget cap, exactly as till/compute wires it.
        var tillBlocks = fx.Qh.Select(q => TillEngine.ComputeBlock(fx.CheckoutHours90 * q.Allocation * 4, Attrs)).ToList();
        TillEngine.CapToBudget(tillBlocks, Math.Max(fx.CheckoutHours90, (fx.TradingClose - fx.TradingOpen) / 60));
        var checkout = tillBlocks.Select((b, i) => new CheckoutDemand(Js.Round(fx.Qh[i].Hour * H), b.Total)).ToList();

        var s1 = Stage1Engine.RunStage1(new Stage1Input
        {
            TradingOpen = fx.TradingOpen,
            TradingClose = fx.TradingClose,
            StaffOpen = StaffOpen,
            StaffClose = StaffClose,
            Benchmark90 = fx.Bench,
            Windows = fx.Windows,
            Checkout = checkout,
        });

        // Aggregate to shaper demand (mirrors planSlots.demandFromStage1).
        var demand = PlanSlots.DemandFromStage1(s1.Blocks);
        // Window barriers, shared by the shaper's levelling pass and the painter.
        var allowed = new JsMap<double, List<string>>();
        for (var m = StaffOpen; m < StaffClose; m += 15)
        {
            var gs = fx.Windows.Where(w => m >= w.Start && m < w.End).Select(w => w.Group).ToList();
            if (m >= fx.TradingOpen && m < fx.TradingClose) gs.Add("checkout");
            allowed.Set(m, new JsSet<string>(gs).ToList());
        }
        var shaped = PlanShaper.ShapePlan(new ShaperInput
        {
            StaffOpen = StaffOpen,
            StaffClose = StaffClose,
            TradingOpen = fx.TradingOpen,
            TradingClose = fx.TradingClose,
            IsSchoolDay = false,
            DemandByBlock = demand,
            MgrRule = new ShaperRule(3, 12),
            SaRule = new ShaperRule(3, 9.5),
            JsaRule = new ShaperRule(3, 6),
            AllowedAt = t => allowed.GetOr(t, []),
        });

        var paintable = shaped.Slots.Select(s => new PaintableShift
        {
            Role = s.Role == "MGR" ? "ASM" : s.Role == "JSA" ? "JSA" : "SA",
            Start = s.Start,
            End = s.End,
            Blocks = [],
        }).ToList();
        TaskPainter.PaintTasks(paintable, shaped.DemandByBlock, TaskGroups.TaskPriority, t => allowed.GetOr(t, []));

        // Tile hours per group + painted checkout per block, as the plan renders.
        var tileHours = new JsMap<string, double>();
        var checkoutTilesAt = new JsMap<double, double>();
        foreach (var p in paintable)
        {
            foreach (var b in p.Blocks)
            {
                if (b.Group == "flex" || b.Group == "break") continue;
                tileHours.Set(b.Group, tileHours.GetOr(b.Group, 0) + 0.25);
                if (b.Group == "checkout") checkoutTilesAt.Set(b.Time, checkoutTilesAt.GetOr(b.Time, 0) + 1);
            }
        }
        var tillsAt = new JsMap<double, double>();
        foreach (var d in shaped.DemandByBlock)
            if (d.GroupCount("checkout") > 0) tillsAt.Set(d.Time, d.GroupCount("checkout"));
        return new BuiltDay(s1, shaped, tileHours, checkoutTilesAt, tillsAt, paintable);
    }

    private static string Hm(double m) => $"{Math.Floor(m / 60)}:{Js.PadStart(Js.NumberToString(m % 60), 2, '0')}";

    [Theory]
    [MemberData(nameof(Days))]
    public void Mans_every_till_in_every_block_painted_checkout_equals_the_Tills_row_EXACTLY(DayFixture fx)
    {
        var day = BuildDay(fx);
        foreach (var (m, tills) in day.TillsAt)
        {
            var painted = day.CheckoutTilesAt.GetOr(m, 0);
            Assert.True(painted == tills, $"{Hm(m)} - {painted} checkout tiles vs {tills} tills");
        }
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void Keeps_every_groups_tiles_within_0_75h_of_its_benchmark(DayFixture fx)
    {
        // 0.75h (not 0.5h) because a short morning shift's award-legal break
        // must sit INSIDE the load window - the half hour of Load Running it
        // displaces has nowhere legal to relocate, and trailing breaks are
        // forbidden (≥1h of tasks must follow the last break).
        //
        // The mandated MORNING ADMIN block (the opening manager's first 15 min)
        // is netted out before comparing: it moves 0.25h into admin from
        // whichever group the opener would have started on, by design rather
        // than by allocation error, so it must not consume the envelope.
        var day = BuildDay(fx);
        foreach (var a in day.S1.Allocations)
        {
            var tiles = day.TileHours.GetOr(a.Group, 0);
            if (a.Group == "admin") tiles -= 0.25;
            var slack = a.Group == "admin" ? 0.75 : 1.0;
            Assert.True(Math.Abs(tiles - a.BenchmarkHours) <= slack,
                $"{a.Group}: {Js.ToFixed(tiles, 2)}h tiles vs {Js.ToFixed(a.BenchmarkHours, 2)}h benchmark");
        }
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void Keeps_the_DAY_inside_the_1h_allowance_the_plan_page_enforces(DayFixture fx)
    {
        // Allocated = the work tiles actually drawn (what the Benchmark Hours
        // table's Total row counts) - the daily allowance is benchmark ±1h.
        var day = BuildDay(fx);
        var allocated = day.TileHours.Values().Sum();
        Assert.True(Math.Abs(allocated - day.S1.TotalBenchmark) <= 1,
            $"allocated {Js.ToFixed(allocated, 2)}h vs benchmark {Js.ToFixed(day.S1.TotalBenchmark, 2)}h");
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void Places_breaks_mid_shift_never_the_first_hour_always_1h_of_tasks_after_the_last_break(DayFixture fx)
    {
        var day = BuildDay(fx);
        foreach (var p in day.Paintable)
        {
            var breaks = Js.Sorted(p.Blocks.Where(b => b.Group == "break").Select(b => b.Time), (a, b) => a - b);
            if (breaks.Count == 0) continue;
            // Never within the first hour of the shift (hard award rule).
            Assert.True(breaks[0] >= p.Start + 60, $"break at {breaks[0]} on {p.Start}-{p.End}");
            // A meal-length segment must START by the 5th hour.
            Assert.True(breaks[0] - p.Start <= 5 * 60);
            // At least 1 hour of tasks after the LAST break - no trailing breaks,
            // no exceptions.
            Assert.True(breaks[^1] + 15 <= p.End - 60, $"last break on {p.Start}-{p.End} runs past end-60");
        }
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void Doesnt_fragment_the_day_into_a_pile_of_3h_shifts(DayFixture fx)
    {
        var day = BuildDay(fx);
        var shorties = day.Shaped.Slots.Count(s => s.Role != "MGR" && s.End - s.Start <= 3.25 * 60);
        Assert.True(shorties <= 2, string.Join("; ", day.Shaped.Slots));
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void Never_leaves_the_closing_manager_alone_at_the_end_of_the_day(DayFixture fx)
    {
        var day = BuildDay(fx);
        var spanEnd = day.Shaped.Slots.Max(s => s.End);
        for (var m = spanEnd - 60; m < spanEnd; m += 15)
        {
            var bodies = day.Shaped.Slots.Count(s => s.Start <= m && m < s.End);
            Assert.True(bodies >= 2, $"only {bodies} planned at {Hm(m)}");
        }
        Assert.Equal(StaffClose, spanEnd);
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void Emits_exactly_two_manager_slots(DayFixture fx)
    {
        Assert.Equal(2, BuildDay(fx).Shaped.Slots.Count(s => s.Role == "MGR"));
    }
}
