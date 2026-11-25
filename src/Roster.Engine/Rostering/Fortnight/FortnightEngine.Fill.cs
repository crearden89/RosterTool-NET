// Port of src/lib/stage2/fortnight.ts (the day fill state, the fixed-shift
// anchors and fillDaySlots).

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

public static partial class FortnightEngine
{
    // ─── Day fill state, shared across the order's phases ────────────────────────
    // One per open day, created by AnchorDay before any phase runs. The phases
    // mutate it in turn; Roster.Shifts/Flags are the SAME lists ByDate exposes.
    private sealed class DayFillState
    {
        public DayRoster Roster { get; } = new();
        public JsSet<string> UsedToday { get; } = new();
        public JsSet<string> SlotFilled { get; } = new();
        // Which slot ROLE each employee's shift is serving - merges may only extend a
        // shift over a slot of the SAME role class (a manager shift must never be
        // stretched to absorb SA coverage: the open/close shape is sacred).
        public JsMap<string, string> ServingRole { get; } = new();
    }

    private static bool SlotCompatible(FortnightEmp emp, string role)
    {
        if (role == SlotRoles.MGR) return Roles.IsManager(emp.Role);
        if (role == SlotRoles.SA) return IsAdult(emp.Role);
        return true; // JSA slot: junior or adult down-cover
    }

    private static void CommitShift(
        DayFillState state,
        JsMap<string, Ledger> ledgers,
        IReadOnlyDictionary<string, BreakRule> breakRules,
        int di,
        int weekIndex,
        FortnightEmp emp,
        double start,
        double end,
        bool locked)
    {
        var rule = breakRules[emp.Role];
        state.UsedToday.Add(emp.Id);
        state.Roster.Shifts.Add(new FShift
        {
            EmployeeId = emp.Id,
            Name = emp.Name,
            Role = emp.Role,
            Start = start,
            End = end,
            BreakMinutes = TotalBreakMinutes(rule, start, end),
            Blocks = new List<ShiftBlock>(),
            Locked = locked,
        });
        var led = ledgers[emp.Id];
        led.PaidHoursByWeek[weekIndex] += PaidHoursOf(emp.Role, rule, start, end);
        led.DaysWorked.Add(di);
        led.ShiftEndByDay.Set(di, Math.Max(led.ShiftEndByDay.GetOr(di, 0), end));
    }

    // Fixed rosters / locked shifts: immovable anchors, committed once per day
    // before ANY phase. Each consumes the best-overlapping compatible slot so the
    // plan doesn't double-demand that coverage.
    private static DayFillState AnchorDay(
        int di,
        DayContext day,
        JsMap<string, FortnightEmp> empById,
        JsMap<string, Ledger> ledgers,
        IReadOnlyDictionary<string, BreakRule> breakRules)
    {
        var state = new DayFillState();
        foreach (var emp in empById.Values())
        {
            var a = day.Avail.Get(emp.Id);
            if (a is null || !a.Available || a.Fixed is null) continue;
            var start = Math.Max(a.Fixed.Start, day.StaffOpen);
            var end = Math.Min(a.Fixed.End, day.StaffClose);
            if (end - start < BLOCK_MIN) continue;
            CommitShift(state, ledgers, breakRules, di, day.WeekIndex, emp, start, end, true);
            // The slot this fixed shift stands for: the plan cuts one with the same
            // times, so an exact match wins; then the biggest overlap, then the
            // closest times. Overlap alone took the first slot it tied with - a
            // 07:00-15:45 load shift for a 09:00-15:45 fixed roster - and the
            // fixed-roster slot then went to someone else at 09:00, so the morning
            // load was never worked on the roster.
            PlanSlot? best = null;
            double[]? bestScore = null;
            foreach (var s in day.Slots)
            {
                if (state.SlotFilled.Has(s.Id) || !SlotCompatible(emp, s.Role)) continue;
                var ov = Math.Min(end, s.End) - Math.Max(start, s.Start);
                if (ov <= 0) continue;
                double[] score =
                [
                    s.Start == start && s.End == end ? 1 : 0,
                    ov,
                    // Prefer a MGR slot for a manager when the overlap ties.
                    Roles.IsManager(emp.Role) && s.Role == SlotRoles.MGR ? 1 : 0,
                    -(Math.Abs(s.Start - start) + Math.Abs(s.End - end)),
                ];
                if (bestScore is null || CmpTuple(score, bestScore) > 0)
                {
                    bestScore = score;
                    best = s;
                }
            }
            if (best is not null)
            {
                state.SlotFilled.Add(best.Id);
                state.ServingRole.Set(emp.Id, best.Role);
            }
        }
        return state;
    }

    // ─── Fill (a subset of) one day's plan slots with real people ────────────────
    // One phase's worth of work on one day. Manager ALLOCATE phases pass a slot
    // predicate for MGR slots, a cohort filter and finalPass=false (whole-slot
    // assignment only); FILL_FLOOR passes everything-goes and finalPass=true,
    // unlocking the split/merge/emergency machinery and UNFILLED marking.
    private static void FillDaySlots(
        int di,
        DayContext day,
        JsMap<string, FortnightEmp> empById,
        JsMap<string, Ledger> ledgers,
        IReadOnlyDictionary<string, BreakRule> breakRules,
        Func<string, double> reserve,
        JsaUse useJsas,
        // Earliest slot start TOMORROW (null when the store is shut or the fortnight
        // ends), so a late finish today can be weighed against the morning it costs.
        double? nextDayStart,
        // Trading days left in this Mon-Sun week, today included.
        double openDaysLeft,
        // Of those, how many this employee is actually available for.
        Func<string, double> daysAvailableLeft,
        DayFillState state,
        Func<PlanSlot, bool> slotPred,
        Func<FortnightEmp, DayContext, bool>? cohort,
        bool finalPass,
        // Committed shift start TOMORROW for an employee (out-of-order fills may
        // already have placed it) - the forward rest-gap check.
        Func<string, double?> nextShiftStart)
    {
        var usedToday = state.UsedToday;
        var slotFilled = state.SlotFilled;
        var servingRole = state.ServingRole;
        var shifts = state.Roster.Shifts;
        var flags = state.Roster.Flags;
        void Commit(FortnightEmp emp, double start, double end, bool locked) =>
            CommitShift(state, ledgers, breakRules, di, day.WeekIndex, emp, start, end, locked);

        // ── Candidate machinery ───────────────────────────────────────────────────
        var j = JsaWindow(day.IsSchoolDay);

        // Can `emp` work [start, end) today - availability window, rest gap, caps,
        // role-specific JSA limits. (Slot-role compatibility is checked separately.)
        bool Fits(FortnightEmp emp, double start, double end)
        {
            var a = day.Avail.Get(emp.Id);
            if (a is null || !a.Available || a.Fixed is not null || usedToday.Has(emp.Id)) return false;
            var led = ledgers[emp.Id];
            if (!CanWorkToday(emp.Role, led, di, day.WeekIndex)) return false;
            if (a.FromMin is { } from && from > start) return false;
            if (a.ToMin is { } to && to < end) return false;
            if (RestGapEarliestStart(emp.Role, led, di) > start) return false;
            // Forward rest gap: an out-of-order fill may already have placed this
            // person's NEXT day, so a late finish today can be illegal against a
            // committed early start tomorrow (mirror of RestGapEarliestStart).
            if (!RestGapExempt(emp.Role))
            {
                var ns = nextShiftStart(emp.Id);
                if (ns is { } nsv && nsv < end - DAY_MIN + R.RestGapMin) return false;
            }
            var rule = breakRules[emp.Role];
            var len = end - start;
            // The award maximum caps PAID hours, not clock: a 9-hour Store Assistant
            // works a 9.5h span, 30 minutes of which is the unpaid break. Comparing
            // clock against the cap docked half an hour of pay from every full-length
            // shift, which is precisely what kept the managers under contract.
            if (PaidHoursOf(emp.Role, rule, start, end) > rule.MaxShiftHours + 1e-9) return false;
            if (OverWeeklyCap(rule, led.PaidHoursByWeek[day.WeekIndex] + PaidHoursOf(emp.Role, rule, start, end))) return false;
            if (len < Math.Min(R.MinPieceMin, ToBlocks(rule.MinShiftHours) * BLOCK_MIN)) return false;
            if (emp.Role == Roles.JSA)
            {
                if (start < j.Start || end > j.Latest) return false;
                if (len > j.MaxLenMin) return false;
                if (PaidHoursOf(Roles.JSA, rule, start, end) > JsaWeeklyRemaining(day, led) + 1e-9) return false;
            }
            return true;
        }

        // Clopening (closing one night, opening the next morning) is legal for the
        // rest-gap-exempt SM/ASM but must still be AVOIDED: a candidate whose start
        // today falls inside the 11-hour window after yesterday's finish ranks
        // behind every properly-rested alternative, of any role - they're chosen
        // only when nobody else can cover. (Non-exempt roles are hard-blocked by
        // the rest gap in Fits(), so this penalty effectively steers SM/ASM.)
        double ClopenPenalty(FortnightEmp emp, double start)
        {
            if (!ledgers[emp.Id].ShiftEndByDay.TryGet(di - 1, out var prevEnd)) return 0;
            return R.RestGapMin - DAY_MIN + prevEnd > start ? 1 : 0;
        }

        // The mirror image, looking FORWARD: a late finish costs a rest-gap-bound
        // manager tomorrow morning entirely, because the 11-hour gap pushes their
        // earliest start past the opener. With only two manager slots a day that is
        // a whole manager-day thrown away - the closer belongs to a rest-gap-exempt
        // SM/ASM, who can open the next morning regardless. Without this the DSM
        // takes the long closer on the even-spread tiebreak, is locked out of the
        // next opener, and the SM/ASM burn their week budget covering the mornings
        // instead of the weekend.
        double CostsTomorrow(FortnightEmp emp, double end)
        {
            if (nextDayStart is not { } nextStart) return 0;
            if (RestGapExempt(emp.Role)) return 0;
            var led = ledgers[emp.Id];
            if (DaysWorkedInWeek(led, day.WeekIndex, di) + 1 >= R.MaxWorkedPerWeek) return 0;
            return R.RestGapMin - DAY_MIN + end > nextStart ? 1 : 0;
        }

        // ─── Slot length vs what the person still owes per day ────────────────────
        // The five-days-a-week cap means everyone gets at most five shifts a week,
        // so a 45h/wk manager needs NINE PAID HOURS every single time. Ranking on
        // contract size alone hands them whatever slot comes up first - take a 5.5h
        // one and a day is gone that can never be earned back, while the 10.5h/wk
        // casual the slot actually suited gets nothing. Over a fortnight that is the
        // whole under-contract problem: the hours exist, they are just handed out in
        // the wrong sizes.
        //
        // So rank on FIT - the paid hours a candidate must average over each of
        // their remaining workable days. Big contracts gravitate to the long slots
        // and small contracts to the short ones, with no change to the plan's shape.
        double RequiredPerDay(FortnightEmp emp)
        {
            var led = ledgers[emp.Id];
            var need = WeeklyContractOf(emp) - led.PaidHoursByWeek[day.WeekIndex];
            if (need <= 0) return 0; // already on contract - any slot is a bonus
            var budgetLeft = R.MaxWorkedPerWeek - DaysWorkedInWeek(led, day.WeekIndex, di);
            var daysLeft = Math.Max(1, Math.Min(budgetLeft, openDaysLeft));
            return need / daysLeft;
        }

        // The day's longest slot, in clock hours. Nobody can get more out of a single
        // day than this - not the award maximum, which is a legal ceiling no plan
        // slot comes near (a Store Manager may work 12 paid hours; the longest slot
        // the shaper cuts is 9.5 clock).
        var dayLongestHours = Js.Max(day.Slots.Select(s => (s.End - s.Start) / 60));

        // The most PAID hours this role could take out of today's longest slot.
        double BestPaidToday(string role)
        {
            var rule = breakRules[role];
            var unpaid = Breaks.BreakForShift(rule.BreakType, dayLongestHours).UnpaidMinutes / 60;
            return Math.Min(rule.MaxShiftHours, dayLongestHours - unpaid);
        }

        // Can this person still reach contract if they DON'T work today? Someone
        // with both weekend days marked off has only the weekdays left, so missing
        // one is nine hours they can never earn back. The length-fit rule below is a
        // preference between slots; this is the check that it never costs anybody a
        // whole day. A slightly-short shift always beats no shift.
        //   0 = must work today, 1 = has slack.
        double MustWorkToday(FortnightEmp emp)
        {
            var led = ledgers[emp.Id];
            var need = WeeklyContractOf(emp) - led.PaidHoursByWeek[day.WeekIndex];
            if (need <= 0) return 1;
            // Days needed measured against what a shift REALISTICALLY pays, not the
            // award ceiling. Using the ceiling (12h for an SM/ASM) said a 45h/wk
            // manager could get there in four days when every available slot pays at
            // most nine - so the check never fired and they drifted to four days and
            // ~36 hours while store assistants went OVER contract on the slots they
            // should have had. This stays a necessary condition (the fewest days they
            // could possibly need), so it never forces someone who has genuine slack.
            var daysNeeded = Math.Ceiling(need / BestPaidToday(emp.Role) - 1e-9);
            var budgetLeft = R.MaxWorkedPerWeek - DaysWorkedInWeek(led, day.WeekIndex, di);
            var daysLeft = Math.Min(budgetLeft, daysAvailableLeft(emp.Id));
            return daysNeeded >= daysLeft ? 0 : 1;
        }

        // Penalty in WHOLE hours for a slot that falls short of that figure. Whole
        // hours because anything finer is noise - the same slot pays a manager half
        // an hour less than a store assistant (flat unpaid break vs tiered), and at
        // half-hour granularity that artefact outranked the role hierarchy. Only a
        // shortfall is punished: a slot LONGER than someone needs just puts them
        // over contract, which the contract band already handles.
        double LengthFit(FortnightEmp emp, double start, double end)
        {
            var req = Math.Min(RequiredPerDay(emp), dayLongestHours);
            if (req <= 0) return 0;
            var paidHours = PaidHoursOf(emp.Role, breakRules[emp.Role], start, end);
            return Math.Max(0, Math.Floor(req - paidHours));
        }

        // Rank candidates for a slot: rested before clopening, then slot length
        // matched to the hours each candidate still owes per remaining day, then
        // biggest contracts first; under-contract adults → JSAs (under-resourced
        // flex-up) → adults above contract (salaried SM/ASM first - free - then even
        // OT spread). MGR slots rank SM/ASM → DSM → RSM ahead of everything else.
        // Within a contract band, someone with NO shift at all this fortnight goes
        // first - an active adult must never print an empty roster.
        double[] RankFor(string slotRole, FortnightEmp emp, double start, double end)
        {
            var led = ledgers[emp.Id];
            var paid = led.PaidHoursByWeek[day.WeekIndex];
            var clopen = ClopenPenalty(emp, start);
            double hasShift = led.PaidHoursByWeek[0] + led.PaidHoursByWeek[1] > 0 ? 1 : 0;
            if (slotRole == SlotRoles.MGR)
            {
                // Managers are rostered as their own entity, spread EVENLY across the
                // fortnight rather than SM/ASM working Monday-Friday and the rest idle.
                // Order of the tuple:
                //   reserve  - weekend reservation: don't burn a manager's week budget on
                //              a weekday when they're needed for a weekend day nobody
                //              else can cover. This outranks the clopen penalty on
                //              purpose: a clopen is legal-but-undesirable for the exempt
                //              SM/ASM (everyone else is hard-blocked by the rest gap),
                //              whereas a weekend with NO manager is an operational
                //              failure. The penalty only fires when the week budget is
                //              genuinely too thin for the weekend days that person is
                //              the last cover for, so it stays rare;
                //   clopen   - otherwise a rested manager always beats a close-then-open
                //              one;
                //   costsTom - a late finish locks a rest-gap-bound manager out of
                //              tomorrow's opener; give the closer to an exempt SM/ASM
                //              instead of burning a whole manager-day;
                //   rsmLast  - the RSM (priciest cover: SA rate + sole-manager allowance)
                //              stays a backup behind the core SM/ASM/DSM managers;
                //   mustWork - they cannot reach contract if they sit today out, so the
                //              length preference below must not be allowed to cost them
                //              the whole day;
                //   lengthFit- match the slot to the hours the candidate still owes per
                //              remaining day, so the 45h/wk SM takes the 9.5h slots and
                //              the 35h/wk DSM takes the shorter ones instead of whoever
                //              happens to rank first burning a day on a short shift;
                //   daysSoFar- FEWEST fortnight-days worked first, so the 28 manager
                //              slots rotate across the whole team instead of piling onto
                //              two people (this is the even-spread the manager phase is
                //              about);
                //   MGR_RANK - on equal load, SM/ASM lead DSM;
                // then the usual contract-fill tiebreaks.
                double rsmLast = emp.Role == Roles.RSM ? 1 : 0;
                double daysSoFar = led.DaysWorked.Size;
                return
                [
                    reserve(emp.Id),
                    clopen,
                    CostsTomorrow(emp, end),
                    rsmLast,
                    MustWorkToday(emp),
                    LengthFit(emp, start, end),
                    daysSoFar,
                    MgrSlotRank(emp.Role),
                    hasShift,
                    paid < WeeklyContractOf(emp) ? 0 : 1,
                    -emp.ContractHours,
                    paid,
                ];
            }
            // Contract-filling order: the SM/ASM/DSM cohort is filled to contract
            // FIRST, then store assistants biggest contract to smallest. Managers hold
            // the largest contracts and only two slots a day are labelled MGR, so
            // without this they finish the fortnight short every time - their
            // remaining hours have to come off the SA slots. Any residual shortfall
            // then lands on the SMALLEST contracts, which is where the business can
            // act on it (leave), rather than being smeared across everyone.
            double mgrFirst = CoreManager(emp.Role) ? 0 : 1;
            // A junior slot goes to a junior first when the adults' shift-days are
            // short: an adult spending a day on it leaves an adult slot open later.
            if (emp.Role == Roles.JSA)
                return [clopen, useJsas == JsaUse.First && slotRole == SlotRoles.JSA ? -1 : 1, reserve(emp.Id), paid, 0];
            if (paid < WeeklyContractOf(emp))
                return
                [
                    clopen,
                    0,
                    reserve(emp.Id),
                    MustWorkToday(emp),
                    LengthFit(emp, start, end),
                    mgrFirst,
                    hasShift,
                    -emp.ContractHours,
                    paid,
                ];
            return [clopen, 2, reserve(emp.Id), Salaried(emp.Role) ? 0 : 1, mgrFirst, paid];
        }

        // Managers already serving FLOOR coverage today (beyond the two MGR slots).
        // Interview: the fortnight has ~12 spare manager-days beyond open/close, so
        // at most ONE extra manager joins the floor on a given day - the rest of the
        // floor belongs to SAs/JSAs. Soft cap: a second spare manager is still
        // preferred over leaving a slot UNFILLED (see the fallback below).
        int FloorManagersToday() =>
            shifts.Count(s =>
                !s.Unfilled &&
                CoreManager(s.Role) &&
                servingRole.Get(s.EmployeeId) != SlotRoles.MGR);

        // A manager still short of their contract this week. Their hours are a
        // COMMITMENT, not overflow: two MGR slots a day cannot carry a 45h/week
        // contract, so the balance has to come off the SA slots. The
        // one-extra-manager-a-day cap exists to stop IDLE managers crowding the
        // floor - it must never be the reason a manager finishes under contract.
        bool OwedContract(FortnightEmp emp) =>
            CoreManager(emp.Role) &&
            ledgers[emp.Id].PaidHoursByWeek[day.WeekIndex] < WeeklyContractOf(emp);

        List<FortnightEmp> CandidatesFor(
            string slotRole,
            double start,
            double end,
            // EMERGENCY tier: the slot is otherwise going UNFILLED. Lifts the
            // one-extra-manager floor cap AND the over-resourced JSA bench.
            bool emergency = false)
        {
            var list = empById.Values()
                .Where(e => SlotCompatible(e, slotRole))
                // Phase cohort (ALLOCATE steps): only the phase's roles, and only while
                // still under weekly contract.
                .Where(e => cohort is null || cohort(e, day))
                // Over-resourced week: casuals are benched so every plan hour goes to
                // fulfilling adult contracts - but NEVER to the point of leaving the
                // floor short. An unfilled slot is worse than a paid casual, so the
                // bench lifts in the emergency tier.
                .Where(e => !(e.Role == Roles.JSA && useJsas == JsaUse.Off && !emergency))
                .Where(e =>
                    slotRole == SlotRoles.MGR ||
                    emergency ||
                    !CoreManager(e.Role) ||
                    OwedContract(e) ||
                    FloorManagersToday() < 1)
                .Where(e => Fits(e, start, end))
                .ToList();
            // Rank tuples are worked out per comparison, as the TypeScript comparator does.
            return Js.Sort(list, (a, b) => CmpTuple(RankFor(slotRole, a, start, end), RankFor(slotRole, b, start, end)));
        }

        // ≥3 adults on the floor for every block a JSA works, counted over the
        // shifts assigned so far (MGR + SA slots are filled before JSA slots).
        bool JsaAdultCoverOk(double start, double end)
        {
            for (var m = start; m < end; m += BLOCK_MIN)
            {
                var adults = 0;
                foreach (var s in shifts)
                    if (!s.Unfilled && IsAdult(s.Role) && s.Start <= m && m < s.End) adults++;
                if (adults < R.MinAdultsWithJsa) return false;
            }
            return true;
        }

        // ── 3. Fill slots: MGR (opener, closer) first - the day's shape is decided
        //       by its managers - then ALL floor slots longest-first, SA and JSA
        //       together.
        //
        //       JSA slots are deliberately NOT deferred to the end. The label is a
        //       statement about who MAY take a slot, not about when it should be
        //       filled: adults down-cover JSA slots freely, so ordering by label
        //       pushed perfectly ordinary floor shifts behind everything else and
        //       the adults who needed them had already spent their day budget.
        //       Interleaving by length is safe because a junior is only ever chosen
        //       when JsaAdultCoverOk passes against the adults placed SO FAR - an
        //       early JSA slot simply fails that check and goes to an adult, which
        //       is the outcome we want anyway.
        static double RoleOrder(string role) => role == SlotRoles.MGR ? 0 : 1;
        var orderedSlots = Js.Sort(
            day.Slots.Where(slotPred).ToList(),
            (a, b) =>
                (RoleOrder(a.Role) - RoleOrder(b.Role))
                .Or(b.End - b.Start - (a.End - a.Start)) // longest first: hardest to fit
                .Or(a.Start - b.Start));

        string WhyParts()
        {
            var unavailable = 0;
            var capped = 0;
            var working = 0;
            var other = 0;
            foreach (var e in empById.Values())
            {
                var a = day.Avail.Get(e.Id);
                if (a is null || !a.Available) unavailable++;
                else if (usedToday.Has(e.Id)) working++;
                else if (!CanWorkToday(e.Role, ledgers[e.Id], di, day.WeekIndex)) capped++;
                else other++;
            }
            return
                $"Why: of {N(empById.Size)} staff - {N(working)} already rostered today, " +
                $"{N(unavailable)} unavailable (leave / weekend off / availability), " +
                $"{N(capped)} at the 5-days-per-week cap" +
                (other > 0 ? $", {N(other)} can't take the open shift" : "");
        }

        // Extend an already-assigned adjacent shift (same slot-role compatibility)
        // over an unfillable slot's span, when that person may legally work the
        // combined length. Ledger is repriced for the extra paid hours.
        bool TryExtendNeighbourInto(PlanSlot slot)
        {
            foreach (var s in shifts)
            {
                if (s.Unfilled || s.Locked) continue;
                if (s.End != slot.Start && s.Start != slot.End) continue;
                // Only a shift already serving THIS role class may absorb the slot - a
                // manager's open/close shift is never stretched over floor coverage.
                if (servingRole.Get(s.EmployeeId) != slot.Role) continue;
                var emp = empById.Get(s.EmployeeId);
                if (emp is null || !SlotCompatible(emp, slot.Role)) continue;
                var nStart = Math.Min(s.Start, slot.Start);
                var nEnd = Math.Max(s.End, slot.End);
                var a = day.Avail[emp.Id];
                var rule = breakRules[emp.Role];
                if (PaidHoursOf(emp.Role, rule, nStart, nEnd) > rule.MaxShiftHours + 1e-9) continue;
                if (a.FromMin is { } from && from > nStart) continue;
                if (a.ToMin is { } to && to < nEnd) continue;
                var led = ledgers[emp.Id];
                if (RestGapEarliestStart(emp.Role, led, di) > nStart) continue;
                // Forward rest gap, exactly as Fits() checks it: days are filled out of
                // order, so tomorrow's start may already be committed and a later finish
                // today can land inside the 11 hours.
                if (!RestGapExempt(emp.Role))
                {
                    var ns = nextShiftStart(emp.Id);
                    if (ns is { } nsv && nsv < nEnd - DAY_MIN + R.RestGapMin) continue;
                }
                if (emp.Role == Roles.JSA)
                {
                    if (nStart < j.Start || nEnd > j.Latest || nEnd - nStart > j.MaxLenMin) continue;
                    var extra =
                        PaidHoursOf(Roles.JSA, rule, nStart, nEnd) - PaidHoursOf(Roles.JSA, rule, s.Start, s.End);
                    if (extra > JsaWeeklyRemaining(day, led) + 1e-9) continue;
                    // >=3 adults in every block the junior's shift newly covers - the
                    // same rule every other junior placement enforces. Without it a
                    // junior was stretched to 18:00 with one adult left after 15:00.
                    if (nStart < s.Start && !JsaAdultCoverOk(nStart, s.Start)) continue;
                    if (nEnd > s.End && !JsaAdultCoverOk(s.End, nEnd)) continue;
                }
                var delta =
                    PaidHoursOf(emp.Role, rule, nStart, nEnd) - PaidHoursOf(emp.Role, rule, s.Start, s.End);
                if (OverWeeklyCap(rule, led.PaidHoursByWeek[day.WeekIndex] + delta)) continue;
                led.PaidHoursByWeek[day.WeekIndex] += delta;
                led.ShiftEndByDay.Set(di, Math.Max(led.ShiftEndByDay.GetOr(di, 0), nEnd));
                s.Start = nStart;
                s.End = nEnd;
                s.BreakMinutes = TotalBreakMinutes(rule, nStart, nEnd);
                return true;
            }
            return false;
        }

        for (var si = 0; si < orderedSlots.Count; si++)
        {
            var slot = orderedSlots[si];
            if (slotFilled.Has(slot.Id)) continue;

            // JSA slots may be taken by juniors only while adult coverage holds.
            var jsaLegalHere = slot.Role != SlotRoles.JSA || JsaAdultCoverOk(slot.Start, slot.End);
            var whole = CandidatesFor(slot.Role, slot.Start, slot.End).Find(e => e.Role != Roles.JSA || jsaLegalHere);
            if (whole is not null)
            {
                Commit(whole, slot.Start, slot.End, false);
                slotFilled.Add(slot.Id);
                servingRole.Set(whole.Id, slot.Role);
                continue;
            }

            // ALLOCATE phases stop at whole-slot assignment - the slot stays open for
            // the next phase. Only the final fill may reshape or mark it UNFILLED.
            if (!finalPass) continue;

            // SPLIT (reshape, cost-neutral): cut the slot at a grid boundary so two
            // people can cover it end-to-end - both pieces ≥3h, coverage identical.
            var len = slot.End - slot.Start;
            var splitDone = false;
            if (len >= 2 * R.MinPieceMin)
            {
                var mid = slot.Start + Js.Round(len / 2 / BLOCK_MIN) * BLOCK_MIN;
                var cuts = new List<double>();
                for (double off = 0; ; off += BLOCK_MIN)
                {
                    var lo = mid - off;
                    var hi = mid + off;
                    var any = false;
                    foreach (var x in off == 0 ? new[] { mid } : new[] { lo, hi })
                    {
                        if (x - slot.Start >= R.MinPieceMin && slot.End - x >= R.MinPieceMin)
                        {
                            cuts.Add(x);
                            any = true;
                        }
                    }
                    if (!any && off > 0) break;
                }
                foreach (var x in cuts)
                {
                    var first = CandidatesFor(slot.Role, slot.Start, x).Find(e =>
                        e.Role != Roles.JSA || (slot.Role == SlotRoles.JSA && JsaAdultCoverOk(slot.Start, x)));
                    if (first is null) continue;
                    Commit(first, slot.Start, x, false); // commit so the 2nd search excludes them
                    var second = CandidatesFor(slot.Role, x, slot.End).Find(e =>
                        e.Role != Roles.JSA || (slot.Role == SlotRoles.JSA && JsaAdultCoverOk(x, slot.End)));
                    if (second is not null)
                    {
                        Commit(second, x, slot.End, false);
                        slotFilled.Add(slot.Id);
                        servingRole.Set(first.Id, slot.Role);
                        servingRole.Set(second.Id, slot.Role);
                        splitDone = true;
                        break;
                    }
                    // Roll back the first piece - this cut doesn't work.
                    var undone = shifts[^1];
                    shifts.RemoveAt(shifts.Count - 1);
                    usedToday.Delete(undone.EmployeeId);
                    var led = ledgers[undone.EmployeeId];
                    led.PaidHoursByWeek[day.WeekIndex] -= PaidHoursOf(
                        undone.Role,
                        breakRules[undone.Role],
                        undone.Start,
                        undone.End);
                    led.DaysWorked.Delete(di);
                    led.ShiftEndByDay.Delete(di);
                }
            }
            if (splitDone) continue;

            // MERGE (reshape, cost-neutral): combine with an adjacent same-role slot
            // when one person can span both - fewer bodies, identical coverage.
            var neighbour = orderedSlots.Find(o =>
                !slotFilled.Has(o.Id) &&
                o.Id != slot.Id &&
                o.Role == slot.Role &&
                (o.Start == slot.End || o.End == slot.Start));
            if (neighbour is not null)
            {
                var mStart = Math.Min(slot.Start, neighbour.Start);
                var mEnd = Math.Max(slot.End, neighbour.End);
                var cand = CandidatesFor(slot.Role, mStart, mEnd).Find(e =>
                    e.Role != Roles.JSA || (slot.Role == SlotRoles.JSA && JsaAdultCoverOk(mStart, mEnd)));
                if (cand is not null)
                {
                    Commit(cand, mStart, mEnd, false);
                    slotFilled.Add(slot.Id);
                    slotFilled.Add(neighbour.Id);
                    servingRole.Set(cand.Id, slot.Role);
                    continue;
                }
            }

            // MERGE INTO A NEIGHBOUR'S SHIFT: an adjacent slot may already be filled -
            // extend that person's shift to absorb this one (the interview's "combine
            // the 6-11 and 11-3 into one 6-3 shift" when bodies are short). Same
            // hours-for-coverage, one fewer head needed.
            if (TryExtendNeighbourInto(slot))
            {
                slotFilled.Add(slot.Id);
                continue;
            }

            // EMERGENCY: before declaring the slot open, lift the soft caps - a
            // second spare manager, or a benched casual, beats external cover. The
            // junior's HARD limits (window, length, weekly cap, >=3 adults on the
            // floor) are still enforced by Fits() and jsaLegalHere.
            var rescuer = CandidatesFor(slot.Role, slot.Start, slot.End, true).Find(e => e.Role != Roles.JSA || jsaLegalHere);
            if (rescuer is not null)
            {
                Commit(rescuer, slot.Start, slot.End, false);
                slotFilled.Add(slot.Id);
                servingRole.Set(rescuer.Id, slot.Role);
                continue;
            }

            // UNFILLED: the slot stays visible as an open shift needing external cover.
            var label = slot.Role == SlotRoles.MGR ? "No manager available to cover" : "Understaffed";
            var msg = $"{label} {Fmt(slot.Start)}–{Fmt(slot.End)} - external cover may be required";
            if (!flags.Contains(msg)) flags.Add(msg);
            if (!flags.Any(f => f.StartsWith("Why: ", StringComparison.Ordinal))) flags.Add(WhyParts());
            shifts.Add(UnfilledShift(slot));
            slotFilled.Add(slot.Id);
        }

        // Record which slot-role class each committed shift ended up serving.
        // (Painting happens once at the end of RunFortnightStage2, after every
        // phase - including the repair - has finished moving shifts.)
        foreach (var s in shifts)
        {
            if (!s.Unfilled && s.SlotRole is null)
            {
                s.SlotRole = servingRole.Get(s.EmployeeId) ?? SlotRoles.SA;
            }
        }
    }
}
