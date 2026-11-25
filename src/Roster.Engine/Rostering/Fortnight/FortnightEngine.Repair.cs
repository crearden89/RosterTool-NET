// Port of src/lib/stage2/fortnight.ts (repairFortnight: the fortnight repair
// pass and the junior rescue).

using Roster.Engine.Core;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

public static partial class FortnightEngine
{
    // ─── Fortnight repair pass ───────────────────────────────────────────────────
    // Local-search repair over the filled fortnight. Three move types, applied
    // best-first until no move improves the weighted shortfall:
    //   CLAIM    - an under-contract person takes an UNFILLED slot on a day off;
    //   SWAP     - two same-day shifts exchange holders (a short-shifted manager
    //              takes the long slot an on-contract SA is holding);
    //   TRANSFER - a whole shift moves to an under-contract person's free day.
    // The objective is Σ weight(e)·shortfall(e) with weight = managers >> bigger
    // contracts > smaller, so hours flow to the SM/ASM/DSM cohort first and any
    // unavoidable residue settles on the SMALLEST contracts - the interview rule,
    // now enforced globally instead of hoped-for locally.
    //
    // Every move re-checks the hard rules: slot-role compatibility (a MGR slot
    // only ever goes to a manager), availability windows, leave/day-off, the
    // 5-days-per-week and 6-consecutive-day caps, the per-shift PAID maximum, and
    // the 11-hour rest gap against BOTH neighbouring days. The gap is hard here
    // even for the rest-gap-EXEMPT SM/ASM: clopening is legal for them but the
    // standing rule is that it is AVOIDED, and a repair that quietly manufactured
    // clopens to close the last contract hours would trade a visible problem for
    // a hidden one. If someone's final hours are only reachable by clopening
    // them, the under-contract flag says so and the manager decides by hand.
    // Locked shifts (fixed rosters, manual pins) and JSA-held shifts are never
    // touched.
    private static JsSet<int> RepairFortnight(
        FortnightInput input,
        JsMap<string, DayRoster> byDate,
        JsMap<string, Ledger> ledgers) => new Repair(input, byDate, ledgers).Execute();

    private enum Fit { Ok, Hard, GapPrev, GapNext }

    private sealed class Move(double gain, Action apply)
    {
        public double Gain { get; } = gain;
        public Action Apply { get; } = apply;
    }

    private sealed class Repair
    {
        private readonly List<DayContext> days;
        private readonly List<FortnightEmp> employees;
        private readonly IReadOnlyDictionary<string, BreakRule> breakRules;
        private readonly JsMap<string, DayRoster> byDate;
        private readonly JsMap<string, Ledger> ledgers;
        private readonly JsMap<string, FortnightEmp> empById;
        private readonly JsSet<int> dirty = new();
        private readonly JsMap<string, double[]> targetWeekCache = new();
        // empId → day index → their shift (one per day by construction).
        private readonly JsMap<string, JsMap<int, FShift>> shiftAt = new();

        public Repair(FortnightInput input, JsMap<string, DayRoster> byDate, JsMap<string, Ledger> ledgers)
        {
            days = input.Days;
            employees = input.Employees;
            breakRules = input.BreakRules;
            this.byDate = byDate;
            this.ledgers = ledgers;
            empById = new JsMap<string, FortnightEmp>(employees.Select(e => (e.Id, e)));
            for (var di = 0; di < days.Count; di++)
            {
                var r = byDate.Get(days[di].Date);
                if (r is null) continue;
                foreach (var s in r.Shifts)
                {
                    if (s.Unfilled) continue;
                    if (!shiftAt.Has(s.EmployeeId)) shiftAt.Set(s.EmployeeId, new JsMap<int, FShift>());
                    shiftAt[s.EmployeeId].Set(di, s);
                }
            }
        }

        // Per-week paid target net of leave + public-holiday credit; shortfall
        // against it. Leave and PH credit never change during repair (moves shuffle
        // SHIFTS, not availability), so the target is memoised once per person
        // instead of re-derived inside the cubic search loops below - where its
        // 14-day scans were the dominant cost of the whole repair pass.
        private double TargetWeek(FortnightEmp e, int w)
        {
            if (!targetWeekCache.TryGet(e.Id, out var t))
            {
                t =
                [
                    Math.Max(0, WeeklyContractOf(e) - LeaveHoursInWeek(e, days, 0) - PhCreditInWeek(e, days, 0)),
                    Math.Max(0, WeeklyContractOf(e) - LeaveHoursInWeek(e, days, 1) - PhCreditInWeek(e, days, 1)),
                ];
                targetWeekCache.Set(e.Id, t);
            }
            return t[w];
        }

        private double Shortfall(FortnightEmp e)
        {
            var led = ledgers[e.Id];
            return
                Math.Max(0, TargetWeek(e, 0) - led.PaidHoursByWeek[0]) +
                Math.Max(0, TargetWeek(e, 1) - led.PaidHoursByWeek[1]);
        }

        private static double Weight(FortnightEmp e) => (CoreManager(e.Role) ? 1000 : 0) + e.ContractHours;

        // Objective decrease when `e`'s paid hours in week w change by `delta`.
        private double GainOf(FortnightEmp e, int w, double delta)
        {
            var led = ledgers[e.Id];
            var before = Math.Max(0, TargetWeek(e, w) - led.PaidHoursByWeek[w]);
            var after = Math.Max(0, TargetWeek(e, w) - (led.PaidHoursByWeek[w] + delta));
            return Weight(e) * (before - after);
        }

        // EXACT objective decrease for a batch of paid-hour deltas. Two deltas that
        // land on the SAME person's SAME week do not compose through GainOf (the
        // second is evaluated against a stale ledger), and a compound move whose
        // estimated gain disagrees with its real effect can cycle the search
        // forever. This aggregates per (person, week) first, then prices the batch
        // once.
        private double GainExact(params (FortnightEmp E, int W, double Delta)[] entries)
        {
            var byEmp = new JsMap<string, (FortnightEmp E, double[] D)>();
            foreach (var (e, w, delta) in entries)
            {
                (FortnightEmp E, double[] D) cur = byEmp.TryGet(e.Id, out var c) ? c : (e, new double[] { 0, 0 });
                cur.D[w] += delta;
                byEmp.Set(e.Id, cur);
            }
            double total = 0;
            foreach (var (e, d) in byEmp.Values())
            {
                var led = ledgers[e.Id];
                foreach (var w in new[] { 0, 1 })
                {
                    if (d[w] == 0) continue;
                    var before = Math.Max(0, TargetWeek(e, w) - led.PaidHoursByWeek[w]);
                    var after = Math.Max(0, TargetWeek(e, w) - (led.PaidHoursByWeek[w] + d[w]));
                    total += Weight(e) * (before - after);
                }
            }
            return total;
        }

        private double PaidOf(FortnightEmp e, double start, double end) =>
            PaidHoursOf(e.Role, breakRules[e.Role], start, end);

        private FShift? ShiftOf(string empId, int d) =>
            shiftAt.TryGet(empId, out var m) && m.TryGet(d, out var s) ? s : null;

        private bool Works(string empId, int d) => shiftAt.TryGet(empId, out var m) && m.Has(d);

        private static bool SlotOk(FortnightEmp e, string? role)
        {
            if (role == SlotRoles.MGR) return Roles.IsManager(e.Role);
            return IsAdult(e.Role); // SA slots and adult-down-covered JSA slots
        }

        // May `e` hold [start,end) on day di (ignoring who holds it now)?
        // `allowExemptClopen` lets a rest-gap-EXEMPT role take a span inside the
        // 11-hour window of its own neighbouring shifts - the fallback tier.
        // `vacatingDi` marks a day whose shift `e` is GIVING UP as part of the same
        // move (a day exchange), so its neighbours must not be counted against them.
        private bool CanHold(FortnightEmp e, int di, double start, double end, bool allowExemptClopen, int vacatingDi = -99)
        {
            var day = days[di];
            var a = day.Avail.Get(e.Id);
            if (a is null || !a.Available || a.Fixed is not null) return false;
            if (a.FromMin is { } from && from > start) return false;
            if (a.ToMin is { } to && to < end) return false;
            var rule = breakRules[e.Role];
            if (PaidOf(e, start, end) > rule.MaxShiftHours + 1e-9) return false;
            if (end - start < Math.Min(R.MinPieceMin, ToBlocks(rule.MinShiftHours) * BLOCK_MIN))
                return false;
            // The week's paid hours with this shift in place of what they hold today
            // (and of the day they are giving up in the same move).
            {
                var w = day.WeekIndex;
                var weekPaid = ledgers[e.Id].PaidHoursByWeek[w] + PaidOf(e, start, end);
                var today = ShiftOf(e.Id, di);
                if (today is not null) weekPaid -= PaidOf(e, today.Start, today.End);
                var given = vacatingDi >= 0 ? ShiftOf(e.Id, vacatingDi) : null;
                if (given is not null && days[vacatingDi].WeekIndex == w && vacatingDi != di) weekPaid -= PaidOf(e, given.Start, given.End);
                if (OverWeeklyCap(rule, weekPaid)) return false;
            }
            // Rest gap against both neighbours (soft for exempt roles in the fallback
            // tier; hard for everyone else always).
            if (!(allowExemptClopen && RestGapExempt(e.Role)))
            {
                var prev = di - 1 == vacatingDi ? null : ShiftOf(e.Id, di - 1);
                if (prev is not null && start < prev.End - DAY_MIN + R.RestGapMin) return false;
                var next = di + 1 == vacatingDi ? null : ShiftOf(e.Id, di + 1);
                if (next is not null && next.Start < end - DAY_MIN + R.RestGapMin) return false;
            }
            return true;
        }

        // May `e` occupy day di given week + consecutive caps, treating `vacatingDi`
        // (a day they are giving up in the same move) as no longer worked?
        private bool CanOccupyDay(FortnightEmp e, int di, int vacatingDi = -99)
        {
            var led = ledgers[e.Id];
            bool WorksOn(int d) => d != vacatingDi && led.DaysWorked.Has(d);
            var inWeek = 0;
            var w0 = days[di].WeekIndex * 7;
            for (var d = w0; d < w0 + 7; d++) if (WorksOn(d)) inWeek++;
            if (inWeek >= R.MaxWorkedPerWeek) return false;
            var run = 1;
            for (var d = di - 1; WorksOn(d); d--) run++;
            for (var d = di + 1; WorksOn(d); d++) run++;
            return run <= MaxConsecFor(e.Role);
        }

        private void ApplyHold(FortnightEmp e, int di, FShift s)
        {
            var w = days[di].WeekIndex;
            var led = ledgers[e.Id];
            led.PaidHoursByWeek[w] += PaidOf(e, s.Start, s.End);
            led.DaysWorked.Add(di);
            led.ShiftEndByDay.Set(di, s.End);
            s.EmployeeId = e.Id;
            s.Name = e.Name;
            s.Role = e.Role;
            s.BreakMinutes = TotalBreakMinutes(breakRules[e.Role], s.Start, s.End);
            s.Unfilled = false;
            if (!shiftAt.Has(e.Id)) shiftAt.Set(e.Id, new JsMap<int, FShift>());
            shiftAt[e.Id].Set(di, s);
            dirty.Add(di);
        }

        private void ApplyRelease(FortnightEmp e, int di, FShift s)
        {
            var w = days[di].WeekIndex;
            var led = ledgers[e.Id];
            led.PaidHoursByWeek[w] -= PaidOf(e, s.Start, s.End);
            led.DaysWorked.Delete(di);
            led.ShiftEndByDay.Delete(di);
            shiftAt[e.Id].Delete(di);
            dirty.Add(di);
        }

        private Move? BestMove(bool allowExemptClopen)
        {
            Move? best = null;
            void Consider(double gain, Action apply)
            {
                if (gain > 0.01 && (best is null || gain > best.Gain)) best = new Move(gain, apply);
            }

            var unders = employees.Where(e => IsAdult(e.Role) && Shortfall(e) > 0.05).ToList();
            if (unders.Count == 0) return null;

            for (var di = 0; di < days.Count; di++)
            {
                var day = days[di];
                var roster = byDate.Get(day.Date);
                if (roster is null || day.Closed) continue;
                var w = day.WeekIndex;

                foreach (var s in roster.Shifts)
                {
                    // CLAIM an unfilled slot.
                    if (s.Unfilled)
                    {
                        foreach (var p in unders)
                        {
                            if (Works(p.Id, di)) continue;
                            if (
                                !SlotOk(p, s.SlotRole) ||
                                !CanHold(p, di, s.Start, s.End, allowExemptClopen) ||
                                !CanOccupyDay(p, di)
                            )
                                continue;
                            var g = GainOf(p, w, PaidOf(p, s.Start, s.End));
                            var (pp, ddi, ss) = (p, di, s);
                            Consider(g, () => ApplyHold(pp, ddi, ss));
                        }
                        continue;
                    }
                    if (s.Locked) continue;
                    var q = empById.Get(s.EmployeeId);
                    if (q is null) continue;

                    // TRANSFER the whole shift to an under-contract person's free day.
                    // A casual MAY be displaced: adults down-cover JSA slots legally, and
                    // a junior holding hours an under-contract adult can work is exactly
                    // the substitution the over-resourced rule exists to make. The greedy
                    // fill could not see it (at that moment no under-contract adult could
                    // take the day), but after repair moves days around, one often can.
                    // Juniors are never the RECEIVER - `unders` is adults only - so a JSA
                    // still never moves up.
                    if (q.Role != Roles.JSA)
                    {
                        // Never strip an ADULT's only shift: an active adult must not print
                        // an empty roster, however small their contract weighs. Casuals have
                        // no contract to protect.
                        if ((shiftAt.Get(s.EmployeeId)?.Size ?? 0) <= 1) continue;
                    }
                    foreach (var p in unders)
                    {
                        if (p.Id == q.Id || Works(p.Id, di)) continue;
                        if (
                            !SlotOk(p, s.SlotRole) ||
                            !CanHold(p, di, s.Start, s.End, allowExemptClopen) ||
                            !CanOccupyDay(p, di)
                        )
                            continue;
                        var g = GainOf(p, w, PaidOf(p, s.Start, s.End)) + GainOf(q, w, -PaidOf(q, s.Start, s.End));
                        var (pp, qq, ddi, ss) = (p, q, di, s);
                        Consider(g, () =>
                        {
                            ApplyRelease(qq, ddi, ss);
                            ApplyHold(pp, ddi, ss);
                        });
                    }
                }

                // SWAP holders of two same-day shifts.
                var swappable = roster.Shifts
                    .Where(s => !s.Unfilled && !s.Locked && empById.Get(s.EmployeeId)?.Role != Roles.JSA)
                    .ToList();
                for (var i = 0; i < swappable.Count; i++)
                {
                    for (var jj = i + 1; jj < swappable.Count; jj++)
                    {
                        var s1 = swappable[i];
                        var s2 = swappable[jj];
                        var a = empById[s1.EmployeeId];
                        var b = empById[s2.EmployeeId];
                        if (!SlotOk(a, s2.SlotRole) || !SlotOk(b, s1.SlotRole)) continue;
                        var g =
                            GainOf(a, w, PaidOf(a, s2.Start, s2.End) - PaidOf(a, s1.Start, s1.End)) +
                            GainOf(b, w, PaidOf(b, s1.Start, s1.End) - PaidOf(b, s2.Start, s2.End));
                        if (g <= 0.01) continue;
                        var fitA = SwapFit(a, di, s2, allowExemptClopen, BaseHeld);
                        var fitB = SwapFit(b, di, s1, allowExemptClopen, BaseHeld);
                        var ddi = di;
                        if (fitA == Fit.Ok && fitB == Fit.Ok)
                        {
                            Consider(g, () =>
                            {
                                ApplyRelease(a, ddi, s1);
                                ApplyRelease(b, ddi, s2);
                                ApplyHold(a, ddi, s2);
                                ApplyHold(b, ddi, s1);
                            });
                            continue;
                        }
                        // COMPOUND UNLOCK: the swap pays but one side is blocked ONLY by the
                        // 11-hour gap against a neighbouring day's shift. A same-day
                        // reshuffle on THAT day often clears it at zero cost - strict
                        // improvement alone can never take the zero-gain step, so evaluate
                        // the pair as one move. One level deep, first legal secondary wins.
                        (FortnightEmp E, FShift Take, int Dn)? blocked =
                            fitA is Fit.GapPrev or Fit.GapNext
                                ? (a, s2, di + (fitA == Fit.GapNext ? 1 : -1))
                                : fitB is Fit.GapPrev or Fit.GapNext
                                    ? (b, s1, di + (fitB == Fit.GapNext ? 1 : -1))
                                    : null;
                        var otherFitOk = blocked is { } bl
                            ? ReferenceEquals(bl.E, a)
                                ? fitB == Fit.Ok
                                : fitA == Fit.Ok
                            : false;
                        if (blocked is not { } blk || !otherFitOk) continue;
                        var dn = blk.Dn;
                        if (dn < 0 || dn >= days.Count) continue;
                        var nRoster = byDate.Get(days[dn].Date);
                        var mine = ShiftOf(blk.E.Id, dn);
                        if (nRoster is null || mine is null || mine.Locked) continue;
                        foreach (var t in nRoster.Shifts)
                        {
                            if (ReferenceEquals(t, mine) || t.Unfilled || t.Locked) continue;
                            var o = empById.Get(t.EmployeeId);
                            if (o is null || o.Role == Roles.JSA || o.Id == a.Id || o.Id == b.Id) continue;
                            if (!SlotOk(blk.E, t.SlotRole) || !SlotOk(o, mine.SlotRole)) continue;
                            // Final-state spans after BOTH swaps, for gap checks.
                            FShift? Held(string empId, int d)
                            {
                                if (empId == a.Id && d == ddi) return s2;
                                if (empId == b.Id && d == ddi) return s1;
                                if (empId == blk.E.Id && d == dn) return t;
                                if (empId == o.Id && d == dn) return mine;
                                return ShiftOf(empId, d);
                            }
                            if (SwapFit(blk.E, dn, t, allowExemptClopen, Held) != Fit.Ok) continue;
                            if (SwapFit(o, dn, mine, allowExemptClopen, Held) != Fit.Ok) continue;
                            if (SwapFit(a, di, s2, allowExemptClopen, Held) != Fit.Ok) continue;
                            if (SwapFit(b, di, s1, allowExemptClopen, Held) != Fit.Ok) continue;
                            var wn = days[dn].WeekIndex;
                            var gTotal = GainExact(
                                // Primary swap deltas...
                                (a, w, PaidOf(a, s2.Start, s2.End) - PaidOf(a, s1.Start, s1.End)),
                                (b, w, PaidOf(b, s1.Start, s1.End) - PaidOf(b, s2.Start, s2.End)),
                                // ...plus the neighbour-day unlock, priced as ONE batch so a
                                // person appearing in both is aggregated before clamping.
                                (blk.E, wn, PaidOf(blk.E, t.Start, t.End) - PaidOf(blk.E, mine.Start, mine.End)),
                                (o, wn, PaidOf(o, mine.Start, mine.End) - PaidOf(o, t.Start, t.End)));
                            if (gTotal <= 0.01) continue;
                            var e2 = blk.E;
                            var (tt, oo) = (t, o);
                            Consider(gTotal, () =>
                            {
                                ApplyRelease(e2, dn, mine);
                                ApplyRelease(oo, dn, tt);
                                ApplyHold(e2, dn, tt);
                                ApplyHold(oo, dn, mine);
                                ApplyRelease(a, ddi, s1);
                                ApplyRelease(b, ddi, s2);
                                ApplyHold(a, ddi, s2);
                                ApplyHold(b, ddi, s1);
                            });
                            break;
                        }
                    }
                }
            }

            // 3-ROTATION (same day): A takes B's shift, B takes C's, C takes A's.
            // Needed where a 2-swap hits the role wall - a manager stuck on a short
            // MGR slot cannot swap with the SA holding the day's long slot, because
            // the SA can't legally take the MGR slot back. Rotating through a third
            // manager-compatible holder resolves it. Neighbour days are untouched by
            // a same-day rotation, so the plain rest-gap view applies.
            for (var di = 0; di < days.Count; di++)
            {
                var day = days[di];
                var roster = byDate.Get(day.Date);
                if (roster is null || day.Closed) continue;
                var w = day.WeekIndex;
                var rot = roster.Shifts
                    .Where(s => !s.Unfilled && !s.Locked && empById.Get(s.EmployeeId)?.Role != Roles.JSA)
                    .ToList();
                if (rot.Count < 3) continue;
                var ddi = di;
                foreach (var sA in rot)
                {
                    foreach (var sB in rot)
                    {
                        if (ReferenceEquals(sB, sA)) continue;
                        foreach (var sC in rot)
                        {
                            if (ReferenceEquals(sC, sA) || ReferenceEquals(sC, sB)) continue;
                            var A = empById[sA.EmployeeId];
                            var B = empById[sB.EmployeeId];
                            var C = empById[sC.EmployeeId];
                            if (Shortfall(A) <= 0.05 && Shortfall(B) <= 0.05 && Shortfall(C) <= 0.05) continue;
                            if (!SlotOk(A, sB.SlotRole) || !SlotOk(B, sC.SlotRole) || !SlotOk(C, sA.SlotRole))
                                continue;
                            if (
                                SwapFit(A, di, sB, allowExemptClopen, BaseHeld) != Fit.Ok ||
                                SwapFit(B, di, sC, allowExemptClopen, BaseHeld) != Fit.Ok ||
                                SwapFit(C, di, sA, allowExemptClopen, BaseHeld) != Fit.Ok
                            )
                                continue;
                            var g = GainExact(
                                (A, w, PaidOf(A, sB.Start, sB.End) - PaidOf(A, sA.Start, sA.End)),
                                (B, w, PaidOf(B, sC.Start, sC.End) - PaidOf(B, sB.Start, sB.End)),
                                (C, w, PaidOf(C, sA.Start, sA.End) - PaidOf(C, sC.Start, sC.End)));
                            var (xa, xb, xc) = (sA, sB, sC);
                            Consider(g, () =>
                            {
                                ApplyRelease(A, ddi, xa);
                                ApplyRelease(B, ddi, xb);
                                ApplyRelease(C, ddi, xc);
                                ApplyHold(A, ddi, xb);
                                ApplyHold(B, ddi, xc);
                                ApplyHold(C, ddi, xa);
                            });
                        }
                    }
                }
            }

            // DAY EXCHANGE: A works d1 (not d2), B works d2 (not d1) - they trade
            // whole days. This is what single-shift moves cannot reach: two people of
            // EQUAL weight both under contract block each other on every same-day
            // swap (zero net), yet trading their days can put each on the slot mix
            // that fits their contract. Restricted to pairs where at least one side
            // is under, like every other move.
            var movable = new List<(int Di, FShift S, FortnightEmp E)>();
            for (var di = 0; di < days.Count; di++)
            {
                var roster = byDate.Get(days[di].Date);
                if (roster is null || days[di].Closed) continue;
                foreach (var s in roster.Shifts)
                {
                    if (s.Unfilled || s.Locked) continue;
                    var e = empById.Get(s.EmployeeId);
                    if (e is null || e.Role == Roles.JSA) continue;
                    movable.Add((di, s, e));
                }
            }
            foreach (var x in movable)
            {
                foreach (var y in movable)
                {
                    if (x.Di >= y.Di || x.E.Id == y.E.Id) continue;
                    var (d1, s1, a) = x;
                    var (d2, s2, b) = y;
                    if (Shortfall(a) <= 0.05 && Shortfall(b) <= 0.05) continue;
                    if (Works(a.Id, d2) || Works(b.Id, d1)) continue;
                    if (!SlotOk(a, s2.SlotRole) || !SlotOk(b, s1.SlotRole)) continue;
                    if (!CanHold(a, d2, s2.Start, s2.End, allowExemptClopen, d1)) continue;
                    if (!CanHold(b, d1, s1.Start, s1.End, allowExemptClopen, d2)) continue;
                    if (!CanOccupyDay(a, d2, d1) || !CanOccupyDay(b, d1, d2)) continue;
                    var w1 = days[d1].WeekIndex;
                    var w2 = days[d2].WeekIndex;
                    // Per-week deltas (a leaves w1, joins w2; b the reverse). Same-week
                    // pairs collapse to a single delta per person.
                    var gA =
                        w1 == w2
                            ? GainOf(a, w1, PaidOf(a, s2.Start, s2.End) - PaidOf(a, s1.Start, s1.End))
                            : GainOf(a, w1, -PaidOf(a, s1.Start, s1.End)) +
                              GainOf(a, w2, PaidOf(a, s2.Start, s2.End));
                    var gB =
                        w1 == w2
                            ? GainOf(b, w1, PaidOf(b, s1.Start, s1.End) - PaidOf(b, s2.Start, s2.End))
                            : GainOf(b, w2, -PaidOf(b, s2.Start, s2.End)) +
                              GainOf(b, w1, PaidOf(b, s1.Start, s1.End));
                    Consider(gA + gB, () =>
                    {
                        ApplyRelease(a, d1, s1);
                        ApplyRelease(b, d2, s2);
                        ApplyHold(a, d2, s2);
                        ApplyHold(b, d1, s1);
                    });
                }
            }
            return best;
        }

        // Like CanHold but for a swap on a day the person ALREADY works: skip the
        // adds-a-day checks, keep windows, the paid cap and the rest gap against the
        // two neighbouring days. `held` supplies the neighbouring shifts so compound
        // moves can evaluate the FINAL state; the return distinguishes a pure
        // rest-gap block (unlockable by reshuffling the neighbour day) from a hard
        // one.
        private FShift? BaseHeld(string empId, int d) => ShiftOf(empId, d);

        private Fit SwapFit(FortnightEmp e, int di, FShift s, bool allowExemptClopen, Func<string, int, FShift?> held)
        {
            var day = days[di];
            var a = day.Avail.Get(e.Id);
            if (a is null || a.Fixed is not null) return Fit.Hard;
            if (a.FromMin is { } from && from > s.Start) return Fit.Hard;
            if (a.ToMin is { } to && to < s.End) return Fit.Hard;
            var rule = breakRules[e.Role];
            if (PaidOf(e, s.Start, s.End) > rule.MaxShiftHours + 1e-9) return Fit.Hard;
            var today = ShiftOf(e.Id, di);
            var weekPaid =
                ledgers[e.Id].PaidHoursByWeek[day.WeekIndex] + PaidOf(e, s.Start, s.End) - (today is not null ? PaidOf(e, today.Start, today.End) : 0);
            if (OverWeeklyCap(rule, weekPaid)) return Fit.Hard;
            if (!(allowExemptClopen && RestGapExempt(e.Role)))
            {
                var prev = held(e.Id, di - 1);
                if (prev is not null && s.Start < prev.End - DAY_MIN + R.RestGapMin) return Fit.GapPrev;
                var next = held(e.Id, di + 1);
                if (next is not null && next.Start < s.End - DAY_MIN + R.RestGapMin) return Fit.GapNext;
            }
            return Fit.Ok;
        }

        public JsSet<int> Execute()
        {
            var rounds = 0;
            for (var round = 0; round < 200; round++)
            {
                var move = BestMove(false);
                if (move is null) break;
                move.Apply();
                rounds++;
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUDIT_DEBUG")))
            {
                var rem = employees
                    .Where(e => IsAdult(e.Role) && Shortfall(e) > 0.05)
                    .Select(e => $"{e.Name}:{Js.ToFixed(Shortfall(e), 1)}");
                Console.WriteLine($"[repair] rounds={N(rounds)} remaining=[{string.Join(", ", rem)}]");
            }

            Rescue();

            // Rebuild the unfilled/Why flags for days the repair touched - a claimed
            // slot's "external cover" line must not survive it.
            foreach (var di in dirty)
            {
                var roster = byDate.Get(days[di].Date);
                if (roster is null) continue;
                roster.Flags = roster.Flags
                    .Where(f => !f.EndsWith("external cover may be required", StringComparison.Ordinal) && !f.StartsWith("Why: ", StringComparison.Ordinal))
                    .ToList();
                foreach (var s in roster.Shifts)
                {
                    if (!s.Unfilled) continue;
                    var label = s.SlotRole == SlotRoles.MGR ? "No manager available to cover" : "Understaffed";
                    roster.Flags.Add($"{label} {Fmt(s.Start)}–{Fmt(s.End)} - external cover may be required");
                }
            }
            return dirty;
        }

        // RESCUE: a slot still open after repair may now be coverable even though it
        // was not during the fill - jsaAdultCoverOk is evaluated against the adults
        // placed SO FAR, so a junior refused mid-fill can be perfectly legal once the
        // day is complete and repair has finished moving people. Adults come first
        // everywhere above; this is the last step before declaring a slot uncoverable,
        // and every junior HARD limit - window, length, weekly cap, >=3 adults in
        // every block - is enforced.
        private void Rescue()
        {
            for (var di = 0; di < days.Count; di++)
            {
                var day = days[di];
                var roster = byDate.Get(day.Date);
                if (roster is null || day.Closed) continue;
                if (!roster.Shifts.Any(x => x.Unfilled)) continue;
                var j = JsaWindow(day.IsSchoolDay);

                // Adults on the floor at `m`, optionally with one shift's span rewritten
                // (used to price a move before committing to it).
                int AdultsAt(double m, (FShift From, double Start, double End)? moved = null)
                {
                    var c = 0;
                    foreach (var x in roster.Shifts)
                    {
                        if (x.Unfilled || !IsAdult(x.Role)) continue;
                        var isMoved = moved is { } mv && ReferenceEquals(x, mv.From);
                        var st = isMoved ? moved!.Value.Start : x.Start;
                        var en = isMoved ? moved!.Value.End : x.End;
                        c += st <= m && m < en ? 1 : 0;
                    }
                    return c;
                }

                // A junior who may legally work [start,end) today, given the adult
                // presence that will exist after any accompanying move.
                FortnightEmp? JuniorFor(double start, double end, Func<double, int> cover)
                {
                    for (var m = start; m < end; m += BLOCK_MIN)
                    {
                        if (cover(m) < R.MinAdultsWithJsa) return null;
                    }
                    if (start < j.Start || end > j.Latest || end - start > j.MaxLenMin) return null;
                    if (end - start < R.MinPieceMin) return null;
                    return employees.Find(e =>
                    {
                        if (e.Role != Roles.JSA) return false;
                        if (Works(e.Id, di)) return false;
                        var a = day.Avail.Get(e.Id);
                        if (a is null || !a.Available || a.Fixed is not null) return false;
                        var rule = breakRules[e.Role];
                        var pay = PaidOf(e, start, end);
                        if (pay > rule.MaxShiftHours + 1e-9) return false;
                        var led = ledgers[e.Id];
                        if (pay > JsaWeeklyRemaining(day, led) + 1e-9) return false;
                        return CanOccupyDay(e, di) && CanHold(e, di, start, end, false);
                    });
                }

                foreach (var slot in roster.Shifts.Where(x => x.Unfilled).ToList())
                {
                    // (a) Straight rescue - a junior takes the open slot as it stands. Only a
                    // JUNIOR slot: a manager slot needs a manager and a store-assistant slot
                    // carries register work a junior may not do. Checking the junior's hours
                    // but not the slot made a junior the closing manager (15:00-21:00, no
                    // manager in the store) and put another on the registers.
                    var direct =
                        slot.SlotRole == SlotRoles.JSA ? JuniorFor(slot.Start, slot.End, m => AdultsAt(m)) : null;
                    if (direct is not null)
                    {
                        ApplyHold(direct, di, slot);
                        continue;
                    }

                    // (b) STEP-ACROSS rescue: no junior can take THIS slot, but an adult
                    // already working today may be able to - freeing THEIR shift for a
                    // junior instead. The open slot is often the late one, where adult
                    // presence thins below the 3-adult floor a junior needs; an adult has no
                    // such floor, so moving the adult late and putting the junior on the
                    // busy midday shift they vacate covers both. (At G999 this is exactly
                    // the 08/08 case: the 06:00 shift cannot go to a junior - only two
                    // adults are in at 06:00 - but the 12:00-17:30 one can.)
                    foreach (var donor in roster.Shifts)
                    {
                        if (ReferenceEquals(donor, slot) || donor.Unfilled || donor.Locked) continue;
                        // The junior inherits the DONOR's shift, so that shift must itself be a
                        // junior slot - otherwise an adult leaving the opener manager slot
                        // would hand it to a junior.
                        if (donor.SlotRole != SlotRoles.JSA) continue;
                        var adult = empById.Get(donor.EmployeeId);
                        if (adult is null || adult.Role == Roles.JSA) continue;
                        if (!SlotOk(adult, slot.SlotRole)) continue;
                        if (SwapFit(adult, di, slot, false, BaseHeld) != Fit.Ok) continue;
                        var junior = JuniorFor(donor.Start, donor.End, m =>
                            AdultsAt(m, (donor, slot.Start, slot.End)));
                        if (junior is null) continue;
                        var w = day.WeekIndex;
                        // Only worth doing if it does not push the adult further from contract
                        // than leaving the slot open would - a shorter shift for a
                        // contract-hungry adult is a real cost.
                        var delta = PaidOf(adult, slot.Start, slot.End) - PaidOf(adult, donor.Start, donor.End);
                        if (GainOf(adult, w, delta) < -0.01) continue;
                        ApplyRelease(adult, di, donor);
                        ApplyHold(adult, di, slot);
                        ApplyHold(junior, di, donor);
                        break;
                    }
                    if (!slot.Unfilled) continue;

                    // (c) DAY-EXCHANGE rescue: nobody free today may take the slot, because
                    // everyone who could has already spent their days this week (5 a week,
                    // 6 in a row). Days are filled in a fixed order, so the last days of
                    // the order (Wednesday, Thursday by default) are where this lands: on
                    // the live stores a Thursday morning load crew went to external cover
                    // while the same people worked a shift on another day that a colleague
                    // with a day to spare could have taken. Give that other day's shift to
                    // the colleague, and the freed person takes this slot. Every hard rule
                    // holds for both people; contract effects only choose between
                    // candidates, because an open slot costs more than either.
                    var wk = day.WeekIndex;
                    // May `taker` (not working day dj) take over shift `give` on day dj?
                    bool TakerOk(FortnightEmp taker, int dj, FShift give)
                    {
                        if (Works(taker.Id, dj)) return false;
                        var dayJ = days[dj];
                        if (taker.Role == Roles.JSA)
                        {
                            if (give.SlotRole != SlotRoles.JSA) return false;
                            var jj = JsaWindow(dayJ.IsSchoolDay);
                            if (give.Start < jj.Start || give.End > jj.Latest || give.End - give.Start > jj.MaxLenMin) return false;
                            if (PaidOf(taker, give.Start, give.End) > JsaWeeklyRemaining(dayJ, ledgers[taker.Id]) + 1e-9) return false;
                            var rosterJ = byDate.Get(dayJ.Date);
                            for (var m = give.Start; m < give.End; m += BLOCK_MIN)
                            {
                                var adults = 0;
                                foreach (var x in rosterJ?.Shifts ?? [])
                                    if (!ReferenceEquals(x, give) && !x.Unfilled && IsAdult(x.Role) && x.Start <= m && m < x.End) adults++;
                                if (adults < R.MinAdultsWithJsa) return false;
                            }
                        }
                        else if (!SlotOk(taker, give.SlotRole)) return false;
                        return CanOccupyDay(taker, dj) && CanHold(taker, dj, give.Start, give.End, false);
                    }
                    Move? pick = null;
                    void Offer(FortnightEmp mover, int dj, FShift give, FortnightEmp taker)
                    {
                        var gain = GainExact(
                            (mover, wk, PaidOf(mover, slot.Start, slot.End) - PaidOf(mover, give.Start, give.End)),
                            (taker, wk, PaidOf(taker, give.Start, give.End)));
                        if (pick is null || gain > pick.Gain)
                            pick = new Move(gain, () =>
                            {
                                ApplyRelease(mover, dj, give);
                                ApplyHold(taker, dj, give);
                                ApplyHold(mover, di, slot);
                            });
                    }
                    foreach (var mover in employees)
                    {
                        if (!SlotOk(mover, slot.SlotRole)) continue;
                        if (Works(mover.Id, di)) continue;
                        foreach (var (dj, give) in shiftAt.Get(mover.Id)?.Entries() ?? [])
                        {
                            if (dj == di || days[dj].WeekIndex != wk || give.Locked) continue;
                            if (days[dj].Avail.Get(mover.Id)?.Fixed is not null) continue;
                            if (!CanOccupyDay(mover, di, dj) || !CanHold(mover, di, slot.Start, slot.End, false, dj)) continue;
                            foreach (var taker in employees) if (taker.Id != mover.Id && TakerOk(taker, dj, give)) Offer(mover, dj, give, taker);
                        }
                    }
                    // (d) SAME-DAY step-across for adults: someone already working today
                    // can take the open slot (the rest gap after yesterday's close, say,
                    // only bars the others from it), and a colleague with a day to spare
                    // takes the shift they leave. At G351 on a Sunday whose Saturday closers
                    // could not open, the Sunday morning crew went to external cover while
                    // the Sunday afternoon was held by people who could have opened.
                    if (pick is null)
                        foreach (var mover in employees)
                        {
                            if (!SlotOk(mover, slot.SlotRole)) continue;
                            var own = ShiftOf(mover.Id, di);
                            if (own is null || own.Locked || day.Avail.Get(mover.Id)?.Fixed is not null) continue;
                            if (!CanHold(mover, di, slot.Start, slot.End, false)) continue;
                            foreach (var taker in employees) if (taker.Id != mover.Id && TakerOk(taker, di, own)) Offer(mover, di, own, taker);
                        }
                    pick?.Apply();
                }
            }
        }
    }
}
