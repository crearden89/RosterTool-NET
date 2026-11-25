// Port of src/lib/stage2/fortnight.ts (the entry point, the cross-day rules and
// the phase runner; the fill, repair, painting and contract checks are in the
// FortnightEngine.*.cs files beside this one).
// ─── Stage 2: Fortnight Roster - PLAN-SLOT FILLER (interview model) ──────────
// The roster no longer re-solves shift shapes from demand: the PLAN (shaper)
// is the single source of shift structure, and this engine's job is to put real
// named employees into those slots across all 14 days, honouring contracts and
// every cross-day rule. The plan's shapes are followed as closely as possible;
// re-shaping is a LAST RESORT and only ever cost-neutral + coverage-preserving:
//   - SPLIT a slot (6:00-15:00 → 6:00-11:00 + 11:00-15:00) so two part-
//     contract people can each take a piece;
//   - MERGE two adjacent same-role slots so one person covers both when there
//     aren't enough bodies.
// Never slide coverage off a block that needs it.
//
// Fill priorities (interview):
//   MGR slots  ← SM/ASM first (best leaders in charge), then DSM, RSM last
//                (the RSM is a store assistant stepping up only when no core
//                manager can cover - it earns the sole-manager allowance for
//                exactly those blocks).
//   SA slots   ← adults; the SM/ASM/DSM cohort is filled to CONTRACT here
//                first, because two MGR slots a day cannot carry a manager's
//                contract. Then store assistants (incl. the RSM), biggest
//                contract to smallest. Never a JSA.
//   JSA slots  ← JSAs (adults may down-cover). A JSA never moves up.
//   Contracts: managers to the full, then biggest contracts filtering down, so
//   any residual shortfall lands on the SMALLEST contracts - the ones the
//   business can put on leave. Over-resourced weeks (rosterable adult
//   contracts ≥ plan hours): JSAs are not used at all. Under-resourced weeks:
//   contracts first → JSA flex-up (cheaper) → adults above contract (salaried
//   SM/ASM first - no OT cost - then even OT spread).
//
// Shift maxima are PAID hours, not clock: see MaxClockMinutes in Pay/Breaks.
// Unfillable slots become visible UNFILLED rows + flags (flag-don't-force).

using System.Globalization;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

public static partial class FortnightEngine
{
    private const double BLOCK_MIN = Grid.BlockMin;

    /// <summary>The week-sized share of a fortnightly contract, for per-week ledger maths.</summary>
    private static double WeeklyContractOf(FortnightEmp e) => e.ContractHours / 2;

    private static double ToBlocks(double hours) => Js.Round(hours * (60 / BLOCK_MIN));

    private static Ledger NewLedger() => new();

    // Paid (contract-counting) hours of a shift = clock span minus the unpaid break.
    internal static double PaidHoursOf(string role, BreakRule rule, double start, double end)
    {
        var lenH = (end - start) / 60;
        var unpaid = Breaks.BreakForShift(rule.BreakType, lenH).UnpaidMinutes;
        return (end - start - unpaid) / 60;
    }

    // The role's weekly cap (Break & Work rules page): paid hours in a Mon-Sun
    // week. Nothing in the roster used it, so in a store whose plans need more
    // hours than its contracts hold people were rostered past it.
    private static bool OverWeeklyCap(BreakRule rule, double weekPaid) =>
        rule.WeeklyCap is { } cap && weekPaid > cap + 1e-9;

    private static double TotalBreakMinutes(BreakRule rule, double start, double end) =>
        Breaks.BreakForShift(rule.BreakType, (end - start) / 60).TotalMinutes;

    // JSA legal window (spec §6.2): 06:00-21:00 ABSOLUTE, close-only on school
    // days, length caps. The bounds are NOT clipped to trading hours - a junior may
    // work pre-open (unloading a delivery from 06:00 is a normal junior shift) and
    // through the post-close pack-down. Clipping the morning to tradingOpen was a
    // bug: it barred juniors from every early slot and left them unfilled while
    // three juniors sat idle. Slots never run outside staff hours anyway, so the
    // absolute window is the only bound this needs.
    private static JsaWindow JsaWindow(bool isSchoolDay) => RosteringRules.JsaWindowFor(R, isSchoolDay);

    private static string Fmt(double min) => Grid.MinToTime(min);

    private static string Ddmm(string dateIso) => $"{dateIso.Substring(8, 2)}/{dateIso.Substring(5, 2)}";

    /// <summary>A number as JavaScript's String(number) writes it, for flag text.</summary>
    private static string N(double x) => Js.NumberToString(x);

    private static string N(int x) => x.ToString(CultureInfo.InvariantCulture);

    // ─── Cross-day hard constraints (spec §6.2) ──────────────────────────────────
    // Values live in RosteringRules (Default) and may be overridden per-store via
    // the Rostering Rules admin page. The engine is fully synchronous, so an
    // active-rules value set at the RunFortnightStage2 entry point is safe - no
    // await ever interleaves two runs - and saves threading the object through
    // every closure below. It is per thread here (the TypeScript's module
    // variable), so tests running side by side never see each other's rules.
    [ThreadStatic] private static RosteringRules? _rules;
    [ThreadStatic] private static JsSet<int>? _termWeeks;

    /// <summary>The rules of the latest run on this thread (the defaults before any).</summary>
    internal static RosteringRules R => _rules ?? RosteringRules.Default;

    private const double DAY_MIN = 24 * 60;

    // SM/ASM are exempt from the 11-hour rest gap (spec §6.2).
    private static bool RestGapExempt(string role) => role is Roles.SM or Roles.ASM;

    // The consecutive-day run limit applying to this role. The salaried SM/ASM -
    // already rest-gap exempt - carry their own cap, defaulting to the whole
    // fortnight: they work as many days in a row as the store needs. Capping them
    // at six cost the ASM a whole day (and with it ~9h of a 45h/wk contract)
    // whenever a run straddled the week boundary.
    private static double MaxConsecFor(string role) => RestGapExempt(role) ? R.MaxConsecDaysSalaried : R.MaxConsecDays;

    private static bool IsAdult(string role) => role != Roles.JSA;

    // Consecutive worked days immediately before `di`, read from the ledger.
    public static int ConsecutiveBefore(Ledger led, int di)
    {
        var c = 0;
        for (var d = di - 1; d >= 0 && led.ShiftEndByDay.Has(d); d--) c++;
        return c;
    }

    // Days already worked earlier in this Mon-Sun week (indices before `di`).
    public static int DaysWorkedInWeekBefore(Ledger led, int di, int weekIndex)
    {
        var c = 0;
        for (var d = weekIndex * 7; d < di; d++) if (led.ShiftEndByDay.Has(d)) c++;
        return c;
    }

    // Order-independent variant: the phase engine fills days OUT of date order
    // (Saturday before Monday), so "worked days this week" must count the whole
    // Mon-Sun week except today - counting only the days BEFORE today silently
    // missed a Saturday already placed when Monday was being filled.
    public static int DaysWorkedInWeek(Ledger led, int weekIndex, int excludeDi)
    {
        var c = 0;
        for (var d = weekIndex * 7; d < weekIndex * 7 + 7; d++)
        {
            if (d != excludeDi && led.ShiftEndByDay.Has(d)) c++;
        }
        return c;
    }

    // Consecutive worked days immediately after `di` (order-independent fills can
    // have later days already placed).
    private static int ConsecutiveAfter(Ledger led, int di)
    {
        var c = 0;
        for (var d = di + 1; led.ShiftEndByDay.Has(d); d++) c++;
        return c;
    }

    // Cross-day availability: may this employee start a shift today at all?
    // Both checks look in BOTH directions so an out-of-order fill can never build
    // an illegal run or an over-cap week through a gap it filled later.
    private static bool CanWorkToday(string role, Ledger led, int di, int weekIndex)
    {
        if (ConsecutiveBefore(led, di) + ConsecutiveAfter(led, di) + 1 > MaxConsecFor(role)) return false;
        if (DaysWorkedInWeek(led, weekIndex, di) >= R.MaxWorkedPerWeek) return false;
        return true;
    }

    // Earliest legal start today given the 11-hour rest gap after yesterday's shift.
    public static double RestGapEarliestStart(string role, Ledger led, int di)
    {
        if (RestGapExempt(role)) return double.NegativeInfinity;
        if (!led.ShiftEndByDay.TryGet(di - 1, out var prevEnd)) return double.NegativeInfinity;
        return R.RestGapMin - DAY_MIN + prevEnd;
    }

    // Remaining weekly JSA hours (15h in a term week, 30h in a holiday week).
    // The junior WEEKLY cap belongs to the week, not the day. A term week's Saturday
    // is not a school day, but the week is still a term week and the 15h cap still
    // binds (interview: "JSA weekly cap 15h (term) / 30h (holiday)"). Choosing the
    // cap from the day let weekend shifts be checked against the 30h holiday cap,
    // and a junior reached 20h in a 15h term week. A Mon-Sun week counts as term
    // when any day in it is a school day. (The per-SHIFT maximum is a per-day rule
    // - "5h (school) / 6h (non-school)" - and stays with JsaWindowFor.)
    private static JsSet<int> TermWeeks => _termWeeks ??= new JsSet<int>();

    public static JsSet<int> TermWeeksOf(IEnumerable<DayContext> days) =>
        TermWeeksOf(days.Select(d => (d.WeekIndex, d.IsSchoolDay)));

    public static JsSet<int> TermWeeksOf(IEnumerable<(int WeekIndex, bool IsSchoolDay)> days) =>
        new(days.Where(d => d.IsSchoolDay).Select(d => d.WeekIndex));

    public static double JsaWeekCapH(bool termWeek) => termWeek ? R.JsaTermWeekCapH : R.JsaHolidayWeekCapH;

    /// <summary>termWeek defaults to whether the day's week is a term week in the latest run.</summary>
    public static double JsaWeeklyRemaining(DayContext day, Ledger led, bool? termWeek = null) =>
        JsaWeekCapH(termWeek ?? TermWeeks.Has(day.WeekIndex)) - led.PaidHoursByWeek[day.WeekIndex];

    // Manager preference for MGR slots: SM/ASM (best leaders, no marginal cost),
    // then DSM, RSM last (its sole-manager allowance is the priciest cover).
    private static double MgrSlotRank(string role) => role switch
    {
        Roles.SM => 0,
        Roles.ASM => 0,
        Roles.DSM => 1,
        Roles.RSM => 2,
        Roles.SA => 9,
        Roles.JSA => 9,
        _ => double.NaN,
    };

    private static bool Salaried(string role) => role is Roles.SM or Roles.ASM;

    // The real management cohort. The RSM is NOT one of them: it is a store
    // assistant on store-assistant rules, who steps up to a manager shift only
    // when no core manager can cover it - and earns the sole-manager allowance for
    // exactly those blocks. So the RSM queues with the floor by contract size,
    // doesn't consume the extra-floor-manager allowance, and doesn't jump the
    // contract-fill queue ahead of the SAs it is paid alongside.
    //
    // This cohort is what the two rules below key off:
    //   - contracts filled to the full BEFORE any store assistant gets hours;
    //   - counted against the one-extra-manager-on-the-floor-a-day cap.
    private static bool CoreManager(string role) => role is Roles.SM or Roles.ASM or Roles.DSM;

    private static double CmpTuple(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var n = Math.Max(a.Count, b.Count);
        for (var i = 0; i < n; i++)
        {
            var d = (i < a.Count ? a[i] : 0) - (i < b.Count ? b[i] : 0);
            if (d != 0) return d; // NaN included, as `d !== 0` lets it through
        }
        return 0;
    }

    /// <summary>Whether juniors are used in a week: not at all, as usual, or ahead of adults on junior slots.</summary>
    private enum JsaUse { Off, On, First }

    public static FortnightResult RunFortnightStage2(FortnightInput input)
    {
        _rules = RosteringRules.Resolve(input.Rules);
        _termWeeks = TermWeeksOf(input.Days);
        return new Run(input).Execute();
    }

    /// <summary>One run of the engine: the TypeScript function body and its closures.</summary>
    private sealed partial class Run
    {
        private readonly FortnightInput input;
        private readonly IReadOnlyList<OrderStep> order;
        private readonly IReadOnlyList<string> priority;
        private readonly JsMap<string, FortnightEmp> empById;
        private readonly JsMap<string, Ledger> ledgers;
        private readonly JsMap<string, DayRoster> byDate = new();
        private readonly JsaUse[] useJsasByWeek = [JsaUse.Off, JsaUse.Off];
        private readonly JsMap<int, DayFillState> states = new();
        private List<int> chronological = new();

        public Run(FortnightInput input)
        {
            this.input = input;
            order = RosterOrder.NormaliseOrder(input.Order);
            priority = input.TaskPriority is { Count: > 0 } ? input.TaskPriority : TaskGroups.TaskPriority;
            empById = new JsMap<string, FortnightEmp>(input.Employees.Select(e => (e.Id, e)));
            ledgers = new JsMap<string, Ledger>(input.Employees.Select(e => (e.Id, NewLedger())));
        }

        private BreakRule Rule(string role) => input.BreakRules[role];

        public FortnightResult Execute()
        {
            // Resourcing balance per Mon-Sun week: when the adults' contracted hours meet
            // or exceed the plan's paid hours, the store is over-resourced and JSAs
            // (casuals) are not used at all - every plan hour goes to fulfilling adult
            // contracts (interview). Under-resourced weeks flex JSAs up before overtime.
            // "first": the adults have too few shift-days for the week's slots, so the
            // juniors are not just allowed but take the junior slots ahead of adults.
            foreach (var w in new[] { 0, 1 })
            {
                double planPaid = 0;
                foreach (var day in input.Days)
                {
                    if (day.WeekIndex != w || day.Closed) continue;
                    foreach (var s in day.Slots)
                    {
                        var rule =
                            s.Role == SlotRoles.MGR
                                ? Rule(Roles.ASM)
                                : s.Role == SlotRoles.JSA
                                    ? Rule(Roles.JSA)
                                    : Rule(Roles.SA);
                        planPaid += PaidHoursOf(s.Role == SlotRoles.MGR ? Roles.ASM : Roles.SA, rule, s.Start, s.End);
                    }
                }
                // Contract that is actually ROSTERABLE this week. Someone on approved
                // leave is paid to contract without touching a plan slot, so counting
                // their full contract here would read the store as over-resourced and
                // switch the casuals off - leaving the slots their leave just freed up
                // with nobody legally able to take them.
                double adultContract = 0;
                foreach (var e in input.Employees.Where(e => e.Role != Roles.JSA))
                    adultContract += Math.Max(
                        0,
                        WeeklyContractOf(e) - LeaveHoursInWeek(e, input.Days, w) - PhCreditInWeek(e, input.Days, w));
                // Hours are not the only limit: nobody works more than R.maxWorkedPerWeek
                // days a week, so a plan of many short shifts can need more shift-days
                // than the adults have, even when their contracts cover its hours. An
                // adult who down-covers a junior's slot spends one of those days, and an
                // adult's slot later in the week goes unfilled while the juniors sit
                // idle. Count the days too.
                double slotDays = 0;
                foreach (var day in input.Days) if (day.WeekIndex == w && !day.Closed) slotDays += day.Slots.Count;
                double adultDays = 0;
                foreach (var e in input.Employees.Where(e => e.Role != Roles.JSA))
                {
                    double open = 0;
                    foreach (var day in input.Days)
                        if (day.WeekIndex == w && !day.Closed && day.Slots.Count > 0 && day.Avail.Get(e.Id)?.Available == true) open++;
                    adultDays += Math.Min(open, R.MaxWorkedPerWeek);
                }
                useJsasByWeek[w] = slotDays > adultDays
                    ? JsaUse.First
                    : planPaid > adultContract + 1e-9 ? JsaUse.On : JsaUse.Off;
            }

            // ── The phase runner (Roster Order page) ──────────────────────────────────
            // Every open day gets a persistent fill state; the configured steps then run
            // over those states in sequence. The manager ALLOCATE phases pre-assign the
            // open/close slots cohort by cohort (whole slots only, stopping at weekly
            // contract); FILL_FLOOR runs the complete fill machinery - splits, merges,
            // the emergency tier and unfilled marking - over everything that remains, in
            // the configured day order; SHUFFLE_JSA is the fortnight repair + junior
            // rescue; CONTRACT_CHECK raises the under-contract flags.
            for (var di = 0; di < input.Days.Count; di++)
            {
                var day = input.Days[di];
                if (day.Closed || day.Slots.Count == 0)
                {
                    byDate.Set(day.Date, new DayRoster());
                    continue;
                }
                var state = AnchorDay(di, day, empById, ledgers, input.BreakRules);
                states.Set(di, state);
                byDate.Set(day.Date, state.Roster);
            }

            chronological = states.Keys().OrderBy(k => k).ToList();

            var flags = new List<string>();
            var contractChecked = false;
            foreach (var step in order)
            {
                switch (step.Type)
                {
                    case OrderStepTypes.AllocateSm:
                        RunFill(PhaseDayOrder(step.DayOrder), MgrSlot, CohortOf([Roles.SM]), false);
                        break;
                    case OrderStepTypes.AllocateAsm:
                        // Opposition falls out of the sequencing: on each day the SM works,
                        // the only MGR slot left is the OPPOSITE one (opener vs closer), and
                        // the SM's off days are covered to the ASM's own contract.
                        RunFill(PhaseDayOrder(step.DayOrder), MgrSlot, CohortOf([Roles.ASM]), false);
                        break;
                    case OrderStepTypes.AllocateDsm:
                        // RSM included but ranked last (rsmLast) - it steps up only when no
                        // DSM can cover.
                        RunFill(PhaseDayOrder(step.DayOrder), MgrSlot, CohortOf([Roles.DSM, Roles.RSM]), false);
                        break;
                    case OrderStepTypes.FillFloor:
                        RunFill(PhaseDayOrder(step.DayOrder), _ => true, null, true);
                        break;
                    case OrderStepTypes.ShuffleJsa:
                        // The fortnight repair + junior rescue. It claims UNFILLED rows, so
                        // everything still open must be marked first.
                        SweepUnfilled();
                        RepairFortnight(input, byDate, ledgers);
                        break;
                    case OrderStepTypes.ContractCheck:
                        flags = ContractFlags();
                        contractChecked = true;
                        break;
                }
            }
            SweepUnfilled();

            PaintAll();

            // The contract and run-length lines are written again from the final
            // ledger, once, here. After the check, the painter trims shifts, the break
            // rescue swaps who holds two shifts and adds time, and a break that leaves
            // time empty cuts another shift - the ledger follows each of them, but the
            // lines written at the check did not: a swap alone left both people's lines
            // with their hours from before it. (A custom order with steps after the
            // check is covered too.) Where nothing changed, the lines read the same.
            if (contractChecked) flags = ContractFlags();

            return new FortnightResult
            {
                ByDate = byDate,
                Flags = flags,
                Outcomes = ContractOutcomes(input.Employees, ledgers, input.Days),
            };
        }

        private List<string> ContractFlags() =>
        [
            .. UnderContractFlags(input.Employees, ledgers, input.Days, input.BreakRules),
            .. OverContractFlags(input.Employees, ledgers, input.Days),
            .. LongRunFlags(input.Employees, ledgers, input.Days),
        ];

        // Weekend reservation: contract-targeting must not burn everyone's 5 workable
        // days on weekdays and leave Saturday empty. Penalise (never block) choosing
        // someone today when working today would leave them too few week-days for the
        // weekend days they can still cover.
        private double ReservePenalty(int di, string empId)
        {
            var day = input.Days[di];
            var weekEnd = day.WeekIndex * 7 + 6;
            var weekendAvail = 0;
            for (var d = di + 1; d <= weekEnd && d < input.Days.Count; d++)
            {
                if (d % 7 < 5) continue; // only Sat(5)/Sun(6) of the Mon-start week
                var dd = input.Days[d];
                if (dd.Closed || dd.Slots.Count == 0) continue;
                if (dd.Avail.Get(empId)?.Available == true) weekendAvail++;
            }
            if (weekendAvail == 0) return 0;
            var led = ledgers[empId];
            var budgetLeft = R.MaxWorkedPerWeek - DaysWorkedInWeek(led, day.WeekIndex, di);
            return budgetLeft - 1 < weekendAvail ? 1 : 0;
        }

        // Trading days left this week, today included, on which this person is
        // actually AVAILABLE. Someone with both weekend days marked off has only the
        // weekdays to reach contract - and every one they miss is gone.
        private double AvailableDaysLeft(int di, string empId)
        {
            var weekEnd = input.Days[di].WeekIndex * 7 + 6;
            var n = 0;
            for (var d = di; d <= weekEnd && d < input.Days.Count; d++)
            {
                var dd = input.Days[d];
                if (dd.Closed || dd.Slots.Count == 0) continue;
                if (dd.Avail.Get(empId)?.Available == true) n++;
            }
            return n;
        }

        // Earliest slot the NEXT trading day opens with - what a late finish today
        // would cost someone bound by the 11-hour rest gap.
        private double? NextDayStartFor(int di)
        {
            if (di + 1 >= input.Days.Count) return null;
            var next = input.Days[di + 1];
            if (next.Closed || next.Slots.Count == 0) return null;
            return Js.Min(next.Slots.Select(s => s.Start));
        }

        // Trading days still to come in this Mon-Sun week, today included. With a
        // hard 5-days-a-week cap this is the other half of "how many shifts have I
        // got left to reach contract".
        private double OpenDaysLeftInWeek(int di)
        {
            var weekEnd = input.Days[di].WeekIndex * 7 + 6;
            var n = 0;
            for (var d = di; d <= weekEnd && d < input.Days.Count; d++)
            {
                var dd = input.Days[d];
                if (!dd.Closed && dd.Slots.Count > 0) n++;
            }
            return n;
        }

        // Committed shift start tomorrow, for the forward rest-gap check - an
        // out-of-order fill may already have placed the person's next day.
        private Func<string, double?> NextShiftStartFor(int di) => empId =>
        {
            if (!states.TryGet(di + 1, out var st)) return null;
            var s = st.Roster.Shifts.Find(x => !x.Unfilled && x.EmployeeId == empId);
            return s is null ? null : s.Start;
        };

        private void RunFill(
            List<int> dayIndices,
            Func<PlanSlot, bool> slotPred,
            Func<FortnightEmp, DayContext, bool>? cohort,
            bool finalPass)
        {
            foreach (var di in dayIndices)
            {
                if (!states.TryGet(di, out var state)) continue;
                var day = input.Days[di];
                FillDaySlots(
                    di,
                    day,
                    empById,
                    ledgers,
                    input.BreakRules,
                    id => ReservePenalty(di, id),
                    useJsasByWeek[day.WeekIndex],
                    NextDayStartFor(di),
                    OpenDaysLeftInWeek(di),
                    id => AvailableDaysLeft(di, id),
                    state,
                    slotPred,
                    cohort,
                    finalPass,
                    NextShiftStartFor(di));
            }
        }

        // Phase day order: the configured day-of-week sequence within each Mon-Sun
        // week (hardest-to-staff days claim scarce availability first). Every
        // filling phase - manager allocation included - defaults to it: allocating
        // chronologically burned the SM/ASM week caps on Monday-Friday and left the
        // weekend's manager slots with nobody eligible.
        private List<int> PhaseDayOrder(IReadOnlyList<int>? dayOrder)
        {
            var seq = dayOrder ?? RosterOrder.DefaultFloorDayOrder;
            var out_ = new List<int>();
            foreach (var w in new[] { 0, 1 })
            {
                foreach (var dow in seq)
                {
                    var di = w * 7 + dow;
                    if (states.Has(di)) out_.Add(di);
                }
            }
            return out_;
        }

        private static bool MgrSlot(PlanSlot s) => s.Role == SlotRoles.MGR;

        private Func<FortnightEmp, DayContext, bool> CohortOf(string[] roles) => (e, day) =>
            roles.Contains(e.Role) && ledgers[e.Id].PaidHoursByWeek[day.WeekIndex] < WeeklyContractOf(e);

        // Any slot never claimed by a phase becomes a visible UNFILLED row - run
        // before the shuffle (repair CLAIMs unfilled slots) and again at the end in
        // case the configured order has no fill step at all.
        private void SweepUnfilled()
        {
            foreach (var di in chronological)
            {
                var state = states[di];
                var day = input.Days[di];
                foreach (var slot in day.Slots)
                {
                    if (state.SlotFilled.Has(slot.Id)) continue;
                    state.SlotFilled.Add(slot.Id);
                    var label = slot.Role == SlotRoles.MGR ? "No manager available to cover" : "Understaffed";
                    var msg = $"{label} {Fmt(slot.Start)}–{Fmt(slot.End)} - external cover may be required";
                    if (!state.Roster.Flags.Contains(msg)) state.Roster.Flags.Add(msg);
                    state.Roster.Shifts.Add(UnfilledShift(slot));
                }
            }
        }

        // ── Task painting once, at the end, over the final shift set (the repair may
        // have moved holders between days). ────────────────────────────────────────
        private void PaintAll()
        {
            // Where each person stands against contract before a day's cuts to fill
            // time a shorter break left empty: the ledger holds today's shifts at their
            // times before painting, so the painted times are priced in here.
            var cutsSoFar = new JsMap<string, double>();
            Func<string, Standing?> StandingOn(List<FShift> shifts, List<(double Start, double End)> was)
            {
                var out_ = new JsMap<string, Standing?>();
                return id =>
                {
                    if (out_.TryGet(id, out var cached)) return cached;
                    var e = empById.Get(id);
                    var led = ledgers.Get(id);
                    Standing? st = null;
                    if (e is not null && led is not null && e.Role != Roles.JSA)
                    {
                        var paid = led.PaidHoursByWeek[0] + led.PaidHoursByWeek[1];
                        for (var i = 0; i < shifts.Count; i++)
                        {
                            var s = shifts[i];
                            if (s.Unfilled || s.EmployeeId != id) continue;
                            var rule = input.BreakRules[s.Role];
                            paid += PaidHoursOf(s.Role, rule, s.Start, s.End) - PaidHoursOf(s.Role, rule, was[i].Start, was[i].End);
                        }
                        var credit = LeaveHoursFor(e, input.Days) + PhCreditFor(e, input.Days, d => led.DaysWorked.Has(d));
                        st = new Standing(paid + credit - e.ContractHours, Math.Max(4, e.ContractHours / 10));
                    }
                    out_.Set(id, st);
                    return st;
                };
            }

            foreach (var di in chronological)
            {
                var day = input.Days[di];
                var roster = states[di].Roster;
                var was = roster.Shifts.Select(s => (s.Start, s.End)).ToList();
                var painted = PaintKeepingHeld(day, roster.Shifts, empById, priority, input.BreakRules);
                // Breaks that cost work (master plan decision 4.2): swap who holds two
                // shifts, or add a little time, where that saves the work.
                if (LostWeight(painted.Lost) > 0 && !roster.Shifts.Any(s => s.Unfilled) && BreaksDiffer(day, roster.Shifts, input.BreakRules))
                {
                    FShift? ShiftOn(string empId, int d) =>
                        states.TryGet(d, out var st) ? st.Roster.Shifts.Find(s => s.EmployeeId == empId && !s.Unfilled) : null;
                    var rescued = RescueLostWork(input, di, roster, was, empById, priority, ledgers, ShiftOn, painted);
                    if (rescued is not null)
                    {
                        painted = rescued.Value.Result;
                        foreach (var n in rescued.Value.Notes) if (!roster.Flags.Contains(n)) roster.Flags.Add(n);
                    }
                }
                // A break shorter than the plan priced leaves time with no work (a
                // manager in a store assistant's shift over six hours): fill it with work
                // from another shift's first or last quarter-hours, and that shift starts
                // later or finishes earlier (the owner's decision of 14/11/2026).
                var gap = BreakGaps.FillBreakGaps(
                    roster.Shifts,
                    day,
                    input.BreakRules,
                    new BreakGapRules
                    {
                        MinPeopleOnFloor = R.MinPeopleOnFloor,
                        MinAdultsWithJsa = R.MinAdultsWithJsa,
                        MinShiftMin = R.MinPieceMin,
                        OverlapMinMin = R.OverlapMinMin,
                        BreakEndBy = day.TradingClose >= 21 * 60 ? null : 18 * 60,
                    },
                    id => empById.Get(id)?.Restricted ?? [],
                    id => day.Avail.Get(id)?.Fixed is not null,
                    StandingOn(roster.Shifts, was),
                    cutsSoFar);
                if (gap is not null)
                {
                    var note = BreakGaps.RosterBreakCutNote(gap.Cuts, gap.Fills);
                    if (note is not null && !roster.Flags.Contains(note)) roster.Flags.Add(note);
                    foreach (var c in gap.Cuts)
                        if (c.To.End != c.From.End) ledgers.Get(c.EmployeeId)?.ShiftEndByDay.Set(di, c.To.End);
                    painted = painted with
                    {
                        Blanks = painted.Blanks
                            .Select(b => b with
                            {
                                Times = b.Times.Where(t => BlockAt(b.Shift, t)?.Group == TaskGroups.Flex).ToList(),
                            })
                            .Where(b => b.Times.Count > 0)
                            .ToList(),
                    };
                }
                // The painter cuts shifts around the work. A shift it shortened is that
                // much shorter on the roster, so its break and paid hours follow it into
                // the ledger the contract outcomes are read from.
                for (var i = 0; i < roster.Shifts.Count; i++)
                {
                    var s = roster.Shifts[i];
                    if (s.Unfilled) continue;
                    var w = was[i];
                    if (w.Start == s.Start && w.End == s.End) continue;
                    var rule = input.BreakRules[s.Role];
                    s.BreakMinutes = TotalBreakMinutes(rule, s.Start, s.End);
                    var led = ledgers.Get(s.EmployeeId);
                    if (led is not null)
                    {
                        led.PaidHoursByWeek[day.WeekIndex] -=
                            PaidHoursOf(s.Role, rule, w.Start, w.End) - PaidHoursOf(s.Role, rule, s.Start, s.End);
                    }
                }
                foreach (var b in painted.Blanks)
                {
                    var f = TaskPainter.BlankFlag(b.Shift.Name, b.Shift.Start, b.Shift.End, b.Times);
                    if (!roster.Flags.Contains(f)) roster.Flags.Add(f);
                }
                // Name every block the finished roster leaves without a manager, below the
                // floor minimum, or with a junior under-supervised. An open slot used to
                // say only "Understaffed"; this says what the gap actually breaks.
                foreach (var f in Coverage.CoverFlags(roster.Shifts, R))
                {
                    if (!roster.Flags.Contains(f)) roster.Flags.Add(f);
                }
                foreach (var l in painted.Lost)
                {
                    var f = TaskPainter.LostWorkFlag(l.Group, l.Blocks);
                    if (!roster.Flags.Contains(f)) roster.Flags.Add(f);
                }
                if (painted.SearchCapped && !roster.Flags.Contains(TaskPainter.SearchCapFlag)) roster.Flags.Add(TaskPainter.SearchCapFlag);
            }
        }
    }

    /// <summary>The visible UNFILLED row for a slot nobody could legally take.</summary>
    private static FShift UnfilledShift(PlanSlot slot) => new()
    {
        EmployeeId = $"open-{slot.Id}",
        Name = "UNFILLED - needs cover",
        Role = slot.Role == SlotRoles.MGR ? Roles.DSM : Roles.SA,
        Start = slot.Start,
        End = slot.End,
        BreakMinutes = 0,
        Blocks = new List<ShiftBlock>(),
        Locked = false,
        Unfilled = true,
        SlotRole = slot.Role,
    };

    /// <summary><c>shift.blocks[(t - shift.start) / BLOCK_MIN]</c>: the tile at a time, or null outside the list.</summary>
    private static ShiftBlock? BlockAt(FShift s, double t)
    {
        var idx = (t - s.Start) / BLOCK_MIN;
        if (idx < 0 || idx != Math.Floor(idx) || idx >= s.Blocks.Count) return null;
        return s.Blocks[(int)idx];
    }
}
