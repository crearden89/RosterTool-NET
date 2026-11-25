// Port of the pure parts of src/lib/stage1/compute.ts.
// computeStage1 assembles Stage 1's inputs from the request-cached reference
// data (benchmark 90% hours, trading hours, national windows) + the Till engine,
// and runs the placement. Here the reference data arrives already loaded, as
// Stage1Sources (the data layer's loaders fill it the way lib/refdata did), and
// ComputeStage1 is everything computeStage1 did with it.

using System.Text.Json.Serialization;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;

namespace Roster.Engine.Planning.Stage1;

/// <summary>One benchmark_days row (the *_90 figures are the file's hours / 0.9).</summary>
public sealed record BenchmarkDayRow
{
    [JsonPropertyName("sales_forecast")] public double SalesForecast { get; init; }
    [JsonPropertyName("checkout_hours_90")] public double CheckoutHours90 { get; init; }
    // The un-transformed figure, kept only so the Till Preview screen can show
    // the ÷0.9 step of the derivation. Nothing plans off it.
    [JsonPropertyName("checkout_hours_100")] public double CheckoutHours100 { get; init; }
    [JsonPropertyName("lr_hours_90")] public double LrHours90 { get; init; }
    [JsonPropertyName("cleaning_hours_90")] public double CleaningHours90 { get; init; }
    [JsonPropertyName("specials_hours_90")] public double SpecialsHours90 { get; init; }
    [JsonPropertyName("reset_hours_90")] public double ResetHours90 { get; init; }
    [JsonPropertyName("admin_hours_90")] public double AdminHours90 { get; init; }
    [JsonPropertyName("employee_count")] public double? EmployeeCount { get; init; }
}

/// <summary>One store_attributes row.</summary>
public sealed record StoreAttrsRow
{
    [JsonPropertyName("has_sco")] public bool HasSco { get; init; }
    [JsonPropertyName("is_new_sco_model")] public bool IsNewScoModel { get; init; }
    [JsonPropertyName("mco_registers")] public double McoRegisters { get; init; }
    [JsonPropertyName("sco_lanes")] public double ScoLanes { get; init; }
    [JsonPropertyName("sco_efficiency")] public double ScoEfficiency { get; init; }
    [JsonPropertyName("first_step")] public double FirstStep { get; init; }
    [JsonPropertyName("sco_share")] public double ScoShare { get; init; }
    /// <summary>Night-fill store: the load is worked in the evening, not the morning.</summary>
    [JsonPropertyName("night_fill")] public bool NightFill { get; init; }
}

/// <summary>One sales_overrides row: "closed", or a new figure by percent or value.</summary>
public sealed record SalesOverrideRow
{
    [JsonPropertyName("closed")] public bool Closed { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("pct")] public double? Pct { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
}

/// <summary>One quarter-hour of a weekday's trade curve (getTradeCurveWeek).</summary>
public sealed record QuarterHourRow
{
    [JsonPropertyName("day_of_week")] public int DayOfWeek { get; init; }
    [JsonPropertyName("block_time")] public string BlockTime { get; init; } = "";
    [JsonPropertyName("allocation")] public double Allocation { get; init; }
}

/// <summary>
/// The Process → Planning Order configuration (getPlacementConfig): rules
/// normalised so every group has one, the settings, the step order, and the
/// groups with a SAVED rule (the rest are shipped defaults the page tells the
/// store "still use the even spread", so the plan must not treat them as
/// instructions).
/// </summary>
public sealed record PlacementConfig
{
    public IReadOnlyList<PlacementRule> Rules { get; init; } = Placement.NormaliseRules(null);
    public PlacementSettings Settings { get; init; } = Placement.DefaultSettings;
    public IReadOnlyList<string> StepOrder { get; init; } = WindowGroups.DefaultStepOrder;
    public IReadOnlyList<string> SavedGroups { get; init; } = [];

    public static readonly PlacementConfig Default = new();
}

/// <summary>
/// One trading quarter-hour of the till engine's result for the day (the parts
/// of TillBlockRow that Stage 1 reads).
/// </summary>
public sealed record Stage1TillBlock
{
    /// <summary>"HH:MM:SS".</summary>
    public string BlockTime { get; init; } = "";
    /// <summary>Tills planned (registers + the SCO attendant).</summary>
    public double Total { get; init; }
    /// <summary>The block's exact checkout demand, in till-units.</summary>
    public double Segment { get; init; }
    public double Mco { get; init; }
    /// <summary>Registers the demand asks for, before the register cap.</summary>
    public double McoWanted { get; init; }
}

/// <summary>What Stage 1 reads of computeTillForDate's result for the day.</summary>
public sealed record Stage1Till
{
    public IReadOnlyList<Stage1TillBlock> Blocks { get; init; } = [];
    /// <summary>Trading quarter-hours the trading pattern has no share for (owner decision 18).</summary>
    public IReadOnlyList<double> UncoveredTrading { get; init; } = [];

    public static readonly Stage1Till Empty = new();
}

/// <summary>
/// Everything computeStage1 read for one store-day, as the data layer loads it
/// (the lib/refdata getters, the non_op_entries query and the till engine's
/// result). The store-wide parts are the same for every day of a fortnight.
/// </summary>
public sealed record Stage1Sources
{
    public DateOnly Date { get; init; }
    /// <summary>getBenchmarkDay(store, date).</summary>
    public BenchmarkDayRow? Bench { get; init; }
    /// <summary>getBenchmarkDay(store, date + 1): a public holiday tomorrow moves its load into this evening.</summary>
    public BenchmarkDayRow? NextBench { get; init; }
    /// <summary>getTradingWeek(store).</summary>
    public IReadOnlyList<TradingDayRow> TradingWeek { get; init; } = [];
    /// <summary>getSpecialHours(store).</summary>
    public IReadOnlyList<SpecialHoursRow> SpecialHours { get; init; } = [];
    /// <summary>getLabourWindows(): the national labour-group windows by weekday (Mon=0).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<WindowRule>> LabourWindows { get; init; } = new Dictionary<int, IReadOnlyList<WindowRule>>();
    /// <summary>non_op_entries.minutes for the store-day.</summary>
    public IReadOnlyList<double> NonOpMinutes { get; init; } = [];
    /// <summary>getHolidaySet(store): national, state and local public holidays.</summary>
    public IReadOnlyList<DateOnly> Holidays { get; init; } = [];
    /// <summary>getPhReallocSet(store): dates whose plan moves the holiday's load to the eve.</summary>
    public IReadOnlyList<DateOnly> PhReallocDates { get; init; } = [];
    /// <summary>getSalesOverrideMap(store).get(date).</summary>
    public SalesOverrideRow? SalesOverride { get; init; }
    /// <summary>getAdminTasks(): the active catalogue, by sort order then name.</summary>
    public IReadOnlyList<AdminTaskRule> AdminTaskRules { get; init; } = [];
    /// <summary>getRegionPrefix(store): "501".."509", or null.</summary>
    public string? RegionPrefix { get; init; }
    /// <summary>getStoreAttrs(store).</summary>
    public StoreAttrsRow? Attrs { get; init; }
    /// <summary>getResetTasks(): the active catalogue, by sort order.</summary>
    public IReadOnlyList<ResetTaskRule> ResetTaskRules { get; init; } = [];
    /// <summary>getPlacementConfig().</summary>
    public PlacementConfig Placement { get; init; } = PlacementConfig.Default;
    /// <summary>getTradeCurveWeek(store): weekday (Mon=0) → that day's curve.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>> TradeCurveWeek { get; init; } = new Dictionary<int, IReadOnlyList<QuarterHourRow>>();
    /// <summary>getRuleOverrides(): the rostering_rules table (overrides only).</summary>
    public IReadOnlyDictionary<string, double> RuleOverrides { get; init; } = new Dictionary<string, double>();
    /// <summary>getPosCustomerRows(store): the four-week POS sample's customers by quarter-hour.</summary>
    public IReadOnlyList<PosCustomerRow> PosCustomers { get; init; } = [];
    /// <summary>computeTillForDate(store, date): the till engine's result for the day.</summary>
    public Stage1Till Till { get; init; } = Stage1Till.Empty;
}

/// <summary>Register demand above the store's register count, which the till engine caps (R2).</summary>
public sealed record TillCapped(double Hours, double Registers, double Start, double End);

public sealed class Stage1Computation : Stage1Result
{
    public bool HasData { get; set; }
    // Deliberately closed (special trading hours or a Sales-page override) - an
    // empty day that is CORRECT, not missing data: status pills show it green.
    public bool Closed { get; set; }
    public double TradingOpen { get; set; }
    public double TradingClose { get; set; }
    public double StaffOpen { get; set; } // pre-open prep floor (window groups + shift bounds)
    public double StaffClose { get; set; } // post-close wrap
    public List<WindowRule> Windows { get; set; } = new(); // the day's labour-group time barriers (hard limits)
    // Named administration tasks due this day, with resolved minutes + windows.
    // PLACEMENT-ONLY: the benchmark Admin hours already fund them, so they never
    // change the hours math above. Null on snapshots generated before the
    // catalogue existed.
    public List<ResolvedAdminTask>? AdminTasks { get; set; }
    // Named reset tasks (re-runs, first declare, full decard) - same
    // placement-only model, funded by the benchmark Reset hours.
    public List<ResolvedResetTask>? ResetTasks { get; set; }
    // Groups placed by a strategy the store SAVED on Process -> Planning Order
    // (switched on, and not "spread evenly"). That placement is the store's
    // instruction, so the shaper leaves it where Stage 1 put it - re-spreading
    // it made the page do nothing (Roster Engine Review 07/11/2026, RC4).
    public List<string>? StrategyGroups { get; set; }
    // Which way levelling breaks a tie for a group, from its Planning Order
    // strategy (back-fill leans late, forward-fill early): "early" or "late".
    public JsMap<string, string>? LevelLean { get; set; }
    // The benchmark file's six groups as uploaded (90% OE), before the checkout
    // borrow and any public-holiday move: the yardstick of the day's hours
    // check (owner decision R2). In the order checkout, load_running, cleaning,
    // specials, reset, admin. Null on older snapshots.
    public JsMap<string, double>? BenchmarkFile { get; set; }
    // Load Running a public-holiday reallocation moved: + on the eve (taken in
    // for the evening), - on the holiday (moved out). Part of the benchmark the
    // day is held to.
    public double? PhMovedHours { get; set; }
    // Checkout rounding the other groups could not lend (decision 10): the day
    // plans over its benchmark by this much, on purpose.
    public double? CheckoutShortfall { get; set; }
    // Register demand above the store's register count, which the till engine
    // caps (R2: flagged, not dropped silently).
    public TillCapped? TillCapped { get; set; }
}

public static class Stage1Compute
{
    // Fraction of a public holiday's Load Running moved to the prior evening when
    // PH reallocation is switched on for that date (spec §4.6).
    private const double PhReallocFraction = 0.75;

    private const double B = Grid.BlockMin;

    // Per-block set of task groups PERMITTED at that time - the labour-group time
    // barriers plus checkout inside trading hours. Stage 2 uses this so no tile is
    // ever painted outside its group's allowed window. placementOnly leaves out
    // the windows work may only be MADE UP in (the load's minutes after its
    // window) - the shaper levels work into the rest.
    public static JsMap<double, List<string>> AllowedGroupsFromStage1(Stage1Computation s1, bool placementOnly = false)
    {
        var map = new JsMap<double, List<string>>();
        for (var m = s1.StaffOpen; m < s1.StaffClose; m += B)
        {
            var gs = s1.Windows.Where(w => m >= w.Start && m < w.End && !(placementOnly && w.MakeUpOnly)).Select(w => w.Group).ToList();
            if (m >= s1.TradingOpen && m < s1.TradingClose) gs.Add(TaskGroups.Checkout);
            map.Set(m, gs);
        }
        return map;
    }

    /// <summary>
    /// The windows of the day's NAMED admin and reset tasks. The painter uses these
    /// to keep work inside the job that asked for it: a re-run displaced by a break
    /// is made up inside the re-run's own window, never in the decard's. Groups with
    /// no named tasks are absent, and the painter then falls back to the group
    /// window alone.
    /// </summary>
    public static JsMap<string, List<MinuteWindow>> TaskWindowsFromStage1(Stage1Computation s1)
    {
        var out_ = new JsMap<string, List<MinuteWindow>>();
        var admin = (s1.AdminTasks ?? []).Select(t => new MinuteWindow(t.WindowStart, t.WindowEnd)).ToList();
        var reset = (s1.ResetTasks ?? []).Select(t => new MinuteWindow(t.WindowStart, t.WindowEnd)).ToList();
        if (admin.Count > 0) out_.Set("admin", admin);
        if (reset.Count > 0) out_.Set("reset", reset);
        // The quoted Cleaning hour is a window of its own (owner decisions 10 and
        // 12): its work stays inside it, and the rest of the day's Cleaning stays
        // out of it - morning Cleaning displaced by a break was made up at 20:15,
        // and evening Cleaning topped the morning hour up (review RC-21).
        var quoted = s1.Windows.Where(w => w.Group == "cleaning" && (w.Hours ?? 0) > 0).ToList();
        if (quoted.Count > 0)
        {
            var rest = new List<MinuteWindow>();
            foreach (var w in s1.Windows.Where(x => x.Group == "cleaning" && !((x.Hours ?? 0) > 0)))
            {
                var parts = new List<MinuteWindow> { new(w.Start, w.End) };
                foreach (var q in quoted)
                {
                    parts = parts.SelectMany(p =>
                        q.End <= p.Start || q.Start >= p.End
                            ? new List<MinuteWindow> { p }
                            : new List<MinuteWindow>()
                                .Concat(q.Start > p.Start ? [new MinuteWindow(p.Start, q.Start)] : [])
                                .Concat(q.End < p.End ? [new MinuteWindow(q.End, p.End)] : [])
                                .ToList()).ToList();
                }
                rest.AddRange(parts);
            }
            out_.Set("cleaning", [.. quoted.Select(q => new MinuteWindow(q.Start, q.End)), .. rest]);
        }
        return out_;
    }

    /// <summary>
    /// The job window of each planned Admin quarter-hour (Stage1Result.UnitWindows),
    /// by time. The plan's painter makes displaced Admin up only inside the window
    /// of the job it belongs to (owner decision 11, strict for Admin).
    /// </summary>
    public static JsMap<string, JsMap<double, List<JobWindow>>> UnitWindowsFromStage1(Stage1Computation s1)
    {
        var admin = new JsMap<double, List<JobWindow>>();
        foreach (var u in s1.UnitWindows ?? [])
        {
            if (u.Group != "admin") continue;
            if (!admin.TryGet(u.Time, out var list)) admin.Set(u.Time, list = new List<JobWindow>());
            list.Add(new JobWindow(u.Start, u.End, u.Named));
        }
        var out_ = new JsMap<string, JsMap<double, List<JobWindow>>>();
        if (admin.Size > 0) out_.Set("admin", admin);
        return out_;
    }

    /// <summary>
    /// Where a window was run on into the close-down (evening Cleaning after a
    /// 21:00 close, owner 14/11/2026): the time it used to end. The shaper levels
    /// work in past it only for the people who must be on.
    /// </summary>
    public static double? CloseDownFromStage1(Stage1Computation s1)
    {
        var from = s1.Windows.Where(w => w.CloseDownFrom is not null).Select(w => w.CloseDownFrom!.Value).ToList();
        return from.Count > 0 ? Js.Min(from) : null;
    }

    /// <summary>Monday's Longlife date checks' window, when they are due (A12): a deadline for the painter.</summary>
    public static MinuteWindow? AdminDeadlineFromStage1(Stage1Computation s1)
    {
        var t = (s1.AdminTasks ?? []).FirstOrDefault(x => x.Name == "Longlife Date Checks" && x.Minutes > 0);
        return t is null ? null : new MinuteWindow(t.WindowStart, t.WindowEnd);
    }

    // How much later than 08:30 the doors open (owner decision R8): the load and
    // the morning re-runs move later by this much. Stores opening by 08:30 keep
    // today's times.
    private const double LateOpenFromMin = 8 * 60 + 30;

    public static double LateOpenBy(double tradingOpen) => Math.Max(0, tradingOpen - LateOpenFromMin);

    private static Stage1Computation Empty() => new()
    {
        Blocks = [],
        Allocations = [],
        TotalBenchmark = 0,
        TotalAllocated = 0,
        Variance = 0,
        WithinTolerance = true,
        HasData = false,
        Closed = false,
        TradingOpen = 0,
        TradingClose = 0,
        StaffOpen = 0,
        StaffClose = 0,
        Windows = [],
        Flags = [],
    };

    /// <summary>
    /// computeStage1: the day's Stage 1 placement from its loaded sources. The
    /// whole of the TypeScript function, less its reads.
    /// </summary>
    public static Stage1Computation ComputeStage1(Stage1Sources src)
    {
        var date = src.Date;
        var dow = Period.DowMon0(date);
        var bench = src.Bench;
        var day = StoreDays.ResolveStoreDay(src.TradingWeek, src.SpecialHours, src.RuleOverrides, date);
        var till = src.Till;
        var ruleOverrides = src.RuleOverrides;
        var attrs = src.Attrs;
        var holidaySet = new HashSet<DateOnly>(src.Holidays);

        // A day is closed by special trading hours OR by a Sales-page override.
        // The till engine already sheds its curve for both - if Stage 1 didn't do
        // the same, a closed day would plan every labour group EXCEPT checkout: a
        // full roster with no tills. Closed means no plan at all - and it's a
        // correct, deliberate empty, not missing data.
        if (day.Closed || src.SalesOverride?.Closed == true)
        {
            var closed = Empty();
            closed.Closed = true;
            return closed;
        }
        if (bench is null) return Empty();

        var tradingOpen = day.TradingOpen;
        var tradingClose = day.TradingClose;
        var staffOpen = day.EarliestStaffStart;
        var staffClose = day.StaffClose;

        var benchmark90 = new JsMap<string, double>()
            .Set("load_running", bench.LrHours90)
            .Set("cleaning", bench.CleaningHours90)
            .Set("specials", bench.SpecialsHours90)
            .Set("reset", bench.ResetHours90)
            .Set("admin", bench.AdminHours90);

        // Named admin tasks due today, sized by the benchmark EmployeeCount and the
        // store's till configuration, with concrete windows. Resolved BEFORE the
        // solve: the engine places admin TASK-FIRST inside each task's window.
        var adminTasks = AdminTasks.ResolveAdminTasks(src.AdminTaskRules, new AdminTaskContext
        {
            Date = date,
            RegionPrefix = src.RegionPrefix,
            EmployeeCount = bench.EmployeeCount ?? 0,
            RegisterCount = attrs?.McoRegisters ?? 0,
            ScoCount = attrs?.ScoLanes ?? 0,
            TradingOpen = tradingOpen,
            TradingClose = tradingClose,
            StaffOpen = staffOpen,
            StaffClose = staffClose,
        });

        // ── The load and the opening manager (owner decisions 3 and 4) ──────────
        // Runs once the load window is known (below), on these copies.
        WindowRule LoadAdjust(double loadStart, double loadEnd, double stretchMin)
        {
            // 3. The opening manager starts with the load runners, so nobody is in
            //    alone before the load: no Admin task starts before the load does.
            //    Monday's Longlife date checks keep the length of their window - they
            //    may run past 08:30 (decision 3 names the 07:00 load; a 06:30 one
            //    shortens the window the same way, and left 15 minutes unplanned).
            var moved = adminTasks.Select(t =>
            {
                if (t.WindowStart >= loadStart || t.WindowEnd <= loadStart) return t;
                var len = t.WindowEnd - t.WindowStart;
                var end = t.Name == "Longlife Date Checks" ? Math.Max(t.WindowEnd, loadStart + len) : t.WindowEnd;
                return t with { WindowStart = loadStart, WindowEnd = Math.Min(end, staffClose) };
            }).ToList();
            adminTasks.Clear();
            adminTasks.AddRange(moved);
            // 4. Load runners take their breaks one at a time, and the load may run
            //    on past its window by that break time: the work a break displaces
            //    is made up there, by the crew staying on, instead of by an extra
            //    three-hour body (review RC-13).
            return new WindowRule { Group = "load_running", Start = loadEnd, End = Math.Min(staffClose, loadEnd + stretchMin), MakeUpOnly = true };
        }

        // Task windows are real admin capacity: they join the national windows so
        // downstream permitted-group checks (and manual edits) accept AD tiles
        // wherever a task window opens - e.g. Register Cash-Up after close.
        //
        // Specials and evening Cleaning follow the close (owner decisions 9 and 12):
        // a window that opens later than FollowCloseMin before close opens then
        // instead. The national table holds clock times only, so on a 17:00 close
        // Specials "from 17:00" left one quarter-hour, and Stage 1 stacked the whole
        // day's Specials into it (review RC-1). The morning Cleaning hour (09:00) and
        // Tuesday and Friday's Specials (10:00) open well before, and do not move.
        // Evening Cleaning also runs on to the end of the close-down where that
        // ends after it, as a make-up-only window (owner, 14/11/2026; see
        // EveningCleaningToCloseDown).
        var national = (src.LabourWindows.TryGetValue(dow, out var byDow) ? byDow : [])
            .SelectMany(w => CloseRules.EveningCleaningToCloseDown(
                (w.Group == "specials" || w.Group == "cleaning") && w.Start > tradingClose - CloseRules.FollowCloseMin
                    ? w with { Start = CloseRules.FollowClose(w.Start, tradingClose) }
                    : w,
                tradingClose,
                staffClose))
            .ToList();
        var windows = new List<WindowRule>(national);
        windows.AddRange(adminTasks.Select(t => new WindowRule("admin", t.WindowStart, t.WindowEnd)));

        // A night-fill store works its load in the evening: its Load Running
        // windows are REPLACED by an evening window (18:00 -> staff close). Windows
        // stay hard barriers, so on these stores no load is ever planned into the
        // morning.
        if (attrs?.NightFill == true)
        {
            windows = windows.Where(w => w.Group != "load_running").ToList();
            windows.Add(new WindowRule("load_running", Math.Max(18 * 60, staffOpen), staffClose));
        }

        // ── Load Running start rule (hard) ──────────────────────────────────────
        // The day sets its OWN Load Running window from the model on Labour Group
        // Rules: load size decides how early the crew starts (<=12h -> 07:00,
        // <15h -> 06:30, otherwise 06:00), customers just after open decide how late
        // load may still run on the floor (<=quiet -> 10:00, <=busy -> 09:30, above
        // -> 09:00 with a guaranteed 3h window; everyone else gets at least 2.5h).
        // The customer figure is this weekday's average over the four-week POS
        // sample, public holidays and zero-trade dates excluded. A store with no
        // POS data falls to the quiet band - the load-size half always applies,
        // because benchmark LR hours always exist. Night-fill stores keep their own
        // evening window (the exception the model itself carves out).
        var loadStartRules = LoadStart.LoadStartRulesFrom(ruleOverrides);
        if (attrs?.NightFill != true && loadStartRules.Enabled && benchmark90["load_running"] > 0)
        {
            var customers = LoadStart.MorningCustomers(src.PosCustomers, dow, tradingOpen, loadStartRules.CustomerWindowMin, holidaySet);
            var rec = LoadStart.RecommendLoadWindow(benchmark90["load_running"], customers, loadStartRules);
            // Follow the doors (owner decision R8; review RC-34): a store opening
            // later than 08:30 runs its load later by the same amount. The model's
            // times are clock times, so an 11:00-open Sunday ran its load 06:00-09:00,
            // eight deep, with the doors shut for two more hours.
            var late = LateOpenBy(tradingOpen);
            var start = Math.Max(rec.Start + late, staffOpen);
            var end = Math.Min(rec.Finish + late, staffClose);
            if (end > start)
            {
                windows = windows.Where(w => w.Group != "load_running").ToList();
                windows.Add(new WindowRule("load_running", start, end));
            }
        }
        // Decisions 3 and 4 (LoadAdjust, above), for a morning load.
        {
            var lr = attrs?.NightFill != true ? windows.FirstOrDefault(w => w.Group == "load_running" && !w.MakeUpOnly) : null;
            if (lr is not null && lr.Start < 12 * 60)
            {
                var stretchMin = ruleOverrides.TryGetValue("loadBreakStretchMin", out var st) ? st : RosteringRules.DefaultLoadBreakStretchMin;
                var stretch = LoadAdjust(lr.Start, lr.End, stretchMin);
                if (stretch.End > stretch.Start) windows.Add(stretch);
                // The moved task windows join the barriers, as the originals did.
                windows.AddRange(adminTasks.Select(t => new WindowRule("admin", t.WindowStart, t.WindowEnd)));
            }
        }

        var checkout = till.Blocks.Select(b => new CheckoutDemand(Grid.TimeToMin(b.BlockTime), b.Total)).ToList();

        // ── Checkout rounding borrow ────────────────────────────────────────────
        // The till tier table rounds each block up on a 0.1 remainder, so the day's
        // planned till hours exceed the benchmark's checkout figure. The excess is
        // taken from the other labour groups on the client's time-of-day rules
        // rather than trimmed off checkout - `segment` is in till-units (hours x 4),
        // so both sides divide by 4 to compare as hours.
        // PH load-running reallocation (spec §4.6): keep only 25% of this day's
        // Load Running when it's a reallocated public holiday. Applied BEFORE the
        // checkout borrow - the borrow prices its headroom off these figures, and
        // multiplying afterwards meant only a quarter of any borrowed LR actually
        // funded the rounding, over-planning PH days with no flag.
        var realloc = PhLoadReallocation(src, holidaySet);
        benchmark90["load_running"] *= realloc.KeepFactor;

        var borrow = CheckoutBorrow.Compute(
            till.Blocks.Select(b => (Time: Grid.TimeToMin(b.BlockTime), PlannedHours: b.Total / 4, ExactHours: b.Segment / 4)).ToList(),
            dow,
            benchmark90,
            // The bands' boundaries are settings (R15).
            ruleOverrides.TryGetValue("borrowMorningEndMin", out var bm) && double.IsFinite(bm) ? bm : 10 * 60,
            ruleOverrides.TryGetValue("borrowEveningFromMin", out var be) && double.IsFinite(be) ? be : 18 * 60,
            // Admin's named tasks are fixed commitments for the day, so only the hours
            // it holds ABOVE them can be lent. Without this the borrow ate into work
            // the store still has to do, and Admin planned well over its benchmark.
            // The quoted Cleaning hour is protected the same way (owner decision 10;
            // review RC-10: the evening borrow took Cleaning down to a quarter-hour,
            // and the morning hour was planned with one).
            new JsMap<string, double>()
                .Set("admin", adminTasks.Aggregate(0.0, (s, t) => s + t.Minutes) / 60)
                .Set("cleaning", windows.Where(w => w.Group == "cleaning" && (w.Hours ?? 0) > 0).Aggregate(0.0, (s, w) => s + (w.Hours ?? 0))));
        foreach (var g in CheckoutBorrow.Borrowable)
            benchmark90[g] = Math.Max(0, benchmark90[g] - borrow.Borrowed[g] + borrow.Lent[g]);

        // Non-Op is additive to the hours the plan must cover (not a reallocation).
        var nonOpHours = src.NonOpMinutes.Aggregate(0.0, (s, n) => s + n) / 60;

        // Named reset tasks (re-runs, first declare, full decard), sized from the
        // day's POST-borrow Reset benchmark and windowed for this store's hours.
        // Their windows join the day's barriers like the admin task windows do, so
        // downstream permitted-group checks (and manual edits) accept RR tiles
        // wherever a reset task window opens - e.g. the decard right up to close.
        // The morning re-run follows the doors too (R8): on a late opening Re-run 1
        // and First declare piled up before the store opened, four deep.
        var resetTasks = ResetTasks.ResolveResetTasks(src.ResetTaskRules, benchmark90["reset"], new ResetTaskContext(tradingOpen, tradingClose, staffOpen, staffClose))
            .Select(t =>
            {
                var late = LateOpenBy(tradingOpen);
                // Only a window that opens before the doors do - the morning re-run and
                // first declare; a window already tied to the opening is left alone.
                if (late <= 0 || t.WindowStart >= tradingOpen) return t;
                return t with { WindowStart = Math.Min(t.WindowStart + late, staffClose), WindowEnd = Math.Min(t.WindowEnd + late, staffClose) };
            }).ToList();
        windows.AddRange(resetTasks.Select(t => new WindowRule("reset", t.WindowStart, t.WindowEnd)));

        // The store's trade curve and busy period, for every reset task's own
        // placement whatever the Planning Order switch says. The switch governs the
        // GROUP strategies on that page; a re-run set to "Avoid the busy period" on
        // Labour Group Rules used to do nothing unless someone also found the
        // switch on the other page - reset controlled from two places again
        // (Roster Engine Review 07/11/2026, P8; owner U4).
        var curveArr = (src.TradeCurveWeek.TryGetValue(dow, out var qh) ? qh : [])
            .Select(r => new CurveShare(Grid.TimeToMin(r.BlockTime), r.Allocation))
            .ToList();
        var placementCfg = src.Placement;
        var peak = Plan.Placement.FindPeak(curveArr, placementCfg.Settings);
        // Planning Order directives - resolved HERE (effective strategy, concrete
        // anchor, identified peak) so the engine stays pure. Only consulted while
        // the page's master switch is on; checkout is skipped because the till
        // engine pins it block-by-block.
        var rulesByGroup = new JsMap<string, PlacementDirective>();
        var strategyGroups = new List<string>();
        var levelLean = new JsMap<string, string>();
        if (placementCfg.Settings.Enabled)
        {
            var nightTrader = Plan.Placement.IsNightTrader(curveArr, placementCfg.Settings);
            var bounds = new DayBounds(tradingOpen, tradingClose, staffOpen, staffClose);
            foreach (var r in placementCfg.Rules)
            {
                // Checkout is imposed by the till engine and Non-Op is a booked absence:
                // neither is placed by a strategy.
                if (r.Group == "checkout" || r.Group == "non_op") continue;
                var strategy = Plan.Placement.EffectiveStrategy(r, nightTrader);
                if (strategy != "spread" && placementCfg.SavedGroups.Contains(r.Group)) strategyGroups.Add(r.Group);
                // A direction the levelling should lean, saved or the built-in default:
                // the default - Cleaning back-filled from the close - changed Stage 1 on
                // 100 of the review's 264 days and the finished plan on none, because
                // levelling broke every tie toward midday (owner decision R16; review
                // RC-40). Ties now go the way the Planning Order points.
                if (strategy == "backfill") levelLean.Set(r.Group, "late");
                else if (strategy == "forward_fill") levelLean.Set(r.Group, "early");
                var anchor = Math.Min(Math.Max(Plan.Placement.ResolveAnchor(r with { Strategy = strategy }, bounds), staffOpen), staffClose);
                rulesByGroup.Set(r.Group, new PlacementDirective(strategy, anchor, r.PrePeakPct, r.PostPeakPct, r.AvoidStrength));
            }
            // A night-fill store's load can only sit in the evening window, so its
            // directive backfills from the staffed close regardless of the global rule.
            if (attrs?.NightFill == true)
                rulesByGroup.Set("load_running", new PlacementDirective("backfill", staffClose, null, null, null));
        }
        var curve = new JsMap<double, double>();
        foreach (var c in curveArr) curve.Set(c.Time, c.Share);
        var placement = new PlacementInput { Rules = rulesByGroup, Curve = curve, PeakStart = peak?.Start, PeakEnd = peak?.End };

        var input = new Stage1Input
        {
            TradingOpen = tradingOpen,
            TradingClose = tradingClose,
            StaffOpen = staffOpen,
            StaffClose = staffClose,
            Benchmark90 = benchmark90,
            Windows = windows,
            Checkout = checkout,
            NonOpHours = nonOpHours,
            EveningLoadRunningHours = realloc.EveningInHours,
            AdminTasks = adminTasks.Select(t => new Stage1Task(t.Name, t.Minutes, t.WindowStart, t.WindowEnd)).ToList(),
            ResetTasks = resetTasks.Select(t => new Stage1Task(t.Name, t.Minutes, t.WindowStart, t.WindowEnd, t.Placement)).ToList(),
            Placement = placement,
            // Steps 1-6 in the order set on Process -> Planning Order: each group
            // fills around what the groups before it already claimed.
            StepOrder = placementCfg.StepOrder.ToList(),
            // Managers on Admin at once, and inside the handover (decision 7; R15).
            AdminDepth = new AdminDepth(
                ruleOverrides.TryGetValue("adminHeadcount", out var ah) && double.IsFinite(ah) ? ah : 1,
                ruleOverrides.TryGetValue("adminHeadcountHandover", out var ahh) && double.IsFinite(ahh) ? ahh : 2),
        };
        var result = Stage1Engine.RunStage1(input);

        // Every group was already at its floor, so the rounding excess could not be
        // funded - the day plans above its benchmark by this much.
        if (borrow.Shortfall > 0.01)
            result.Flags.Add($"Checkout rounding needs {Js.ToFixed(borrow.Shortfall, 2)}h more than the other labour groups can spare - the day plans over benchmark by that much");

        // A store with checkout demand but no tills configured renders an empty
        // Tills row with no explanation (the engine caps headcount at available
        // registers - zero available means zero planned). Name the cause instead
        // of failing silently: this is exactly the state a freshly bulk-created
        // store is in until its Store Attributes are filled out.
        if (bench.CheckoutHours90 > 0 && (attrs?.McoRegisters ?? 0) + (attrs?.ScoLanes ?? 0) == 0)
            result.Flags.Add("No tills configured: MCO Registers and SCO Lanes are both 0 on the Store Attributes screen, so checkout cannot be planned");

        // Decision 18: a till whenever the doors are open, pattern or not.
        if (till.UncoveredTrading.Count > 0)
            result.Flags.Add($"The trading pattern has no sales share for {DayHours.Spans(till.UncoveredTrading)} - one till is planned there all the same (a till whenever the doors are open)");

        var capped = till.Blocks.Where(b => b.McoWanted > b.Mco).ToList();
        var tillCapped = capped.Count > 0
            ? new TillCapped(
                capped.Aggregate(0.0, (n, b) => n + (b.McoWanted - b.Mco)) / 4,
                attrs?.McoRegisters ?? 0,
                Js.Min(capped.Select(b => Grid.TimeToMin(b.BlockTime))),
                Js.Max(capped.Select(b => Grid.TimeToMin(b.BlockTime))) + B)
            : null;

        return new Stage1Computation
        {
            Blocks = result.Blocks,
            Allocations = result.Allocations,
            TotalBenchmark = result.TotalBenchmark,
            TotalAllocated = result.TotalAllocated,
            Variance = result.Variance,
            WithinTolerance = result.WithinTolerance,
            HoursIssues = result.HoursIssues,
            UnitWindows = result.UnitWindows,
            Flags = result.Flags,
            HasData = true,
            Closed = false,
            TradingOpen = tradingOpen,
            TradingClose = tradingClose,
            StaffOpen = staffOpen,
            StaffClose = staffClose,
            Windows = windows,
            AdminTasks = adminTasks,
            ResetTasks = resetTasks,
            StrategyGroups = strategyGroups,
            LevelLean = levelLean,
            BenchmarkFile = new JsMap<string, double>()
                .Set("checkout", bench.CheckoutHours90)
                .Set("load_running", bench.LrHours90)
                .Set("cleaning", bench.CleaningHours90)
                .Set("specials", bench.SpecialsHours90)
                .Set("reset", bench.ResetHours90)
                .Set("admin", bench.AdminHours90),
            PhMovedHours = realloc.EveningInHours - bench.LrHours90 * (1 - realloc.KeepFactor),
            CheckoutShortfall = borrow.Shortfall > 0.01 ? borrow.Shortfall : 0,
            TillCapped = tillCapped,
        };
    }

    // True when `date` is a public holiday for the store AND its plan has PH
    // load-running reallocation switched on (day_plans.ph_load_reallocated).
    private static bool IsPhReallocated(Stage1Sources src, ICollection<DateOnly> holidays, DateOnly date) =>
        holidays.Contains(date) && src.PhReallocDates.Contains(date);

    // How PH reallocation affects THIS date's Load Running:
    //  - KeepFactor: multiply this date's own LR by this (0.25 when it's a
    //    reallocated PH, else 1).
    //  - EveningInHours: LR pulled IN from the next day's reallocated PH (75% of
    //    that day's benchmark LR), to be placed this evening after 18:00.
    private static (double KeepFactor, double EveningInHours) PhLoadReallocation(Stage1Sources src, ICollection<DateOnly> holidays)
    {
        var next = Period.AddDaysIso(src.Date, 1);
        var thisOn = IsPhReallocated(src, holidays, src.Date);
        var nextOn = IsPhReallocated(src, holidays, next);
        double eveningInHours = 0;
        if (nextOn) eveningInHours = src.NextBench is not null ? src.NextBench.LrHours90 * PhReallocFraction : 0;
        return (thisOn ? 1 - PhReallocFraction : 1, eveningInHours);
    }
}

/// <summary>
/// The checkout rounding borrow as Stage 1 calls it (lib/till/borrow.ts:
/// computeBorrow over borrowBandsAt, the default floor, and the committed
/// hours). The borrow itself is the till engine's (E3, Roster.Engine.Till);
/// this adapter goes once that lands.
/// </summary>
internal static class CheckoutBorrow
{
    /// <summary>BORROWABLE: the groups the checkout rounding may borrow from.</summary>
    public static readonly IReadOnlyList<string> Borrowable = ["load_running", "reset", "admin", "specials", "cleaning"];

    public sealed record Result(JsMap<string, double> Borrowed, JsMap<string, double> Lent, double Shortfall);

    public static Result Compute(
        IReadOnlyList<(double Time, double PlannedHours, double ExactHours)> blocks,
        int dowMon0,
        JsMap<string, double> available,
        double morningEndMin,
        double eveningFromMin,
        JsMap<string, double> committed) =>
        throw new NotImplementedException("Stage 1 needs the till engine's computeBorrow (Roster.Engine.Till, E3)");
}
