// Port of src/lib/stage2/fortnight.ts (keepHeldTiles, paintKeepingHeld,
// breaksDiffer, rescueLostWork and paintDayShifts).

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

public static partial class FortnightEngine
{
    // The held plan's tiles, kept as approved. A roster built from the held plan
    // fills its shifts with real people, and painting them all afresh could come
    // out differently: the painter is a heuristic, and a manager in a store
    // assistant's shift takes a different break, so work the approved plan
    // carried was lost on the roster (Load Running on about 1 day in 6 on the six
    // stores). Each rostered shift with the times of a held shift whose tiles are
    // legal for the person in it - their own break entitlement and placement,
    // their restricted tasks, what their role may do - keeps those tiles exactly.
    // Only the others are painted, with the work the kept tiles do not carry.
    // A day with an open shift is painted whole, so its work can move.
    // Returns the shifts left to paint and the work left for them, or null when
    // nothing is kept.
    private static (List<FShift> Others, List<DemandBlock> Demand)? KeepHeldTiles(
        DayContext day,
        List<FShift> shifts,
        JsMap<string, FortnightEmp> empById,
        IReadOnlyDictionary<string, BreakRule> breakRules)
    {
        var held = day.HeldShifts;
        if (held is null || held.Count == 0 || shifts.Any(s => s.Unfilled)) return null;
        bool LegalFor(FortnightEmp emp, FShift s, List<ShiftBlock> blocks)
        {
            var breaks = new List<double>();
            foreach (var b in blocks)
            {
                if (b.Time < s.Start || b.Time >= s.End) return false;
                if (b.Group == TaskGroups.Break) breaks.Add(b.Time);
                else if (b.Group != TaskGroups.Flex && b.Group != TaskGroups.NonOp)
                {
                    if (!TaskGroups.EligibleFor(emp.Role, b.Group) || emp.Restricted.Contains(b.Group)) return false;
                }
            }
            return Breaks.BreakTileError(breaks, TotalBreakMinutes(breakRules[emp.Role], s.Start, s.End), s.Start, s.End) is null;
        }
        var used = new JsSet<int>();
        var kept = new List<(FShift S, List<ShiftBlock> Blocks)>();
        var rest = new List<FShift>();
        foreach (var s in shifts)
        {
            var emp = empById.Get(s.EmployeeId);
            var i = -1;
            if (emp is not null)
            {
                for (var hi = 0; hi < held.Count; hi++)
                {
                    var h = held[hi];
                    if (!used.Has(hi) && h.Start == s.Start && h.End == s.End && LegalFor(emp, s, h.Blocks))
                    {
                        i = hi;
                        break;
                    }
                }
            }
            if (i < 0)
            {
                rest.Add(s);
                continue;
            }
            used.Add(i);
            kept.Add((s, held[i].Blocks));
        }
        if (kept.Count == 0) return null;
        // The day's work less what the kept tiles carry.
        var byTime = new JsMap<double, DemandBlock>(day.DemandByBlock.Select(d => (d.Time, new DemandBlock(d.Time, d.Total, d.Groups.Copy()))));
        foreach (var (s, blocks) in kept)
        {
            s.Blocks = blocks.Select(b => b.Clone()).ToList();
            foreach (var b in blocks)
            {
                if (b.Group == TaskGroups.Break || b.Group == TaskGroups.Flex) continue;
                if (!byTime.TryGet(b.Time, out var d)) continue;
                d.Total = Math.Max(0, d.Total - 1);
                if (b.Group != TaskGroups.NonOp) d.Groups.Set(b.Group, Math.Max(0, d.Groups.GetOr(b.Group, 0) - 1));
            }
        }
        return (rest, byTime.Values().Where(d => d.Total > 0).OrderBy(d => d.Time).ToList());
    }

    // Keep the held tiles where they are legal and paint the rest; if that loses
    // work a fresh paint of the whole day would not, paint the whole day.
    private static PaintResult<FShift> PaintKeepingHeld(
        DayContext day,
        List<FShift> shifts,
        JsMap<string, FortnightEmp> empById,
        IReadOnlyList<string> priority,
        IReadOnlyDictionary<string, BreakRule> breakRules)
    {
        var snapshot = shifts.Select(s => (s.Start, s.End, s.Blocks)).ToList();
        var kept = KeepHeldTiles(day, shifts, empById, breakRules);
        if (kept is null) return PaintDayShifts(day, shifts, empById, priority);
        var keptResult = kept.Value.Others.Count > 0
            ? PaintDayShifts(day, kept.Value.Others, empById, priority, kept.Value.Demand, true)
            : new PaintResult<FShift>();
        // An empty till is a hard stop, so a lost till quarter-hour outweighs any other work.
        if (LostWeight(keptResult.Lost) == 0) return keptResult;
        var keptState = shifts.Select(s => (s.Start, s.End, s.Blocks)).ToList();
        for (var i = 0; i < shifts.Count; i++) (shifts[i].Start, shifts[i].End, shifts[i].Blocks) = snapshot[i];
        var fresh = PaintDayShifts(day, shifts, empById, priority);
        if (LostWeight(fresh.Lost) < LostWeight(keptResult.Lost)) return fresh;
        for (var i = 0; i < shifts.Count; i++) (shifts[i].Start, shifts[i].End, shifts[i].Blocks) = keptState[i];
        return keptResult;
    }

    // Work on nobody's shift, an empty till counting above everything.
    private static double LostWeight(IEnumerable<LostWork> lost)
    {
        double n = 0;
        foreach (var l in lost) n += l.Blocks * (l.Group == TaskGroups.Checkout ? 100 : 1);
        return n;
    }

    // ─── Breaks that cost work on the roster (master plan decision 4.2) ─────────
    // The roster fills the approved plan's shifts with real people, and the person
    // in a shift can be entitled to a different break from the one the plan
    // priced: a manager's flat 30 minutes in a store assistant's 3-hour shift
    // that had 15. On a day the plan holds exactly its work, that quarter-hour has
    // nowhere to go and the work ends up on nobody's shift. Two remedies, in
    // order, each kept only when it saves work without adding a cover problem:
    //   1. a plain fault first - swap who holds two of the day's shifts, when both
    //      may legally work the other's (the manager takes the longer shift, where
    //      the break they are owed anyway costs nothing);
    //   2. then the owner's decision 4.2 - keep someone on a quarter- or
    //      half-hour longer, or start them that much earlier, within the day's
    //      allowance (the "Time added so breaks don't cost work" setting, less
    //      what the plan already added that day).
    // Every legal rule is kept: availability, the person's longest paid shift,
    // their role's weekly cap, the 11-hour rest gap both sides (for everyone, as
    // the repair keeps it), the staffed day; fixed shifts and juniors' shifts are
    // never touched. Not a re-painting solver: it only changes who holds a shift
    // or how long it is, and lets the ordinary painter use it.
    private static readonly double[] RescueSteps = [BLOCK_MIN, 2 * BLOCK_MIN];

    // The roster is built from an approved plan, and someone holds a shift whose
    // break they are owed differs from the one the plan priced for it (a manager
    // in a store assistant's shift, a store assistant stepping up to manage).
    // Only then is work the roster loses down to the people in the shifts, not
    // the plan's own shape.
    private static bool BreaksDiffer(DayContext day, List<FShift> shifts, IReadOnlyDictionary<string, BreakRule> breakRules)
    {
        if (day.HeldShifts is not { Count: > 0 }) return false;
        return shifts.Any(s =>
            !s.Unfilled &&
            TotalBreakMinutes(breakRules[s.Role], s.Start, s.End) !=
                TotalBreakMinutes(breakRules[BreakGaps.PlanBreakRole[s.SlotRole ?? SlotRoles.SA]], s.Start, s.End));
    }

    private const int MaxRescueSwaps = 2;

    /// <summary>Who holds a shift, and its times before painting.</summary>
    private sealed record Spec(string EmployeeId, double Start, double End);

    private sealed record Eval(List<Spec> Spec, List<FShift> Clones, PaintResult<FShift> Result, double Lost, double Cover, double Blank, double Min);

    private static (PaintResult<FShift> Result, List<string> Notes)? RescueLostWork(
        FortnightInput input,
        int di,
        DayRoster roster,
        List<(double Start, double End)> @base,
        JsMap<string, FortnightEmp> empById,
        IReadOnlyList<string> priority,
        JsMap<string, Ledger> ledgers,
        Func<string, int, FShift?> shiftOn,
        PaintResult<FShift> first)
    {
        if (R.BreakTimeAddMaxMin <= 0) return null;
        var day = input.Days[di];
        var breakRules = input.BreakRules;
        var baseSpec = roster.Shifts.Select((s, i) => new Spec(s.EmployeeId, @base[i].Start, @base[i].End)).ToList();
        double PaidOf(FortnightEmp e, double start, double end) => PaidHoursOf(e.Role, breakRules[e.Role], start, end);
        // Their own shift today as the ledger holds it (the base times).
        var baseOf = new JsMap<string, Spec>(baseSpec.Select(s => (s.EmployeeId, s)));

        // May `e` work [start, end) today, in place of their own shift today?
        bool MayHold(FortnightEmp e, double start, double end, string? slotRole)
        {
            if (e.Role == Roles.JSA) return false;
            if (!string.IsNullOrEmpty(slotRole) && !SlotCompatible(e, slotRole)) return false;
            if (start < day.StaffOpen || end > day.StaffClose) return false;
            var a = day.Avail.Get(e.Id);
            if (a is null || !a.Available || a.Fixed is not null) return false;
            if (a.FromMin is { } from && from > start) return false;
            if (a.ToMin is { } to && to < end) return false;
            var rule = breakRules[e.Role];
            if (PaidOf(e, start, end) > rule.MaxShiftHours + 1e-9) return false;
            var own = baseOf.Get(e.Id);
            var led = ledgers.Get(e.Id);
            if (led is null || own is null) return false;
            var week = led.PaidHoursByWeek[day.WeekIndex] - PaidOf(e, own.Start, own.End) + PaidOf(e, start, end);
            if (OverWeeklyCap(rule, week)) return false;
            var prev = shiftOn(e.Id, di - 1);
            if (prev is not null && start < prev.End - DAY_MIN + R.RestGapMin) return false;
            var next = shiftOn(e.Id, di + 1);
            if (next is not null && next.Start < end - DAY_MIN + R.RestGapMin) return false;
            return true;
        }

        // Minutes with a cover problem (someone alone, no manager, a junior short
        // of adults): a change may not add any.
        double CoverMin(List<FShift> shifts)
        {
            double n = 0;
            foreach (var c in Coverage.CoverIssues(shifts.Where(s => !s.Unfilled), R)) n = n + c.End - c.Start;
            return n;
        }
        // A swap moves hours from one person to the other: it may not leave the
        // one who gives them up far short of contract (more than 4 hours, or a
        // tenth of it) when they were not already.
        bool LeavesFarShort(FortnightEmp e, Spec from, Spec to)
        {
            var delta = PaidOf(e, to.Start, to.End) - PaidOf(e, from.Start, from.End);
            if (delta >= 0) return false;
            var led = ledgers.Get(e.Id);
            if (led is null) return true;
            var credit = LeaveHoursFor(e, input.Days) + PhCreditFor(e, input.Days, d => led.DaysWorked.Has(d));
            var @short = e.ContractHours - (led.PaidHoursByWeek[0] + led.PaidHoursByWeek[1]) - credit;
            var far = Math.Max(4, e.ContractHours / 10);
            return @short <= far + 1e-9 && @short - delta > far + 1e-9;
        }
        static double SumMinutes(List<Spec> spec)
        {
            double n = 0;
            foreach (var s in spec) n = n + s.End - s.Start;
            return n;
        }
        static double BlankCount(PaintResult<FShift> r)
        {
            double n = 0;
            foreach (var b in r.Blanks) n += b.Times.Count;
            return n;
        }
        Eval Evaluate(List<Spec> spec)
        {
            var clones = roster.Shifts.Select((s, i) =>
            {
                var e = empById.Get(spec[i].EmployeeId);
                var c = s.Copy();
                c.EmployeeId = spec[i].EmployeeId;
                c.Name = e?.Name ?? s.Name;
                c.Role = e?.Role ?? s.Role;
                c.Start = spec[i].Start;
                c.End = spec[i].End;
                c.BreakMinutes = e is not null ? TotalBreakMinutes(breakRules[e.Role], spec[i].Start, spec[i].End) : s.BreakMinutes;
                c.Blocks = new List<ShiftBlock>();
                return c;
            }).ToList();
            var result = PaintKeepingHeld(day, clones, empById, priority, breakRules);
            return new Eval(
                spec,
                clones,
                result,
                LostWeight(result.Lost),
                CoverMin(clones),
                BlankCount(result),
                SumMinutes(spec) - SumMinutes(baseSpec));
        }
        static double Better(Eval a, Eval b) =>
            (a.Lost - b.Lost).Or(a.Cover - b.Cover).Or(a.Blank - b.Blank).Or(a.Min - b.Min);
        static bool Helps(Eval r, Eval cur) => r.Lost < cur.Lost && r.Cover <= cur.Cover;

        var cur = new Eval(
            baseSpec,
            roster.Shifts,
            first,
            LostWeight(first.Lost),
            CoverMin(roster.Shifts),
            BlankCount(first),
            0);
        bool Movable(int i) => !roster.Shifts[i].Locked && !roster.Shifts[i].Unfilled;

        // 1. Swap who holds two shifts. A swap that saves no work by itself but
        // moves the loss to where a little time can save it is kept aside for 2.
        var neutral = new List<(Eval R, (int, int) Pair)>();
        for (var round = 0; round < MaxRescueSwaps && cur.Lost > 0; round++)
        {
            Eval? best = null;
            (int, int)? pair = null;
            neutral = new List<(Eval R, (int, int) Pair)>();
            for (var i = 0; i < cur.Spec.Count; i++)
            {
                if (!Movable(i)) continue;
                var a = empById.Get(cur.Spec[i].EmployeeId);
                if (a is null) continue;
                for (var j = i + 1; j < cur.Spec.Count; j++)
                {
                    if (!Movable(j)) continue;
                    var b = empById.Get(cur.Spec[j].EmployeeId);
                    // Only a swap that changes someone's break can save work.
                    if (b is null || a.Role == b.Role) continue;
                    var si = cur.Spec[i];
                    var sj = cur.Spec[j];
                    if (si.Start == sj.Start && si.End == sj.End) continue;
                    if (!MayHold(a, sj.Start, sj.End, roster.Shifts[j].SlotRole)) continue;
                    if (!MayHold(b, si.Start, si.End, roster.Shifts[i].SlotRole)) continue;
                    if (LeavesFarShort(a, si, sj) || LeavesFarShort(b, sj, si)) continue;
                    var (ii, jj) = (i, j);
                    var spec = cur.Spec.Select((s, k) =>
                        k == ii ? s with { EmployeeId = b.Id } : k == jj ? s with { EmployeeId = a.Id } : s).ToList();
                    var r = Evaluate(spec);
                    if (r.Lost == cur.Lost && r.Cover <= cur.Cover) neutral.Add((r, (i, j)));
                    if (!Helps(r, cur)) continue;
                    if (best is null || Better(r, best) < 0)
                    {
                        best = r;
                        pair = (i, j);
                    }
                }
            }
            if (best is null || pair is null) break;
            cur = best;
            neutral = new List<(Eval R, (int, int) Pair)>();
        }

        // 2. Add time, within what is left of the day's allowance - from the day
        // as it stands, and from the few best swaps that moved the loss.
        Eval AddTime(Eval from)
        {
            var at = from;
            var left = R.BreakTimeAddMaxMin - (day.BreakTimeAddedMin ?? 0);
            while (at.Lost > 0 && left > 0)
            {
                Eval? best = null;
                for (var i = 0; i < at.Spec.Count; i++)
                {
                    if (!Movable(i)) continue;
                    var s = at.Spec[i];
                    var e = empById.Get(s.EmployeeId);
                    if (e is null) continue;
                    foreach (var d in RescueSteps)
                    {
                        if (d > left) continue;
                        foreach (var (start, end) in new[] { (s.Start - d, s.End), (s.Start, s.End + d) })
                        {
                            if (!MayHold(e, start, end, roster.Shifts[i].SlotRole)) continue;
                            var ii = i;
                            var r = Evaluate(at.Spec.Select((x, k) => k == ii ? x with { Start = start, End = end } : x).ToList());
                            if (!Helps(r, at)) continue;
                            if (best is null || Better(r, best) < 0) best = r;
                        }
                    }
                }
                if (best is null) break;
                left -= best.Min - at.Min;
                at = best;
            }
            return at;
        }
        if (cur.Lost > 0)
        {
            var plain = AddTime(cur);
            var pick = (R: plain, Pair: ((int, int)?)null);
            static double ByTime(Eval a, Eval b) =>
                (a.Lost - b.Lost).Or(a.Cover - b.Cover).Or(a.Min - b.Min).Or(a.Blank - b.Blank);
            neutral = Js.Sort(neutral, (x, y) => x.R.Blank - y.R.Blank);
            foreach (var n in neutral.Take(6))
            {
                if (pick.R.Lost == 0 && pick.R.Min <= BLOCK_MIN) break;
                var r = AddTime(n.R);
                if (r.Lost < cur.Lost && ByTime(r, pick.R) < 0) pick = (r, n.Pair);
            }
            if (pick.R.Lost < cur.Lost)
            {
                cur = pick.R;
            }
        }
        if (ReferenceEquals(cur.Clones, roster.Shifts)) return null;

        // Keep the result: the ledger follows each change of holder (at the base
        // times; the painting loop then prices any change of times, as it does for
        // a shift the painter trims).
        var notes = new List<string>();
        var moved = new List<RosterSwapMove>();
        for (var i = 0; i < baseSpec.Count; i++)
        {
            if (baseSpec[i].EmployeeId == cur.Spec[i].EmployeeId) continue;
            moved.Add(new RosterSwapMove(empById.Get(cur.Spec[i].EmployeeId)?.Name ?? "", @base[i].Start, @base[i].End));
        }
        if (moved.Count > 0) notes.Add(BreakTime.RosterSwapNote(moved));
        for (var i = 0; i < roster.Shifts.Count; i++)
        {
            var was = baseSpec[i];
            var now = cur.Spec[i];
            if (was.EmployeeId != now.EmployeeId)
            {
                var out_ = empById[was.EmployeeId];
                var inn = empById[now.EmployeeId];
                var w = day.WeekIndex;
                ledgers[out_.Id].PaidHoursByWeek[w] -= PaidOf(out_, was.Start, was.End);
                ledgers[inn.Id].PaidHoursByWeek[w] += PaidOf(inn, was.Start, was.End);
            }
        }
        var grown = new List<RosterBreakTimeChange>();
        for (var i = 0; i < roster.Shifts.Count; i++)
        {
            var s = roster.Shifts[i];
            var c = cur.Clones[i];
            if (c.End - c.Start > @base[i].End - @base[i].Start)
                grown.Add(new RosterBreakTimeChange(c.Name, new ShiftTimes(@base[i].Start, @base[i].End), new ShiftTimes(c.Start, c.End)));
            s.EmployeeId = c.EmployeeId;
            s.Name = c.Name;
            s.Role = c.Role;
            s.Start = c.Start;
            s.End = c.End;
            s.BreakMinutes = c.BreakMinutes;
            s.Blocks = c.Blocks;
            ledgers.Get(c.EmployeeId)?.ShiftEndByDay.Set(di, c.End);
        }
        var note = BreakTime.RosterBreakTimeNote(grown);
        if (note is not null) notes.Add(note);
        var clonesNow = cur.Clones;
        return (
            cur.Result with
            {
                Blanks = cur.Result.Blanks
                    .Select(b =>
                    {
                        var idx = clonesNow.IndexOf(b.Shift);
                        return b with { Shift = idx >= 0 ? roster.Shifts[idx] : b.Shift };
                    })
                    .ToList(),
            },
            notes);
    }

    // Paint (or re-paint) a day's task tiles. Blocks are rebuilt from scratch so
    // the repair pass can call this again after it moves shifts between people.
    private static PaintResult<FShift> PaintDayShifts(
        DayContext day,
        List<FShift> shifts,
        JsMap<string, FortnightEmp> empById,
        IReadOnlyList<string> priority,
        IReadOnlyList<DemandBlock>? demand = null,
        bool keepTimes = false)
    {
        demand ??= day.DemandByBlock;
        foreach (var s in shifts) s.Blocks = new List<ShiftBlock>();
        var paintable = shifts.Select(s =>
        {
            var p = s.Copy();
            p.Restricted = empById.Get(s.EmployeeId)?.Restricted ?? [];
            // Fixed rosters and manual pins arrive locked: their times are kept. So
            // are the approved plan's when only some shifts are painted: the painter
            // cannot see the kept shifts, so it may not judge who the floor needs.
            p.Fixed = s.Locked || keepTimes;
            return p;
        }).ToList();
        var allowed = day.AllowedGroups;
        var r = TaskPainter.PaintTasks(
            paintable,
            demand,
            priority,
            allowed is not null ? t => allowed.Get(t) ?? [] : null,
            new PaintOptions
            {
                // Breaks done by 18:00 unless the store trades to 21:00 (operating rule).
                BreakEndBy = day.TradingClose >= 21 * 60 ? null : 18 * 60,
                TaskWindows = day.TaskWindows,
                UnitWindows = day.UnitWindows,
                AdminDeadline = day.AdminDeadline,
                Cover = new PaintCover(R.MinPeopleOnFloor, R.MinAdultsWithJsa),
                Handover = new PaintHandover(R.OverlapMinMin, R.ManagerMinShiftMin),
            });
        // Times AND tiles. Copying back only the tiles left every quarter-hour the
        // painter handed back on the roster as a blank tile - shown, approved,
        // exported and paid (Roster Engine Review 07/11/2026, A07-1).
        for (var i = 0; i < paintable.Count; i++)
        {
            shifts[i].Blocks = paintable[i].Blocks;
            shifts[i].Start = paintable[i].Start;
            shifts[i].End = paintable[i].End;
        }
        return new PaintResult<FShift>
        {
            Blanks = r.Blanks.Select(b => new ShiftBlanks<FShift>(shifts[paintable.IndexOf(b.Shift)], b.Times)).ToList(),
            Lost = r.Lost,
            SearchCapped = r.SearchCapped,
        };
    }
}
