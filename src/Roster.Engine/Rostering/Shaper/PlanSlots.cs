// Port of the pure parts of src/lib/stage2/planSlots.ts.
// The loading around them (computeStage1, the break rules, the school day, the
// fixed rosters, the rule overrides, the task priority, the team, the request
// cache) is the data layer's: it fills DaySlotsSources and calls ComputeDaySlots,
// and fills dayCheckOptions' three reads and calls DayCheckOptions.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Painter;

namespace Roster.Engine.Rostering.Shaper;

/// <summary>A plan slot as the painter colours it: <c>PaintableShift &amp; { slot }</c>.</summary>
public sealed class PaintedSlot : PaintableShift
{
    public PlanSlot Slot { get; set; } = new();
}

/// <summary>paintSlots' result: <c>{ painted, result }</c>.</summary>
public sealed record PaintedDay(List<PaintedSlot> Painted, PaintResult<PaintedSlot> Result);

/// <summary>
/// Everything computeDaySlots read for one store-day besides its own work, as the
/// data layer loads it.
/// </summary>
public sealed record DaySlotsSources
{
    /// <summary>computeStage1(store, date).</summary>
    public required Stage1Computation Stage1 { get; init; }
    /// <summary>getBreakRules(): role → its break rule.</summary>
    public IReadOnlyDictionary<string, BreakRule> BreakRules { get; init; } = new Dictionary<string, BreakRule>();
    /// <summary>isSchoolDayFor(store, date).</summary>
    public bool IsSchoolDay { get; init; }
    /// <summary>fixedShiftsFor(store, date): the fixed-roster shifts that will actually be worked.</summary>
    public IReadOnlyList<FixedShift> FixedShifts { get; init; } = [];
    /// <summary>getRuleOverrides(): the rostering_rules table (overrides only).</summary>
    public IReadOnlyDictionary<string, double>? RuleOverrides { get; init; }
    /// <summary>getTaskPriority().</summary>
    public IReadOnlyList<string> Priority { get; init; } = TaskGroups.TaskPriority;
    /// <summary>teamHasTieredManager(store) (<see cref="PlanSlots.HasTieredManager"/>).</summary>
    public bool TeamHasTieredManager { get; init; }
}

/// <summary>computeDaySlots' result: <c>{ shaped, stage1, painted? }</c>.</summary>
public sealed record DaySlots(ShapedDay? Shaped, Stage1Computation Stage1, PaintedDay? Painted = null);

public static class PlanSlots
{
    private const double B = Grid.BlockMin;

    // The fixed-roster shifts that will ACTUALLY be worked on a date are read by
    // the data layer (fixedShiftsFor): a standing day-of-week arrangement in the
    // date's cycle week, minus anyone the day removes (approved leave, a
    // weekend-off marker, a granted day-off). The plan reserves these so the rest
    // of the curve is cut around commitments the roster has no choice but to
    // honour - see ShaperInput.FixedShifts. This is how it types each person.
    public static string SlotRoleForEmployee(string role) =>
        role == Roles.JSA ? SlotRoles.JSA : Roles.Managers.Contains(role) ? SlotRoles.MGR : SlotRoles.SA;

    // Aggregate Stage 1's per-group headcount blocks into per-block demand (total +
    // per task group) - the shape both the shaper and the task painter consume.
    public static List<DemandBlock> DemandFromStage1(IEnumerable<PlannedBlock> blocks)
    {
        var byBlock = new JsMap<double, DemandBlock>();
        foreach (var b in blocks)
        {
            var entry = byBlock.Get(b.Time) ?? new DemandBlock(b.Time, 0);
            entry.Total += b.Headcount;
            // Non-Op is generic coverage, not a directed task group: it counts toward
            // headcount but isn't handed to task assignment (painted as non_op).
            if (b.Group != TaskGroups.NonOp) entry.Groups.Set(b.Group, entry.Groups.GetOr(b.Group, 0) + b.Headcount);
            byBlock.Set(b.Time, entry);
        }
        return Js.Sorted(byBlock.Values(), (a, b) => a.Time - b.Time);
    }

    private static readonly ShaperRule DefaultMgr = new(3, 12);
    private static readonly ShaperRule DefaultSa = new(3, 9);
    private static readonly ShaperRule DefaultJsa = new(3, 6);

    // Roles the roster may put into a MGR slot (RSM is emergency-only cover, so it
    // doesn't constrain the shape).
    private static readonly string[] MgrFillRoles = [Roles.SM, Roles.ASM, Roles.DSM];

    // A manager slot must be workable by ANY of the managers who might fill it, so
    // its length is bounded by the SHORTEST of their award maxima - not the SM's.
    // Without this the shaper cut a 9.5h closer from the ASM's 12h limit, which
    // every DSM (9h) was then legally barred from taking, and the whole
    // fortnight's manager cover collapsed onto the SM and ASM.
    public static ShaperRule ManagerSlotRule(IReadOnlyDictionary<string, BreakRule> rules)
    {
        var present = MgrFillRoles.Select(r => rules.GetValueOrDefault(r)).Where(r => r is not null).Select(r => r!).ToList();
        if (present.Count == 0) return DefaultMgr;
        return new ShaperRule(
            Js.Max(present.Select(r => r.MinShiftHours)),
            Js.Min(present.Select(r => r.MaxShiftHours)),
            rules.GetValueOrDefault(Roles.ASM)?.BreakType ?? BreakTypes.Flat30Unpaid);
    }

    private static ShaperRule SaRuleOf(IReadOnlyDictionary<string, BreakRule> rules) =>
        rules.TryGetValue(Roles.SA, out var r) ? ShaperRule.From(r) : DefaultSa;

    private static ShaperRule JsaRuleOf(IReadOnlyDictionary<string, BreakRule> rules) =>
        rules.TryGetValue(Roles.JSA, out var r) ? ShaperRule.From(r) : DefaultJsa;

    // The engine role each slot type is painted and costed as: manager slots use
    // the ASM template, floor slots SA, junior slots JSA.
    public static readonly IReadOnlyDictionary<string, string> SlotEngineRole = new Dictionary<string, string>
    {
        [SlotRoles.MGR] = Roles.ASM,
        [SlotRoles.SA] = Roles.SA,
        [SlotRoles.JSA] = Roles.JSA,
    };

    // What the day checker (DayChecker) holds a store's plan day to: its
    // floor-cover rules, each slot type's longest legal shift - the same
    // figures the shaper cuts to - and whether it is a school day (a junior's
    // 5-hour cap and 16:00 start, owner decision R10).
    public static DayCheckOptions DayCheckOptions(
        IReadOnlyDictionary<string, BreakRule> rules,
        bool schoolDay,
        IReadOnlyDictionary<string, double>? ruleOverrides)
    {
        var cover = RosteringRules.Resolve(ruleOverrides);
        var mgr = ManagerSlotRule(rules);
        var sa = SaRuleOf(rules);
        var jsa = JsaRuleOf(rules);
        return new DayCheckOptions
        {
            Cover = new Planning.Plan.CoverRules(cover.MinPeopleOnFloor, cover.MinAdultsWithJsa),
            MaxClock = new Dictionary<string, double>
            {
                [SlotRoles.MGR] = Breaks.MaxClockMinutes(mgr.BreakType ?? BreakTypes.Flat30Unpaid, mgr.MaxShiftHours),
                [SlotRoles.SA] = Breaks.MaxClockMinutes(sa.BreakType ?? BreakTypes.Tiered, sa.MaxShiftHours),
                [SlotRoles.JSA] = Math.Min(Breaks.MaxClockMinutes(jsa.BreakType ?? BreakTypes.Tiered, jsa.MaxShiftHours), (schoolDay ? 5 : 6) * 60),
            },
            MinShiftMin = 180,
            ManagerMinShiftMin = cover.ManagerMinShiftMin,
            IsSchoolDay = schoolDay,
        };
    }

    /// <summary>
    /// Paint a day's plan slots with its tasks - the painter the roster uses, with
    /// the day's windows, task windows and cover rules. The Task view shows this;
    /// ComputeDaySlots also runs it as a trial, to check its shape against it.
    /// </summary>
    /// <param name="managerRole">
    /// Paint the manager slots as this role instead (a what-if: the roster may
    /// fill a manager slot with someone owed different breaks).
    /// </param>
    public static PaintedDay PaintSlots(
        ShapedDay shaped,
        Stage1Computation stage1,
        IReadOnlyList<string> priority,
        IReadOnlyDictionary<string, double>? ruleOverrides = null,
        string? managerRole = null)
    {
        var allowed = Stage1Compute.AllowedGroupsFromStage1(stage1);
        var rules = RosteringRules.Resolve(ruleOverrides);
        var painted = shaped.Slots.Select(slot => new PaintedSlot
        {
            Slot = slot,
            Role = !string.IsNullOrEmpty(managerRole) && slot.Role == SlotRoles.MGR ? managerRole : SlotEngineRole[slot.Role],
            Start = slot.Start,
            End = slot.End,
            Blocks = [],
            Restricted = [],
            // A fixed roster's slot keeps its times: the roster has to honour them.
            Fixed = slot.Id.StartsWith("fixed-", StringComparison.Ordinal),
        }).ToList();
        var result = TaskPainter.PaintTasks(painted, shaped.DemandByBlock, priority, t => allowed.GetOr(t, []), new PaintOptions
        {
            // Breaks done by 18:00 unless the store trades to 21:00 (operating rule).
            BreakEndBy = stage1.TradingClose >= 21 * 60 ? null : 18 * 60,
            TaskWindows = Stage1Compute.TaskWindowsFromStage1(stage1),
            UnitWindows = Stage1Compute.UnitWindowsFromStage1(stage1),
            Cover = new PaintCover(rules.MinPeopleOnFloor, rules.MinAdultsWithJsa),
            Handover = new PaintHandover(rules.OverlapMinMin, rules.ManagerMinShiftMin),
            AdminDeadline = Stage1Compute.AdminDeadlineFromStage1(stage1),
        });
        return new PaintedDay(painted, result);
    }

    /// <summary>
    /// Whether the store's active team has a manager owed a store assistant's
    /// tiered breaks (an RSM), who the roster may put in a manager slot.
    /// </summary>
    public static bool HasTieredManager(IEnumerable<(string Role, bool Active)> team) =>
        team.Any(e => e.Active && Roles.Managers.Contains(e.Role) && Breaks.BreakTypeForRole(e.Role) == BreakTypes.Tiered);

    // Work the painter could not place, in the groups break cover can rescue:
    // not tills (3b-ter covers those), not admin (a manager's work), not non-op.
    private static JsMap<string, double> LostToBreaks(PaintResult<PaintedSlot> result)
    {
        var out_ = new JsMap<string, double>();
        foreach (var l in result.Lost)
        {
            if (l.Group == TaskGroups.NonOp || l.Group == TaskGroups.Checkout || l.Group == TaskGroups.Admin) continue;
            out_.Set(l.Group, l.Blocks);
        }
        return out_;
    }

    private static double Total(JsMap<string, double> m)
    {
        double n = 0;
        foreach (var v in m.Values()) n += v;
        return n;
    }

    private sealed class Shape
    {
        public required ShapedDay Shaped;
        public required PaintedDay Painted;
        public required JsMap<string, double> Lost;
        public required ShaperInput Input;
    }

    // Shift time with no work, per slot: each quarter-hour of a painted shift
    // with no work on it (a 'flex' tile is not work).
    private static List<SlotTime> BlankAt(PaintedDay p)
    {
        var out_ = new List<SlotTime>();
        foreach (var s in p.Painted)
        {
            var worked = new JsSet<double>(s.Blocks.Where(b => b.Group != TaskGroups.Flex).Select(b => b.Time));
            for (var t = s.Start; t < s.End; t += 15)
                if (!worked.Has(t)) out_.Add(new SlotTime(s.Slot.Id, t));
        }
        return out_;
    }

    /// <summary>
    /// The day's PLAN SLOTS - the single source of shift structure for BOTH the
    /// Task view and the fortnight roster (the roster fills these slots with real
    /// names; it never re-derives shapes). The data layer caches it: one shape per
    /// (store, date) per request, so the two surfaces can never disagree.
    /// The painted day is the trial below - the Task view's paint, so it is kept
    /// rather than painted again.
    /// </summary>
    public static DaySlots ComputeDaySlots(DaySlotsSources src)
    {
        var stage1 = src.Stage1;
        if (!stage1.HasData) return new DaySlots(null, stage1);
        var rules = src.BreakRules;
        var schoolDay = src.IsSchoolDay;
        var ruleOverrides = src.RuleOverrides;
        var priority = src.Priority;
        var allowed = Stage1Compute.AllowedGroupsFromStage1(stage1);
        var placeable = Stage1Compute.AllowedGroupsFromStage1(stage1, placementOnly: true);
        // Evening Cleaning run on into the close-down (owner, 14/11/2026): its
        // window there is make-up-only, so the first shape levels no work into
        // that half-hour, as before; a second one, below, does - for the second
        // person kept in - and is kept only if the painted day is better for it on
        // every count.
        var closeDownFrom = Stage1Compute.CloseDownFromStage1(stage1);
        var pinned = new JsSet<string>();
        if ((stage1.AdminTasks?.Count ?? 0) > 0) pinned.Add(TaskGroups.Admin);
        if ((stage1.ResetTasks?.Count ?? 0) > 0) pinned.Add(TaskGroups.Reset);
        foreach (var g in stage1.StrategyGroups ?? []) pinned.Add(g);
        var input = new ShaperInput
        {
            AllowedAt = t => allowed.GetOr(t, []),
            PlaceAt = t => placeable.GetOr(t, []),
            StaffOpen = stage1.StaffOpen,
            StaffClose = stage1.StaffClose,
            TradingOpen = stage1.TradingOpen,
            TradingClose = stage1.TradingClose,
            IsSchoolDay = schoolDay,
            DemandByBlock = DemandFromStage1(stage1.Blocks),
            // Named admin / reset jobs carry their own deadlines, and a group placed
            // by a saved Planning Order strategy is where the store asked for it, so
            // Stage 1's placement of them is the answer - the shaper must not level
            // them out of it.
            PinnedGroups = pinned.ToList(),
            LevelLean = stage1.LevelLean,
            QuotedWindows = stage1.Windows
                .Where(w => (w.Hours ?? 0) > 0)
                .Select(w => new QuotedWindow(w.Group, w.Start, w.End, w.Hours!.Value))
                .ToList(),
            MgrRule = ManagerSlotRule(rules),
            SaRule = SaRuleOf(rules),
            JsaRule = JsaRuleOf(rules),
            FixedShifts = src.FixedShifts,
            Rules = ruleOverrides,
        };

        // Check the shape against the painter itself. The shaper buys break cover
        // from an estimate of where breaks will land; the painter decides. Where a
        // trial paint still loses work to breaks - the morning load, above all,
        // which the store asked to have a body added for rather than lose (U2) -
        // the shaper is told how much, and buys that much more. The floors build
        // up round by round (covering the load can move capacity off the re-runs,
        // and the next round covers those too), and the best shape seen is kept.
        Shape ShapeRounds(ShaperInput inp)
        {
            var shaped = PlanShaper.ShapePlan(inp);
            var painted = PaintSlots(shaped, stage1, priority, ruleOverrides);
            var lost = LostToBreaks(painted.Result);
            var best = new Shape { Shaped = shaped, Painted = painted, Lost = lost, Input = inp };
            var minBreakSlack = inp.MinBreakSlack?.Copy() ?? new JsMap<string, double>();
            for (var round = 0; round < 3 && Total(lost) > 0; round++)
            {
                // The slack this shape had where work was lost, plus what was lost.
                foreach (var (g, n) in lost)
                {
                    var had = shaped.BreakSlack?.GetOr(g, 0) ?? 0;
                    minBreakSlack.Set(g, Math.Max(minBreakSlack.GetOr(g, 0), had + n));
                }
                var roundInput = inp with { MinBreakSlack = minBreakSlack.Copy() };
                shaped = PlanShaper.ShapePlan(roundInput);
                painted = PaintSlots(shaped, stage1, priority, ruleOverrides);
                lost = LostToBreaks(painted.Result);
                if (Total(lost) < Total(best.Lost)) best = new Shape { Shaped = shaped, Painted = painted, Lost = lost, Input = roundInput };
            }
            return best;
        }

        // How a painted day stands, by the day checker: work on nobody's shift,
        // errors besides it, shift time with no work.
        var checkOpts = DayCheckOptions(rules, schoolDay, ruleOverrides);
        BreakTimeScore ScoreOf(PaintedDay p, double addedMin)
        {
            var shifts = p.Painted.Select(s => new CheckedShift
            {
                PlanRole = s.Slot.Role,
                SlotId = s.Slot.Id,
                Start = s.Start,
                End = s.End,
                Blocks = s.Blocks.Select(b => (ITile)new CheckedBlock(b.Time, b.Group)).ToList(),
            }).ToList();
            var c = DayChecker.CheckPlanDay(stage1, shifts, checkOpts with { BreakTimeAdded = addedMin / 60 });
            double blank = 0;
            foreach (var s in shifts)
            {
                var worked = new JsSet<double>(s.Blocks.Where(b => b.Group != TaskGroups.Flex).Select(b => b.Time));
                for (var t = s.Start; t < s.End; t += 15)
                    if (!worked.Has(t)) blank++;
            }
            return new BreakTimeScore(
                c.LostAt.Count,
                c.LostAt,
                c.Flags.Count(f => ReviewChecks.SeverityOf(f) == Severity.Error && !(f.Contains("on nobody's shift") || f.Contains("has nobody on it"))),
                blank);
        }

        // Shift time with no work (owner decisions 3 and 5). Where the painted day
        // still has some, cut it again letting the shaper move an assistant's shift
        // later or earlier instead of standing it idle (ShaperInput.SlideIdle), and
        // keep that shape only if it is better on every count: less shift time with
        // no work, no more work lost, no new error, no more hours worked or paid.
        static WorkedPaid Hours(PaintedDay p)
        {
            double worked = 0;
            double paid = 0;
            foreach (var s in p.Painted)
            {
                var brk = Breaks.BreakForRole(s.Role, (s.End - s.Start) / 60);
                worked = worked + s.End - s.Start - brk.TotalMinutes;
                paid = paid + s.End - s.Start - brk.UnpaidMinutes;
            }
            return new WorkedPaid(worked, paid);
        }
        // Nothing moved: the same shifts would paint the same day.
        static bool Same(List<PlanSlot> a, List<PlanSlot> b) =>
            a.Count == b.Count && a.Select((x, i) => x.Id == b[i].Id && x.Start == b[i].Start && x.End == b[i].End).All(ok => ok);
        // A painted day better on every count than another: less shift time with no
        // work, and no more work lost, errors, hours worked or hours paid.
        bool BetterDay(PaintedDay p, PaintedDay than)
        {
            var now = ScoreOf(p, 0);
            var was = ScoreOf(than, 0);
            var h1 = Hours(p);
            var h0 = Hours(than);
            return now.Blank < was.Blank && now.Lost <= was.Lost && now.Errors <= was.Errors && h1.Worked <= h0.Worked && h1.Paid <= h0.Paid;
        }

        // Evening Cleaning run on into the close-down: Cleaning may be levelled
        // into it for the people who must be on (ShaperInput.CloseDownFrom).
        IReadOnlyList<string> ClosingPlaceAt(double t)
        {
            var gs = new List<string>(placeable.GetOr(t, []));
            if (closeDownFrom is { } cd && t >= cd && allowed.GetOr(t, []).Contains(TaskGroups.Cleaning)) gs.Add(TaskGroups.Cleaning);
            return gs;
        }
        // Painting a large store's day takes seconds, so on such a close the
        // close-down shape is tried first: when its painted day is flawless - no
        // shift time with no work, nothing lost, no error - on fewer hours than the
        // usual shape's shifts, it is better on every count and is kept without
        // painting the usual shape at all.
        Shape? flawless = null;
        if (closeDownFrom is not null)
        {
            var closingInput = input with { PlaceAt = ClosingPlaceAt, CloseDownFrom = closeDownFrom };
            var usual = PlanShaper.ShapePlan(input);
            var shaped = PlanShaper.ShapePlan(closingInput);
            var h0 = HandoverCut.SlotHours(usual.Slots);
            var h1 = HandoverCut.SlotHours(shaped.Slots);
            if (!Same(shaped.Slots, usual.Slots) && h1.Worked <= h0.Worked && h1.Paid <= h0.Paid && (h1.Worked < h0.Worked || h1.Paid < h0.Paid))
            {
                var painted = PaintSlots(shaped, stage1, priority, ruleOverrides);
                var sc = ScoreOf(painted, 0);
                if (sc.Blank == 0 && sc.Lost == 0 && sc.Errors == 0)
                    flawless = new Shape { Shaped = shaped, Painted = painted, Lost = LostToBreaks(painted.Result), Input = closingInput };
            }
        }
        var best = flawless ?? ShapeRounds(input);

        // Evening Cleaning in the close-down (owner, 14/11/2026; decisions 2 and 3).
        // Otherwise, where the usual shape leaves shift time with no work in the
        // close-down, shape the day again letting levelling give the second person
        // kept in Cleaning there, with trial rounds of its own, and keep it only if
        // the painted day is better on every count. Levelling into the close-down
        // always could reshape a quiet evening for the worse.
        if (flawless is null && closeDownFrom is { } cdf && best.Painted.Result.Blanks.Any(b => b.Times.Any(t => t >= cdf)))
        {
            var closing = ShapeRounds(input with { PlaceAt = ClosingPlaceAt, CloseDownFrom = closeDownFrom });
            if (!Same(closing.Shaped.Slots, best.Shaped.Slots) && BetterDay(closing.Painted, best.Painted)) best = closing;
        }

        if (best.Painted.Result.Blanks.Count > 0)
        {
            var was = ScoreOf(best.Painted, 0);
            var h0 = Hours(best.Painted);
            // The moved shape gets the same trial rounds as the first: where its
            // paint loses work to breaks, the shaper buys that much more cover.
            var slidInput = best.Input with { SlideIdle = true };
            for (var round = 0; round < 3; round++)
            {
                var slid = PlanShaper.ShapePlan(slidInput);
                if (round == 0 && Same(slid.Slots, best.Shaped.Slots)) break;
                var slidPainted = PaintSlots(slid, stage1, priority, ruleOverrides);
                var now = ScoreOf(slidPainted, 0);
                var h1 = Hours(slidPainted);
                if (now.Blank < was.Blank && now.Lost <= was.Lost && now.Errors <= was.Errors && h1.Worked <= h0.Worked && h1.Paid <= h0.Paid)
                {
                    best.Shaped = slid;
                    best.Painted = slidPainted;
                    best.Lost = LostToBreaks(slidPainted.Result);
                    break;
                }
                var slidLost = LostToBreaks(slidPainted.Result);
                if (Total(slidLost) == 0) break;
                var more = slidInput.MinBreakSlack?.Copy() ?? new JsMap<string, double>();
                foreach (var (g, n) in slidLost)
                {
                    var had = slid.BreakSlack?.GetOr(g, 0) ?? 0;
                    more.Set(g, Math.Max(more.GetOr(g, 0), had + n));
                }
                slidInput = slidInput with { MinBreakSlack = more };
            }
        }

        // The handover hour with less work than the two managers (owner,
        // 14/11/2026; decisions 3 and 5): where a manager still has no work inside
        // the handover, an assistant beside it leaves earlier or starts later and
        // the managers carry the work its edge carried - kept only if the painted
        // day is better on every count (see HandoverCut).
        double TillsAt(double t) => best.Shaped.DemandByBlock.FirstOrDefault(d => d.Time == t)?.GroupCount(TaskGroups.Checkout) ?? 0;
        var blanksNow = best.Painted.Result.Blanks.SelectMany(b => b.Times.Select(time => new SlotTime(b.Shift.Slot.Id, time))).ToList();
        if (HandoverCut.WantsHandoverCut(best.Shaped.Slots, blanksNow, TillsAt))
        {
            HandoverCutScore ScoreCut(PaintedDay p)
            {
                var sc = ScoreOf(p, 0);
                return new HandoverCutScore(sc.Lost, sc.Errors, sc.Blank, BlankAt(p));
            }
            var cut = HandoverCut.CutBesideHandover(
                best.Shaped.Slots,
                new HandoverCutLimits
                {
                    StaffOpen = stage1.StaffOpen,
                    StaffClose = stage1.StaffClose,
                    MaxClock = checkOpts.MaxClock ?? new Dictionary<string, double>(),
                    IsSchoolDay = schoolDay,
                    MinShiftMin = checkOpts.MinShiftMin ?? 180,
                    TillsAt = TillsAt,
                },
                slots =>
                {
                    var p = PaintSlots(best.Shaped with { Slots = slots }, stage1, priority, ruleOverrides);
                    return new ScoredPaint<PaintedDay, HandoverCutScore>(p, ScoreCut(p));
                },
                new ScoredPaint<PaintedDay, HandoverCutScore>(best.Painted, ScoreCut(best.Painted)),
                // Only where the team has a manager owed a store assistant's breaks
                // (an RSM) who could be rostered into a manager slot.
                src.TeamHasTieredManager
                    ? slots => ScoreOf(PaintSlots(best.Shaped with { Slots = slots }, stage1, priority, ruleOverrides, Roles.RSM), 0).Lost
                    : null);
            if (cut.Changes.Count > 0)
            {
                best.Shaped = best.Shaped with { Slots = cut.Slots };
                best.Painted = cut.Painted;
                best.Lost = LostToBreaks(cut.Painted.Result);
            }
        }

        // Time added so breaks don't cost work (master plan decision 4.2, the
        // owner's decision of 14/11/2026). Where the day checker still finds work
        // on nobody's shift, lengthen a shift by a quarter- or half-hour at the end
        // that helps, paint again and keep what saves the most work for the least
        // time - up to the day's allowance (the setting; 0 turns it off). The
        // roster fills these same slots, so it gets the time too.
        var rostering = RosteringRules.Resolve(ruleOverrides);
        if (rostering.BreakTimeAddMaxMin > 0)
        {
            static double Length(IEnumerable<PlanSlot> slots)
            {
                double n = 0;
                foreach (var s in slots) n = n + s.End - s.Start;
                return n;
            }
            var baseMin = Length(best.Shaped.Slots);
            var first = ScoreOf(best.Painted, 0);
            if (first.Lost > 0)
            {
                var added = BreakTime.AddTimeForBreaks(
                    best.Shaped.Slots,
                    new BreakTimeLimits
                    {
                        StaffOpen = stage1.StaffOpen,
                        StaffClose = stage1.StaffClose,
                        MaxClock = checkOpts.MaxClock ?? new Dictionary<string, double>(),
                        IsSchoolDay = schoolDay,
                        BudgetMin = rostering.BreakTimeAddMaxMin,
                    },
                    slots =>
                    {
                        var p = PaintSlots(best.Shaped with { Slots = slots }, stage1, priority, ruleOverrides);
                        return new ScoredPaint<PaintedDay, BreakTimeScore>(p, ScoreOf(p, Length(slots) - baseMin));
                    },
                    new ScoredPaint<PaintedDay, BreakTimeScore>(best.Painted, first));
                var note = BreakTime.BreakTimeNote(added.Changes);
                if (!string.IsNullOrEmpty(note))
                {
                    best.Shaped = best.Shaped with { Slots = added.Slots, Flags = [.. best.Shaped.Flags, note] };
                    best.Painted = added.Painted;
                }
            }
        }
        return new DaySlots(best.Shaped, stage1, best.Painted);
    }
}
