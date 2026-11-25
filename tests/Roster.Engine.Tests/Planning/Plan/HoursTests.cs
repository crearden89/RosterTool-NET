// Port of src/lib/plan/plannedHours.test.ts, benchmarkSplit.test.ts and
// loadRunning.test.ts. (Their cross-checks against the Plan page's, the
// Dashboard's and Analysis's own figures - pageFigures, overview/fortnight and
// analysis/figures - are those pages' tests.)

using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning.Plan;

public class PlannedHoursTests
{
    // A shift as the planner holds it: a quarter-hour tile per entry from `start`.
    private static PlanShift Shift(double start, IReadOnlyList<string> tiles) => new()
    {
        SlotId = $"s{start}",
        PlanRole = "SA",
        Start = start,
        End = start + tiles.Count * 15,
        BreakMinutes = tiles.Count(t => t == "break") * 15,
        UnpaidBreakMinutes = 0,
        Blocks = tiles.Select((g, i) => new DisplayBlock(start + i * 15, g)).ToList(),
    };

    private static IEnumerable<string> Run(string group, int n) => Enumerable.Repeat(group, n);

    private static double TilesOff(IEnumerable<PlanShift> shifts) =>
        shifts.Aggregate(0.0, (t, s) => t + s.Blocks.Count(b => b.Group != "break") * 0.25);

    /// <summary>A held day: the day checker's hours are the shifts less their breaks.</summary>
    private static HeldPlanDay Day(double benchmark, List<PlanShift> shifts, bool legacy = false) => new(
        new BenchmarkParts
        {
            TotalBenchmark = benchmark - 0.2,
            Allocations = [new("checkout", 3, 3), new("load_running", 2.5, 2.5), new("non_op", 0.5, 0.5)],
        },
        new PlanShiftComputation
        {
            HasData = true,
            Shifts = shifts,
            Flags = [],
            Hours = legacy ? null : new HoursCheck { Worked = TilesOff(shifts), Benchmark = benchmark },
        });

    // Three days: tills split into registers and SCOs, non-op, breaks, and time
    // held with no work ("flex": cover, a minimum shift); one held before the day
    // checker kept its hours; and a closed day.
    private static readonly HeldPlanDay[] Days =
    [
        Day(6.4,
        [
            Shift(420, [.. Run("registers", 8), "break", .. Run("load_running", 6), .. Run("flex", 2)]),
            Shift(480, [.. Run("scos", 4), .. Run("non_op", 2), .. Run("flex", 1)]),
        ]),
        Day(5.1, [Shift(420, [.. Run("load_running", 10), "break", .. Run("registers", 8), "flex"])]),
        Day(3.0, [Shift(540, [.. Run("registers", 12)])], legacy: true),
        new(new BenchmarkParts { TotalBenchmark = 0, Allocations = [] }, null),
    ];

    [Fact]
    public void Is_shift_time_less_every_break_time_with_no_work_in_it_included()
    {
        Assert.Equal(5.75, HeldDayHours.PlannedHours(Days[0])); // 16 + 7 quarter-hours, the break aside
        Assert.Equal(3, HeldDayHours.PlannedHours(Days[2])); // held before the day checker: read from its tiles
        Assert.Equal(0, HeldDayHours.PlannedHours(Days[3])); // closed
        Assert.Equal(6.4, HeldDayHours.BenchmarkHours(Days[0]));
        Assert.Equal(2.8, HeldDayHours.BenchmarkHours(Days[2]), 9); // Stage 1's total without the checker
    }

    [Fact]
    public void Splits_a_day_by_group_from_its_tiles_the_rest_being_time_with_no_work()
    {
        var p = HeldDayHours.PlannedByGroup(Days[0]);
        Assert.Equal([("checkout", 3.0), ("load_running", 1.5), ("non_op", 0.5)], p.Groups.Select(e => (e.Key, e.Value)));
        Assert.Equal(0.75, p.NoWork);
        Assert.Equal(5.75, p.Total);
    }

    [Fact]
    public void Gives_the_same_planned_hours_by_day_and_by_group()
    {
        // The Review and the Dashboard read each held day through DayHours.
        var review = Days.Select(HeldDayHours.DayHours).ToList();
        Assert.Equal(13.5, review.Sum(r => r.Planned));
        Assert.Equal([2, 1, 1, 0], review.Select(r => r.Shifts));
        // Analysis adds the held days up by group: the groups and the time with no
        // work add up to the same planned hours.
        var range = HeldDayHours.RangeHours(Days);
        Assert.Equal(13.5, range.Planned);
        Vitest.CloseTo(13.5, range.Groups.Values().Sum(g => g.Planned) + range.NoWork, 9);
        Vitest.CloseTo(review.Sum(r => r.Benchmark), range.Benchmark, 9);
    }
}

public class BenchmarkSplitTests
{
    private static readonly string[] FileKeys = ["checkout", "load_running", "cleaning", "specials", "reset", "admin"];
    private static readonly string[] Groups = ["checkout", "load_running", "reset", "admin", "specials", "cleaning", "non_op"];
    /// <summary>The Plan page's Benchmark Hours rows, in its order.</summary>
    private static readonly string[] Rows = Groups;

    private sealed record Row(string Date, double Total, double[] File, double[] Shares, double Benchmark, double Worked);

    // A test store's held fortnight (made-up data, run through the engine): the
    // benchmark file, Stage 1's shares by group and the day's hours check.
    private static readonly Row[] Fortnight =
    [
        new("2026-11-23", 45.213984499999995, [11.5, 11.9553, 4, 1.440302, 6.804774, 9.515], [16, 11.0038155, 4.334115833333335, 9.481666666666666, 1.440302, 2.9540845, 0], 45.215376, 45.25),
        new("2026-11-24", 50.910075, [11.5, 12.039111, 2.666667, 8.756747, 6.858321, 6.841667], [15.25, 11.3031595, 5.218001241053848, 6.841667, 8.198702258946152, 1.8485449999999999, 2.25], 50.912513000000004, 51),
        new("2026-11-25", 41.126763, [11.5, 12.9515, 2.166667, 1.617191, 7.441237, 5.458333], [15, 12.375801, 5.818240333333334, 5.041666666666667, 1.617191, 1.2738639999999997, 0], 41.134928, 41),
        new("2026-11-26", 42.90392299999999, [11.5, 13.684978, 3.3, 1.550969, 7.90985, 4.958333], [15.5, 12.999982999999999, 6.480862999999999, 4.958333, 1.550969, 1.4137749999999998, 0], 42.904129999999995, 43),
        new("2026-11-27", 48.580532500000004, [11.5, 14.693711, 2.166667, 6.651547, 8.554314, 5.025], [16, 13.966373, 6.320320564949048, 5.025, 6.0149159350509525, 1.253923, 0], 48.591238999999995, 49.5),
        new("2026-11-28", 44.014947354195996, [10.990556, 14.724667, 2.166667, 1.725191, 9.828416, 4.791667], [14, 14.10435110224, 7.805836212144001, 4.791667, 1.725191, 1.5879020398119998, 0], 44.227164, 44),
        new("2026-11-29", 37.848260999999994, [10.5, 12.310622, 0.333333, 1.545636, 8.604807, 4.791667], [14, 11.178777988318544, 6.0376361156182305, 4.791667, 1.5068468960632246, 0.333333, 0], 38.086065000000005, 38),
        new("2026-11-30", 44.48732749999999, [11.5, 11.780389, 4, 1.440302, 6.693028, 9.075], [16, 10.8289045, 4.2223698333333335, 9.041666666666666, 1.440302, 2.9540845, 0], 44.488719, 45),
        new("2026-12-01", 51.399276, [11.5, 11.876589, 2.666667, 7.312302, 6.754489, 11.291667], [15.25, 11.140637499999999, 5.325552075000001, 10.742075875000001, 7.09246555, 1.8485449999999999, 0], 51.401714, 51.25),
        new("2026-12-02", 41.747834, [11.5, 12.720289, 2.166667, 1.617191, 7.293519, 5.458333], [15, 12.144589999999999, 5.670522333333333, 5.041666666666667, 1.617191, 1.2738639999999997, 1], 41.755999, 41.75),
        new("2026-12-03", 42.458814649999994, [11.5, 13.174411, 3.3, 1.550969, 7.583652, 4.775], [17, 12.280166249999999, 5.38321565, 4.775, 1.550969, 1.4694637499999998, 0], 41.884032, 42.75),
        new("2026-12-04", 46.7256355, [11.5, 13.962911, 2.166667, 5.994347, 8.087417, 5.025], [16, 13.235573, 5.846537924801676, 5.025, 5.364601575198325, 1.253923, 0], 46.736342, 48),
        new("2026-12-05", 43.14684500199999, [10.822, 14.376578, 2.166667, 1.622524, 9.576371, 4.791667], [13.5, 13.74277088, 7.9084413279999985, 4.791667, 1.622524, 1.5814417939999998, 0], 43.355807, 43.25),
        new("2026-12-06", 41.128947, [10.5, 14.1734, 0.333333, 1.535413, 10.032938, 4.791667], [14, 13.04491509220737, 7.456312927165088, 4.791667, 1.502718980627542, 0.333333, 0], 41.366750999999994, 41.5),
    ];

    // The weekday's trade pattern inside the store's trading hours (Mon..Sun): the
    // pattern runs 06:00-21:45 and the doors close at 19:00 on Saturday and Sunday.
    private static readonly double[] PatternInside = [0.999879, 0.999788, 0.99929, 0.999982, 0.999069, 0.980691, 0.977352];
    // A sales override on Thursday 3 December: 105%.
    private static readonly Dictionary<string, double> Override = new() { ["2026-12-03"] = 1.05 };

    private static HeldPlanDay Held(Row r)
    {
        var file = new JsMap<string, double>();
        for (var i = 0; i < FileKeys.Length; i++) file.Set(FileKeys[i], r.File[i]);
        return new HeldPlanDay(
            new BenchmarkParts
            {
                TotalBenchmark = r.Total,
                BenchmarkFile = file,
                PhMovedHours = 0,
                Allocations = Groups.Select((g, i) => (g, i))
                    .Where(x => x.g != "non_op" || r.Shares[x.i] > 0)
                    .Select(x => new GroupAllocation(x.g, r.Shares[x.i], r.Shares[x.i]))
                    .ToList(),
            },
            new PlanShiftComputation { HasData = true, Shifts = [], Flags = [], Hours = new HoursCheck { Worked = r.Worked, Benchmark = r.Benchmark } });
    }

    private static readonly HeldPlanDay[] Days = Fortnight.Select(Held).ToArray();

    private static double Stage1Total(HeldPlanDay d) => d.Result.Allocations!.Aggregate(0.0, (n, a) => n + a.BenchmarkHours);

    private static JsMap<string, double> Map(params (string Key, double Value)[] xs) => new(xs.Select(x => (x.Key, x.Value)));

    // ── the day's benchmark ──

    [Fact]
    public void DayBenchmark_is_the_file_booked_non_op_and_any_public_holiday_move()
    {
        var s1 = new BenchmarkParts
        {
            TotalBenchmark = 30.4,
            BenchmarkFile = Map(("checkout", 10), ("load_running", 8), ("cleaning", 2), ("specials", 1), ("reset", 6), ("admin", 3)),
            PhMovedHours = -0.75,
            Allocations = [new GroupAllocation("non_op", 1.5, 1.5)],
        };
        Vitest.CloseTo(30.75, BenchmarkSplit.DayBenchmark(s1), 9);
        // A day held before Stage 1 kept the file: Stage 1's own total.
        Assert.Equal(30.4, BenchmarkSplit.DayBenchmark(new BenchmarkParts { TotalBenchmark = 30.4 }));
        foreach (var d in Days) Vitest.CloseTo(d.Plan!.Hours!.Benchmark, BenchmarkSplit.DayBenchmark(d.Result), 9);
    }

    // ── the benchmark split (owner, 15/11/2026: it adds up exactly) ──

    [Fact]
    public void Keeps_Stage_1s_share_for_every_group_and_gives_Checkout_the_rest()
    {
        var d = Days[10].Result; // Thursday 3 December
        var split = BenchmarkSplit.Split(d);
        foreach (var a in d.Allocations!)
            if (a.Group != "checkout") Assert.Equal(a.BenchmarkHours, split.Get(a.Group));
        Vitest.CloseTo(16.425217, split.Get("checkout"), 6);
        Vitest.CloseTo(41.884032, split.Values().Aggregate(0.0, (a, b) => a + b), 9);
    }

    [Fact]
    public void Finds_Stage_1s_shares_off_the_days_benchmark_by_the_tills_demand()
    {
        // Stage 1 funds the till rounding above the tills' own demand: the file's
        // checkout hours over the trading quarter-hours by the trade pattern, and
        // any sales override. The rest of the difference is the tills'.
        for (var i = 0; i < Days.Length; i++)
        {
            var d = Days[i];
            var dow = Period.DowMon0(DateOnly.ParseExact(Fortnight[i].Date, "yyyy-MM-dd"));
            var checkout = d.Result.BenchmarkFile!.Get("checkout");
            var demand = checkout * PatternInside[dow] * Override.GetValueOrDefault(Fortnight[i].Date, 1);
            Vitest.CloseTo(demand - checkout, Stage1Total(d) - BenchmarkSplit.DayBenchmark(d.Result), 6);
        }
        // Before: 13 of the 14 days off; the fortnight's groups 621.69 h against 622.06 h.
        Assert.Equal(13, Days.Count(d => Math.Abs(Stage1Total(d) - BenchmarkSplit.DayBenchmark(d.Result)) >= 0.0005));
        Assert.Equal("621.69", Js.ToFixed(Days.Aggregate(0.0, (n, d) => n + Stage1Total(d)), 2));
    }

    [Fact]
    public void Adds_up_on_the_Plan_pages_day_table_and_the_Review_for_every_day()
    {
        foreach (var d in Days)
        {
            // The Plan page's Benchmark Hours table: its rows add up to its total...
            var (total, rows) = BenchmarkSplit.BenchmarkRows(d.Result, d.Plan!.Hours!.Benchmark, Rows);
            Assert.Equal(Rows, rows.Keys());
            Assert.Equal(total, rows.Values().Aggregate(0.0, (a, b) => a + b));
            // ...which is the day hours line's and the card's benchmark, and the Review's.
            Assert.Equal(Js.ToFixed(d.Plan.Hours.Benchmark, 2), BenchmarkSplit.HoursText(total));
            Assert.Equal(Js.ToFixed(HeldDayHours.DayHours(d).Benchmark, 2), BenchmarkSplit.HoursText(total));
        }
        // Analysis: the groups, non-op included, share out the fortnight's benchmark.
        var range = HeldDayHours.RangeHours(Days);
        Assert.Equal(Groups.Order(StringComparer.Ordinal), range.Groups.Keys().Order(StringComparer.Ordinal));
        Assert.Equal("622.06", Js.ToFixed(range.Benchmark, 2));
        Vitest.CloseTo(range.Benchmark, range.Groups.Values().Sum(g => g.Benchmark), 9);
        // Checkout's share is the tills' 212.50 h and the 0.37 h they were planned under it.
        var shown = BenchmarkSplit.ToHundredths(new JsMap<string, double>(range.Groups.Select(e => (e.Key, e.Value.Benchmark))), range.Benchmark);
        // (toEqual on an object: the order of the keys is not compared.)
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["checkout"] = "212.87",
                ["load_running"] = "173.35",
                ["reset"] = "83.83",
                ["admin"] = "85.14",
                ["specials"] = "42.24",
                ["cleaning"] = "21.38",
                ["non_op"] = "3.25",
            },
            shown.ToDictionary(e => e.Key, e => BenchmarkSplit.HoursText(e.Value)));
    }

    [Fact]
    public void Shows_Thursday_3_Decembers_Checkout_0_57_h_over_its_share_the_sales_override()
    {
        var d = Days[10];
        var (total, rows) = BenchmarkSplit.BenchmarkRows(d.Result, d.Plan!.Hours!.Benchmark, Rows);
        Assert.Equal(
            [
                ("checkout", "16.43"),
                ("load_running", "12.28"),
                ("reset", "5.38"),
                // 4.775 h: its half-hundredth goes to the larger remainders.
                ("admin", "4.77"),
                ("specials", "1.55"),
                ("cleaning", "1.47"),
                ("non_op", "0.00"),
            ],
            Rows.Select(k => (k, BenchmarkSplit.HoursText(rows.Get(k)))));
        Assert.Equal("41.88", BenchmarkSplit.HoursText(total));
        Assert.Equal("+0.57", BenchmarkSplit.SignedText(1700 - rows.Get("checkout")));
    }

    // ── hundredths that add up ──

    [Fact]
    public void Floors_each_figure_and_hands_the_hundredths_left_to_the_largest_remainders()
    {
        static List<(string, double)> H(JsMap<string, double> m) => m.Select(e => (e.Key, e.Value)).ToList();
        Assert.Equal([("a", 101.0), ("b", 100.0), ("c", 101.0)], H(BenchmarkSplit.ToHundredths(Map(("a", 1.006), ("b", 1.006), ("c", 1.008)), 3.02)));
        Assert.Equal([("a", 334.0), ("b", 333.0), ("c", 333.0)], H(BenchmarkSplit.ToHundredths(Map(("a", 10.0 / 3), ("b", 10.0 / 3), ("c", 10.0 / 3)), 10)));
        // Rows with no figure stay at 0; float dust doesn't cost a hundredth.
        Assert.Equal([("w", 0.0), ("x", 29.0), ("y", 71.0)], H(BenchmarkSplit.ToHundredths(Map(("x", 0.29), ("y", 0.71)), 1, ["w", "x", "y"])));
        // Parts above their total (never a split's) give hundredths back from the smallest remainders.
        Assert.Equal([("a", 49.0), ("b", 49.0)], H(BenchmarkSplit.ToHundredths(Map(("a", 0.5), ("b", 0.504)), 0.98)));
    }

    [Fact]
    public void Writes_hundredths_as_the_pages_write_hours()
    {
        Assert.Equal("41.88", BenchmarkSplit.HoursText(4188));
        Assert.Equal("+0.57", BenchmarkSplit.SignedText(57));
        Assert.Equal("-0.21", BenchmarkSplit.SignedText(-21));
        Assert.Equal("+0.00", BenchmarkSplit.SignedText(0));
    }
}

public class LoadRunningTests
{
    private const double H = 60;

    private static List<LoadRunning.Period> Periods(List<WindowRule>? windows = null, List<PlannedBlock>? blocks = null) =>
        LoadRunning.LoadRunningPeriods(
            windows ?? [new("admin", 7 * H, 17 * H), new("load_running", 6 * H + 30, 10 * H)],
            blocks ?? [new("load_running", 7 * H, 3)],
            6 * H,
            21 * H);

    [Fact]
    public void Is_Load_Runnings_own_window() => Assert.Equal([new LoadRunning.Period(6 * H + 30, 10 * H)], Periods());

    [Fact]
    public void Adds_the_evening_when_Stage_1_moved_a_public_holidays_load_into_it()
    {
        // Stage 1 puts that load after 18:00, outside the window, on purpose -
        // the only load it ever places there.
        var periods = Periods(blocks: [new("load_running", 7 * H, 3), new("load_running", 18 * H + 15, 5)]);
        Assert.Equal([new LoadRunning.Period(6 * H + 30, 10 * H), new LoadRunning.Period(18 * H, 21 * H)], periods);
    }

    [Fact]
    public void Is_the_evening_on_a_night_fill_store()
    {
        var periods = Periods([new("load_running", 18 * H, 21 * H)], [new("load_running", 19 * H, 2)]);
        Assert.Equal([new LoadRunning.Period(18 * H, 21 * H)], periods);
    }

    [Fact]
    public void Marks_a_quarter_hour_outside_every_period()
    {
        List<LoadRunning.Period> periods = [new(6 * H + 30, 10 * H)];
        Assert.True(LoadRunning.OutsidePeriods(periods, 6 * H + 15));
        Assert.False(LoadRunning.OutsidePeriods(periods, 6 * H + 30));
        Assert.False(LoadRunning.OutsidePeriods(periods, 9 * H + 45));
        Assert.True(LoadRunning.OutsidePeriods(periods, 10 * H));
        // No periods at all: every load running tile is out of place.
        Assert.True(LoadRunning.OutsidePeriods([], 8 * H));
    }
}
