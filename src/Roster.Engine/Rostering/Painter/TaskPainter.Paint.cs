// Port of src/lib/stage2/engine.ts: paintTasks, the shared task painter.
//
// The TypeScript is one function whose passes share state through closures;
// here the passes are the methods of one PaintDay, in the same order, with the
// same state. Times are minutes from midnight on the 15-minute grid: the
// TypeScript keeps the outstanding demand in objects keyed by minute, which
// iterate in ascending numeric order, so this keeps it in a SortedDictionary.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Painter;

public static partial class TaskPainter
{
    private const double BreakEdgeMin = 60; // a break never sits within the first/last hour (award)
    private const double MealLatestStartMin = 5 * 60; // a meal break starts by the 5th hour (award)
    private const double BreakGapMin = 60; // two break segments never run back-to-back
    // The no-blank pass's work for one day, in hand-over attempts. An ordinary day
    // needs a few hundred at most (the test corpus, 1,959 paints: median 7, worst
    // 8,081). Past EXACT_PASS_WORK the search stops re-trying shifts a hand-over has
    // already dead-ended on in the same search, which bounds it, and the day is
    // flagged; past BLANK_PASS_BUDGET it stops. Without these a few early-closing
    // weekend days never finished, and left their whole fortnight with no plan.
    private const int ExactPassWork = 20_000;
    private const int BlankPassBudget = 200_000;

    private static PaintResult<S> Paint<S>(
        IReadOnlyList<S> shifts,
        IReadOnlyList<DemandBlock> demandByBlock,
        IReadOnlyList<string> priority,
        Func<double, IReadOnlyList<string>?>? allowedAt,
        PaintOptions? opts) where S : PaintableShift
    {
        var day = new PaintDay(shifts.Cast<PaintableShift>().ToList(), demandByBlock, priority, allowedAt, opts);
        day.Run();
        return new PaintResult<S>
        {
            Lost = day.Lost,
            Blanks = day.Blanks.Select(b => new ShiftBlanks<S>((S)b.Shift, b.Times)).ToList(),
            SearchCapped = day.SearchCapped,
        };
    }

    // JavaScript arr[i]: the element, or undefined unless i is a whole index inside the array.
    private static ShiftBlock? At(List<ShiftBlock> blocks, double i) =>
        i >= 0 && i < blocks.Count && i == Math.Floor(i) ? blocks[(int)i] : null;

    private static bool ContainsRef<T>(List<T> list, T item) where T : class
    {
        foreach (var x in list)
            if (ReferenceEquals(x, item)) return true;
        return false;
    }

    private readonly record struct TrialStep(ShiftBlock B, string Was);

    private readonly record struct Chain(ShiftBlock Take, ShiftBlock? MoveTo);

    private sealed record BreakPlanOf(double Len, int Segments, double SegMin, double Lo, double Hi, double Mid, double FirstHi);

    private sealed record FlexRef(PaintableShift Shift, int Idx, double Time);

    private sealed class BreakSnapshot
    {
        public required List<List<string>> Groups { get; init; }
        public required Dictionary<double, double> OnBreak { get; init; }
        public required Dictionary<PaintableShift, HashSet<double>> BreakBlocks { get; init; }
        public required Dictionary<double, Dictionary<string, double>> Remaining { get; init; }
        public required Dictionary<double, double> RemainingU { get; init; }
        public required int TillsLost { get; init; }
        public required int WorkLost { get; init; }
        public required List<PaintableShift> Stuck { get; init; }
    }

    private sealed record ShiftState(PaintableShift Shift, double Start, double End, List<ShiftBlock> Blocks);

    // A snapshot holds every shift, and the demand still waiting to be painted
    // (which takeOwed draws on), so a restore puts back both.
    private sealed record DaySnapshot(
        List<ShiftState> Shifts,
        Dictionary<double, Dictionary<string, double>> Owed,
        Dictionary<double, double> OwedU);

    private readonly record struct Cover(int People, int Managers, int Adults, int Juniors);

    private sealed class PaintDay
    {
        // The price table of the break placement (see the breaks pass).
        private const double LostPrice = 50;
        private const double StaggerInLoad = 20;
        private const double MinShiftMin = 180;
        private const int PassDepth = 6;

        private static readonly IEqualityComparer<object?> ByRef = ReferenceEqualityComparer.Instance;

        private readonly List<PaintableShift> _shifts;
        private readonly IReadOnlyList<DemandBlock> _demandByBlock;
        private readonly IReadOnlyList<string> _priority;
        private readonly Func<double, IReadOnlyList<string>?>? _allowedAt;
        private readonly PaintOptions? _opts;

        // Demand still waiting for a tile, by minute: directed groups, and the
        // undirected (Non-Op) remainder.
        private readonly SortedDictionary<double, Dictionary<string, double>> _remaining = new();
        private readonly SortedDictionary<double, double> _remainingU = new();
        private readonly Dictionary<double, double> _demandTotal = new();

        private readonly List<PaintableShift> _real;
        private readonly List<PaintableShift> _nonMgrFirst;
        private readonly Dictionary<double, double> _onBreak = new();
        private readonly Dictionary<PaintableShift, HashSet<double>> _breakBlocks = new(ByRef);
        private readonly Dictionary<PaintableShift, HashSet<double>> _checkoutClaims = new(ByRef);
        private readonly Dictionary<double, double> _loadDemandAt = new();
        private readonly HashSet<ShiftBlock> _noSkip = new(ByRef);
        private readonly Func<string, double, double, bool> _sameTaskWindow;

        // What the breaks committed so far cost: quarter-hours of work lost, how
        // many of them tills, and the shifts whose break left a till empty.
        private int _tillsLost;
        private int _workLost;
        private readonly List<PaintableShift> _tillStuckShifts = new();
        private List<(PaintableShift S, BreakPlanOf Plan)> _allBreaks = new();

        private List<FlexRef> _flexRefs = new();
        private readonly Dictionary<string, double> _groupDemand = new();
        private readonly Dictionary<string, double> _groupPainted = new();

        // The no-blank pass.
        private double _handoverMin;
        private double _mgrMinShiftMin;
        private double _minPeople;
        private double _minAdults;
        private readonly HashSet<PaintableShift> _failed = new(ByRef);
        private int _passWork;

        public List<LostWork> Lost { get; private set; } = new();
        public List<(PaintableShift Shift, List<double> Times)> Blanks { get; private set; } = new();
        public bool SearchCapped { get; private set; }

        public PaintDay(
            List<PaintableShift> shifts,
            IReadOnlyList<DemandBlock> demandByBlock,
            IReadOnlyList<string> priority,
            Func<double, IReadOnlyList<string>?>? allowedAt,
            PaintOptions? opts)
        {
            _shifts = shifts;
            _demandByBlock = demandByBlock;
            _priority = priority;
            _allowedAt = allowedAt;
            _opts = opts;
            foreach (var d in demandByBlock)
            {
                _remaining[d.Time] = CopyGroups(d.Groups);
                var gsum = 0.0;
                foreach (var n in d.Groups.Values()) gsum += n;
                _remainingU[d.Time] = Math.Max(0, d.Total - gsum);
                _demandTotal[d.Time] = d.Total;
            }
            _real = shifts.Where(s => !s.Unfilled).ToList();
            // (The TypeScript also counts heads present, tills and "urgent" blocks
            // here; nothing reads them.)
            _nonMgrFirst = Js.Sorted(_real, (a, b) => (Roles.IsManager(a.Role) ? 1 : 0) - (Roles.IsManager(b.Role) ? 1 : 0));
            foreach (var d in demandByBlock) _loadDemandAt[d.Time] = d.GroupCount(TaskGroups.LoadRunning);
            _sameTaskWindow = TaskWindowRule(opts);
        }

        private static Dictionary<string, double> CopyGroups(JsMap<string, double> groups)
        {
            var copy = new Dictionary<string, double>();
            foreach (var (g, n) in groups) copy[g] = n;
            return copy;
        }

        private static Dictionary<double, Dictionary<string, double>> CopyRemaining(SortedDictionary<double, Dictionary<string, double>> rem)
        {
            var copy = new Dictionary<double, Dictionary<string, double>>();
            foreach (var (t, g) in rem) copy[t] = new Dictionary<string, double>(g);
            return copy;
        }

        private double Rem(double m, string g) =>
            _remaining.TryGetValue(m, out var rem) && rem.TryGetValue(g, out var n) ? n : 0;

        private double RemU(double m) => _remainingU.TryGetValue(m, out var n) ? n : 0;

        private bool HasBreakAt(PaintableShift s, double m) => _breakBlocks.TryGetValue(s, out var set) && set.Contains(m);

        private static bool IsRestricted(PaintableShift s, string g) => s.Restricted != null && s.Restricted.Contains(g);

        private static ShiftBlock? BlockOf(PaintableShift s, double m) =>
            m >= s.Start && m < s.End ? At(s.Blocks, (m - s.Start) / Grid.BlockMin) : null;

        public void Run()
        {
            CheckoutPass();
            MainPass();
            PlaceBreaks();
            MakeUp();
            ContinuityPass();
            GapPass();
            MorningAdmin();
            NoBlankPass();
            Finish();
        }

        // ── Checkout pass (BLOCK-major, before anything else): every till must be
        //    manned in its exact block, so checkout units claim bodies first -
        //    Store Assistants ahead of managers (register etiquette). Only then
        //    does the remaining work paint per shift, and only then the breaks.
        private void CheckoutPass()
        {
            foreach (var d in _demandByBlock)
            {
                var units = d.GroupCount(TaskGroups.Checkout);
                if (units <= 0) continue;
                foreach (var s in _nonMgrFirst)
                {
                    if (units <= 0) break;
                    if (s.Start > d.Time || s.End <= d.Time) continue;
                    if (HasBreakAt(s, d.Time)) continue;
                    if (!TaskGroups.EligibleFor(s.Role, TaskGroups.Checkout) || IsRestricted(s, TaskGroups.Checkout)) continue;
                    if (!_checkoutClaims.TryGetValue(s, out var set))
                    {
                        set = new HashSet<double>();
                        _checkoutClaims[s] = set;
                    }
                    if (set.Contains(d.Time)) continue;
                    set.Add(d.Time);
                    units--;
                }
                if (_remaining.TryGetValue(d.Time, out var rem)) rem[TaskGroups.Checkout] = units; // unfillable remainder
            }
        }

        // ── Main pass: paint the rest of the demand (no breaks yet) ──
        private void MainPass()
        {
            foreach (var shift in _nonMgrFirst)
            {
                _breakBlocks.TryGetValue(shift, out var holes);
                _checkoutClaims.TryGetValue(shift, out var claims);
                var order = CandidateOrder(shift.Role, _priority);
                shift.Blocks = new List<ShiftBlock>();
                for (var m = shift.Start; m < shift.End; m += Grid.BlockMin)
                {
                    if (holes != null && holes.Contains(m))
                    {
                        shift.Blocks.Add(new ShiftBlock(m, TaskGroups.Break));
                        continue;
                    }
                    if (claims != null && claims.Contains(m))
                    {
                        shift.Blocks.Add(new ShiftBlock(m, TaskGroups.Checkout));
                        continue;
                    }
                    var assigned = TaskGroups.Flex;
                    if (_remaining.TryGetValue(m, out var rem))
                        foreach (var g in order)
                        {
                            if (rem.GetValueOrDefault(g, 0) > 0 && !IsRestricted(shift, g))
                            {
                                rem[g] = rem.GetValueOrDefault(g, 0) - 1;
                                assigned = g;
                                break;
                            }
                        }
                    if (assigned == TaskGroups.Flex && RemU(m) > 0)
                    {
                        _remainingU[m] = RemU(m) - 1;
                        assigned = TaskGroups.NonOp;
                    }
                    shift.Blocks.Add(new ShiftBlock(m, assigned));
                }
            }
        }

        // ── Make-up pass: work displaced by breaks (or left over from capacity
        //    gaps) moves to the nearest legal spare block instead of disappearing.
        //    Checkout never relocates - a till is manned in its block or not at all.
        private bool LegalAt(PaintableShift s, string g, double time)
        {
            if (g == TaskGroups.NonOp) return true;
            if (!TaskGroups.EligibleFor(s.Role, g) || IsRestricted(s, g)) return false;
            var allowed = _allowedAt?.Invoke(time);
            return allowed == null || allowed.Contains(g);
        }
        // May work demanded at `from` be made up at `to`? For a group whose day is
        // one undifferentiated block of hours (Load Running, Cleaning, Specials) the
        // group's own window is the whole answer, and legalAt has already checked
        // it. Admin and Reset are NOT that: their hours are named jobs with their
        // own deadlines - Re-run 1 by 14:00, the full decard in the last two and a
        // half hours - and the group window is merely the union of all of them.
        // Reset's union is 10:00-20:00, so nothing stopped a re-run that had no body
        // free at 11:00 from being painted at 19:00, where it read as hours of
        // decard the store never planned. Work may therefore only move to a block
        // that shares one of the group's task windows with the block that wanted
        // it: inside the same job, never into a different one.
        //
        // The owner settled how strict (decision 11, 13/11/2026): strict for Admin -
        // a quarter-hour of a job is made up inside THAT job's window, not any due
        // task's that happens to contain both minutes (review RC-9: date checks owed
        // at 07:45 were "made up" at 12:15) - and loose for Reset, which may be made
        // up anywhere in the Reset window (legalAt checks that). Cleaning's quoted
        // morning hour is a window of its own (decisions 10 and 12).
        //
        // "Its own task's window" is judged over the jobs Stage 1 PLANNED: both
        // minutes inside one planned job's window. The day's Admin tiles are then
        // matched to its jobs earliest deadline first (as the day checker does), so
        // a quarter-hour made up inside another planned job's window frees one of
        // that job's tiles for the job that lost it. Tying each quarter-hour to the
        // job it was first placed for is stricter than that matching, and on the
        // sample lost Admin on 18 more days; the windows of due tasks Stage 1 did
        // not plan that day are what made the old test lax.
        //
        // Admin hours past the tasks (Stage 1's generic time) belong to no task and
        // may be done anywhere in the staffed day, as the day checker counts them.
        // Their whole-day window is not a task window: used as one, it let any Admin
        // move anywhere, and the afternoon's Fresh Ordering went to the morning,
        // where it counts for nothing (G001 14/11: 0.75h lost, the managers on
        // Admin right through).  (_sameTaskWindow, built in the constructor.)

        // ── Breaks, around the registers and the tasks just painted ─────────────
        // The owner's sequence (U6; owner decision 4): place the tasks, put the
        // registers around them, THEN "apply the break tiles and shift the checkout
        // tiles to accommodate". Breaks used to be chosen first, on head counts
        // alone: a minute looked safe for a break because somebody there was spare
        // - an idle assistant - when the work the break displaced was the only
        // manager's date checks, which no assistant can do; and a till could come up
        // short where an exact answer existed (Roster Engine Review 07/11/2026,
        // P10, A02-8).
        //
        // Now every position a break could legally take is priced by what really
        // happens to the work on it, quarter-hour by quarter-hour:
        //    -1  the person had nothing to do then - the best place for a break;
        //     0  a colleague who is free then, and allowed to, takes the job over;
        //     1  a till: a colleague takes it and their own job moves to a free
        //        quarter-hour in its own window;
        //   5/6  other work: done in a free quarter-hour of its own window, or by a
        //        colleague whose job moves there;
        //     7  a manager's Admin, done in a free quarter-hour of its own window.
        //        Admin is the last thing a manager's break sits on: a manager's
        //        spare minutes in an Admin window are its only slack - the closing
        //        manager has 14-29 minutes of it in the last four hours - and a
        //        break that spends them leaves a till call nowhere to push the Admin
        //        but out. So where the work at another legal position can be
        //        handed on, the break goes there: the closing manager's meal break
        //        lands before the evening Admin wherever the break rules allow
        //        (owner decisions 7 and R1, 13/11/2026);
        //    50  work lost - nobody can do it anywhere in its window (a till
        //        kept by taking a colleague off a job that is then lost counts
        //        the same);
        //  1000  a till nobody can take;
        // plus 1.5 per colleague already on a break then (one off the floor at a
        // time), and a hair for distance from the shift's natural break point.

        // Someone free at `m`, allowed to do `g` there - assistants first
        // (register etiquette, and a manager's time is the scarce kind).
        private ShiftBlock? FreeTaker(PaintableShift p, string g, double m, HashSet<ShiftBlock> skip)
        {
            foreach (var r in _nonMgrFirst)
            {
                if (ReferenceEquals(r, p)) continue;
                var b = BlockOf(r, m);
                if (b != null && b.Group == TaskGroups.Flex && !skip.Contains(b) && LegalAt(r, g, m)) return b;
            }
            return null;
        }

        // The nearest free quarter-hour where `g`, displaced from `m`, may be done.
        private ShiftBlock? FreeBlockFor(string g, double m, HashSet<ShiftBlock> skip)
        {
            ShiftBlock? best = null;
            var dist = double.PositiveInfinity;
            foreach (var r in _real)
                foreach (var b in r.Blocks)
                {
                    if (b.Group != TaskGroups.Flex || skip.Contains(b) || !LegalAt(r, g, b.Time)) continue;
                    if (!_sameTaskWindow(g, m, b.Time)) continue;
                    var d = Math.Abs(b.Time - m);
                    if (d < dist)
                    {
                        dist = d;
                        best = b;
                    }
                }
            return best;
        }

        // A colleague on a job that can move, who may do `g`: they take it, and
        // their job goes to a free quarter-hour of its own window (or, failing one,
        // back on the pile). Admin is a manager's and stays put - except for a
        // till, which is manned in its own quarter-hour or not at all: the manager
        // takes the register and the admin waits.
        private Chain? ChainTaker(PaintableShift p, string g, double m, HashSet<ShiftBlock> skip, bool needMove)
        {
            // Assistants before managers, and a manager's admin only as the last
            // resort.
            foreach (var adminToo in new[] { false, true })
            {
                if (adminToo && g != TaskGroups.Checkout) break;
                foreach (var r in _nonMgrFirst)
                {
                    if (ReferenceEquals(r, p)) continue;
                    var b = BlockOf(r, m);
                    if (b == null || skip.Contains(b) || b.Group == TaskGroups.Flex || b.Group == TaskGroups.Break || b.Group == g) continue;
                    if (b.Group == TaskGroups.Checkout || (b.Group == TaskGroups.Admin) != adminToo) continue;
                    if (!LegalAt(r, g, m)) continue;
                    var moveTo = FreeBlockFor(b.Group, m, new HashSet<ShiftBlock>(skip, ByRef) { b });
                    if (moveTo != null || !needMove) return new Chain(b, moveTo);
                }
            }
            return null;
        }

        // Handing on one quarter-hour of `p`'s work at `m`, displaced by a break:
        // a free colleague takes it; a till goes to a colleague whose own job
        // moves; other work moves to a free quarter-hour of its own window, or to a
        // colleague whose job moves there. Every block it changes is written to
        // `log`, so a trial can be put back. Returns the price (the table above) and
        // the work it could not place, if any.
        private static void Assign(ShiftBlock b, string g, List<TrialStep> log)
        {
            log.Add(new TrialStep(b, b.Group));
            b.Group = g;
        }

        private static void Undo(List<TrialStep> log)
        {
            for (var i = log.Count - 1; i >= 0; i--) log[i].B.Group = log[i].Was;
            log.Clear();
        }

        private (double Price, string? Lost) HandOn(PaintableShift p, double m, string g, List<TrialStep> log)
        {
            if (g == TaskGroups.Flex) return (-1, null);
            if (g == TaskGroups.Break) return (1e6, null);
            var free = FreeTaker(p, g, m, _noSkip);
            if (free != null)
            {
                Assign(free, g, log);
                return (0, null);
            }
            // The colleague takes `g`; their own job moves, or is lost when it has
            // nowhere to go (returned).
            string? Chained(Chain c)
            {
                var h = c.Take.Group;
                Assign(c.Take, g, log);
                if (c.MoveTo == null) return h;
                Assign(c.MoveTo, h, log);
                return null;
            }
            if (g == TaskGroups.Checkout)
            {
                var chain = ChainTaker(p, g, m, _noSkip, true);
                if (chain != null)
                {
                    Chained(chain.Value);
                    return (1, null);
                }
                // The till is kept, but the colleague's own job is lost: priced as one.
                var lossy = ChainTaker(p, g, m, _noSkip, false);
                return lossy != null ? (50, Chained(lossy.Value)) : (1000, g);
            }
            var later = FreeBlockFor(g, m, _noSkip);
            if (later != null)
            {
                Assign(later, g, log);
                return (g == TaskGroups.Admin ? 7 : 5, null);
            }
            var chain2 = ChainTaker(p, g, m, _noSkip, true);
            if (chain2 != null)
            {
                Chained(chain2.Value);
                return (6, null);
            }
            return (50, g);
        }

        // A break segment at [c, c+segMin): the break goes in, and each
        // quarter-hour's work is handed on in turn, against the day as the ones
        // before it left it. Pricing and committing run these same steps, so the
        // price of a position is what committing it does. Priced one quarter-hour
        // at a time against the untouched day, a tight Admin window with one spare
        // quarter-hour read as room for a half-hour break - both halves "made up"
        // in that one spare - so the closing manager's meal break went inside Fresh
        // Ordering and a quarter-hour of it was lost, where a break before the
        // window lost nothing (owner decisions 7 and R1, 13/11/2026).
        private (double Price, List<(string G, double M)> Lost) BreakSegment(PaintableShift p, double c, double segMin, List<TrialStep> log)
        {
            var work = new List<(double M, string G)>();
            for (var m = c; m < c + segMin; m += Grid.BlockMin)
            {
                var b = BlockOf(p, m);
                work.Add((m, b?.Group ?? TaskGroups.Flex));
                if (b != null) Assign(b, TaskGroups.Break, log);
            }
            double price = 0;
            var lost = new List<(string G, double M)>();
            foreach (var (m, g) in work)
            {
                var r = HandOn(p, m, g, log);
                // One off the floor at a time: a tie-break through the day, but during
                // the load the crew breaks one at a time (owner decisions 4 and R16) -
                // dearer than moving work, cheaper than losing it.
                price += r.Price + (_loadDemandAt.GetValueOrDefault(m, 0) > 0 ? StaggerInLoad : 1.5) * _onBreak.GetValueOrDefault(m, 0);
                if (!string.IsNullOrEmpty(r.Lost)) lost.Add((r.Lost, m));
            }
            return (price, lost);
        }

        // Hand on everything a break at [start, start+segMin) displaces, then put
        // the break in.
        private void CommitBreak(PaintableShift p, double start, double segMin)
        {
            for (var m = start; m < start + segMin; m += Grid.BlockMin)
            {
                if (!_breakBlocks.TryGetValue(p, out var set))
                {
                    set = new HashSet<double>();
                    _breakBlocks[p] = set;
                }
                set.Add(m);
                _onBreak[m] = _onBreak.GetValueOrDefault(m, 0) + 1;
            }
            foreach (var (g, m) in BreakSegment(p, start, segMin, new List<TrialStep>()).Lost)
            {
                _workLost++;
                if (g == TaskGroups.Checkout)
                {
                    _tillsLost++;
                    if (!ContainsRef(_tillStuckShifts, p)) _tillStuckShifts.Add(p);
                }
                if (g == TaskGroups.NonOp) _remainingU[m] = RemU(m) + 1;
                else if (g != TaskGroups.Flex && g != TaskGroups.Break)
                {
                    if (!_remaining.TryGetValue(m, out var rem))
                    {
                        rem = new Dictionary<string, double>();
                        _remaining[m] = rem;
                    }
                    rem[g] = rem.GetValueOrDefault(g, 0) + 1;
                }
            }
        }

        // A trial of the segment, put back afterwards - unless `keep` is given,
        // which holds it in place (the pair search prices the second segment with
        // the first one in) for the caller to undo.
        private double ScoreAt(PaintableShift s, double c, double segMin, double anchor, List<TrialStep>? keep = null)
        {
            var log = keep ?? new List<TrialStep>();
            var (price, _) = BreakSegment(s, c, segMin, log);
            if (keep == null) Undo(log);
            return price + Math.Abs(c + segMin / 2 - anchor) / 10000;
        }

        private (double Start, double Score) ProbeSegment(PaintableShift s, double lo, double hi, double segMin, double anchor)
        {
            double bestStart = -1;
            var bestScore = double.PositiveInfinity;
            for (var c = lo; c + segMin <= hi; c += Grid.BlockMin)
            {
                var score = ScoreAt(s, c, segMin, anchor);
                if (score < bestScore - 1e-9)
                {
                    bestScore = score;
                    bestStart = c;
                }
            }
            return (bestStart, bestScore);
        }

        private double PlaceSegment(PaintableShift s, double lo, double hi, double segMin, double anchor)
        {
            // Both edges are HARD: no break in the first hour, and always at least an
            // hour of tasks after the last break - no trailing "break then clock off"
            // exception. When every legal position sits inside window-locked work
            // (the morning load), the least-damaging position is taken and the
            // displaced work is flagged by the caller.
            //
            // The end-of-day cut-off (breaks done by 18:00 on a normal trading day)
            // is tried FIRST: probe with the span capped at the cut-off, and only if
            // no legal position exists there fall back to the full span - the award
            // break always wins over the operating preference.
            var cap = _opts?.BreakEndBy;
            var best = ProbeSegment(s, lo, hi, segMin, anchor);
            if (cap != null && hi > cap.Value && cap.Value - segMin >= lo)
            {
                var capped = ProbeSegment(s, lo, cap.Value, segMin, Math.Min(anchor, cap.Value - segMin / 2));
                // Work wins (owner decision 16): the preference for breaks done by 18:00
                // yields when every earlier position loses work - a till left unmanned
                // (1000) or a job nobody can do (50) - and a later one does better. It
                // used to yield only for a till, so a break went before 18:00 on a
                // quarter-hour of Admin that a later break kept (review RC-35).
                if (capped.Start != -1 && (capped.Score < LostPrice || capped.Score <= best.Score + 1e-9))
                {
                    CommitBreak(s, capped.Start, segMin);
                    return capped.Start;
                }
            }
            if (best.Start == -1) return -1;
            CommitBreak(s, best.Start, segMin);
            return best.Start;
        }

        // Each shift's break, as the award times it: segment length, and the span
        // its first segment may sit in.
        private static BreakPlanOf? BreakPlan(PaintableShift s)
        {
            var len = s.End - s.Start;
            var brk = Breaks.BreakForRole(s.Role, len / 60);
            if (brk.TotalMinutes <= 0) return null;
            var segMin = Js.Round(brk.TotalMinutes / brk.Segments / Grid.BlockMin) * Grid.BlockMin;
            if (segMin <= 0 || segMin * brk.Segments >= len) return null;
            // Award timing, hard: no break within the first or last hour of the
            // shift. A shift too short to honour the full hour shrinks the edge, but
            // never below one block on each side.
            var edge = BreakEdgeMin;
            var needed = segMin * brk.Segments + (brk.Segments > 1 ? BreakGapMin : 0);
            while (edge > Grid.BlockMin && len - 2 * edge < needed) edge -= Grid.BlockMin;
            var lo = s.Start + edge;
            var hi = s.End - edge;
            if (hi - lo < segMin) return null; // physically no legal home (tiny shift)
            var mid = s.Start + Math.Floor(len / 2 / Grid.BlockMin) * Grid.BlockMin;
            // The single segment doubles as the meal: it must START by the 5th hour.
            // Of two, the first sits in the first half (and meets meal timing by
            // construction).
            var firstHi = brk.Segments == 1
                ? Math.Max(Math.Min(hi, s.Start + MealLatestStartMin + segMin), lo + segMin)
                : Math.Max(Math.Min(mid, hi - segMin), lo + segMin);
            return new BreakPlanOf(len, brk.Segments, segMin, lo, hi, mid, firstHi);
        }

        // Positions for a shift's first segment where every quarter-hour is
        // handed on for free - its own idle time, or a colleague free to take it.
        private int CleanOptions(PaintableShift s, BreakPlanOf plan)
        {
            var n = 0;
            var none = new HashSet<ShiftBlock>(ByRef);
            for (var c = plan.Lo; c + plan.SegMin <= plan.FirstHi; c += Grid.BlockMin)
            {
                var clean = true;
                for (var m = c; m < c + plan.SegMin && clean; m += Grid.BlockMin)
                {
                    var g = BlockOf(s, m)?.Group ?? TaskGroups.Flex;
                    if (g != TaskGroups.Flex && FreeTaker(s, g, m, none) == null) clean = false;
                }
                if (clean) n++;
            }
            return n;
        }

        // The MOST CONSTRAINED break goes first. A break has to sit somewhere its
        // work can be handed on, and the day has only so many such places - so the
        // order the breaks choose in decides who is left taking theirs out of
        // somebody's work. Fewest clean positions first; on a tie, the longer
        // break (it needs that many spare quarter-hours in a row), then the shorter
        // shift. "Shortest shift first" alone let a short shift's quarter-hour take
        // one of the only two back-to-back spares a half-hour break could use, and
        // a till went unmanned that an exact answer kept (A02-8).
        //
        // `first`: shifts that choose ahead of everyone, in this order (the retry
        // below).
        private void PlaceAllBreaks(List<PaintableShift> first)
        {
            var pendingBreaks = new List<(PaintableShift S, BreakPlanOf Plan)>(_allBreaks);
            while (pendingBreaks.Count > 0)
            {
                var pick = -1;
                foreach (var f in first)
                {
                    pick = pendingBreaks.FindIndex(x => ReferenceEquals(x.S, f));
                    if (pick != -1) break;
                }
                (double C, double Seg, double Len)? pickKey = null;
                if (pick == -1)
                {
                    for (var i = 0; i < pendingBreaks.Count; i++)
                    {
                        var x = pendingBreaks[i];
                        (double C, double Seg, double Len) key = (CleanOptions(x.S, x.Plan), -x.Plan.SegMin, x.Plan.Len);
                        if (pickKey == null || key.C < pickKey.Value.C ||
                            (key.C == pickKey.Value.C && (key.Seg < pickKey.Value.Seg || (key.Seg == pickKey.Value.Seg && key.Len < pickKey.Value.Len))))
                        {
                            pick = i;
                            pickKey = key;
                        }
                    }
                }
                var (s, plan) = pendingBreaks[pick];
                pendingBreaks.RemoveAt(pick);
                if (plan.Segments == 1)
                {
                    PlaceSegment(s, plan.Lo, plan.FirstHi, plan.SegMin, (s.Start + s.End) / 2);
                }
                else
                {
                    // One segment per half (award: a rest in each half of a long shift),
                    // anchored at the third-points, separated by at least the gap - and
                    // chosen as a PAIR. Where the first goes decides where the second may:
                    // a first break at 11:15 put the second after 12:45, where every
                    // position left a till empty, while a first break earlier kept 12:30
                    // open for it. The end-of-day cut-off is tried first for the second, as
                    // for a single break.
                    var seg = plan.SegMin;
                    (double C1, double C2, double Score)? Search(double? hiCap)
                    {
                        (double C1, double C2, double Score)? best = null;
                        for (var c1 = plan.Lo; c1 + seg <= plan.FirstHi; c1 += Grid.BlockMin)
                        {
                            var lo2 = Math.Min(Math.Max(plan.Mid, c1 + seg + BreakGapMin), plan.Hi - seg);
                            var hi2 = hiCap == null ? plan.Hi : Math.Min(plan.Hi, hiCap.Value);
                            if (hi2 - lo2 < seg) continue;
                            // The second segment is priced with the first one in.
                            var held = new List<TrialStep>();
                            var firstScore = ScoreAt(s, c1, seg, s.Start + plan.Len / 3, held);
                            var second = ProbeSegment(s, lo2, hi2, seg, s.Start + 2 * plan.Len / 3);
                            Undo(held);
                            if (second.Start == -1) continue;
                            var score = firstScore + second.Score;
                            if (best == null || score < best.Value.Score - 1e-9) best = (c1, second.Start, score);
                        }
                        return best;
                    }
                    var cap = _opts?.BreakEndBy;
                    var pair = cap != null && plan.Hi > cap.Value ? Search(cap) : null;
                    if (pair == null || pair.Value.Score >= LostPrice)
                    {
                        // Work wins over the 18:00 preference (decision 16), as above.
                        var full = Search(null);
                        if (full != null && (pair == null || full.Value.Score < pair.Value.Score - 1e-9)) pair = full;
                    }
                    if (pair != null)
                    {
                        CommitBreak(s, pair.Value.C1, seg);
                        CommitBreak(s, pair.Value.C2, seg);
                    }
                }
            }
        }

        // Breaks are chosen one shift at a time, and a choice is never revisited:
        // a manager's break could take the only quarter-hours an assistant could
        // hand her till over in, and the assistant - choosing later - had nowhere
        // left that kept the till manned, though an arrangement that manned every
        // till existed (review RC-22). So when a break leaves a till empty, the
        // breaks are placed again with that shift choosing first, and the better
        // day is kept: fewer empty tills, then less work lost.
        private BreakSnapshot SnapshotBreaks() => new()
        {
            Groups = _real.Select(x => x.Blocks.Select(b => b.Group).ToList()).ToList(),
            OnBreak = new Dictionary<double, double>(_onBreak),
            BreakBlocks = _breakBlocks.ToDictionary<KeyValuePair<PaintableShift, HashSet<double>>, PaintableShift, HashSet<double>>(kv => kv.Key, kv => new HashSet<double>(kv.Value), ByRef),
            Remaining = CopyRemaining(_remaining),
            RemainingU = new Dictionary<double, double>(_remainingU),
            TillsLost = _tillsLost,
            WorkLost = _workLost,
            Stuck = new List<PaintableShift>(_tillStuckShifts),
        };

        private void RestoreBreaks(BreakSnapshot x)
        {
            for (var i = 0; i < _real.Count; i++)
                for (var j = 0; j < _real[i].Blocks.Count; j++)
                    _real[i].Blocks[j].Group = x.Groups[i][j];
            _onBreak.Clear();
            foreach (var (k, v) in x.OnBreak) _onBreak[k] = v;
            _breakBlocks.Clear();
            foreach (var (k, v) in x.BreakBlocks) _breakBlocks[k] = new HashSet<double>(v);
            _remaining.Clear();
            foreach (var (k, v) in x.Remaining) _remaining[k] = new Dictionary<string, double>(v);
            _remainingU.Clear();
            foreach (var (k, v) in x.RemainingU) _remainingU[k] = v;
            _tillsLost = x.TillsLost;
            _workLost = x.WorkLost;
            _tillStuckShifts.Clear();
            _tillStuckShifts.AddRange(x.Stuck);
        }

        private void PlaceBreaks()
        {
            _allBreaks = _real
                .Select(s => (S: s, Plan: BreakPlan(s)))
                .Where(x => x.Plan != null)
                .Select(x => (x.S, x.Plan!))
                .ToList();
            var beforeBreaks = SnapshotBreaks();
            PlaceAllBreaks(new List<PaintableShift>());
            if (_tillsLost > 0)
            {
                var best = SnapshotBreaks();
                var bestIsCurrent = true;
                var chooseFirst = new List<PaintableShift>();
                for (var attempt = 0; attempt < 3 && best.TillsLost > 0; attempt++)
                {
                    var add = best.Stuck.Where(x => !ContainsRef(chooseFirst, x)).ToList();
                    if (add.Count == 0) break;
                    chooseFirst.AddRange(add);
                    RestoreBreaks(beforeBreaks);
                    PlaceAllBreaks(chooseFirst);
                    if (_tillsLost < best.TillsLost || (_tillsLost == best.TillsLost && _workLost < best.WorkLost))
                    {
                        best = SnapshotBreaks();
                        bestIsCurrent = true;
                    }
                    else
                    {
                        bestIsCurrent = false;
                        break;
                    }
                }
                if (!bestIsCurrent) RestoreBreaks(best);
            }
        }

        private bool TakeNearest(string g, double m)
        {
            var best = -1;
            var bestDist = double.PositiveInfinity;
            for (var i = 0; i < _flexRefs.Count; i++)
            {
                var r = _flexRefs[i];
                if (r.Shift.Blocks[r.Idx].Group != TaskGroups.Flex) continue;
                if (!LegalAt(r.Shift, g, r.Time)) continue;
                if (!_sameTaskWindow(g, m, r.Time)) continue;
                var dist = Math.Abs(r.Time - m);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
            if (best == -1) return false;
            _flexRefs[best].Shift.Blocks[_flexRefs[best].Idx].Group = g;
            return true;
        }

        // Cascade for window-locked work: when no spare block exists inside the
        // group's legal window (e.g. Load Running displaced by a morning break, and
        // its window shuts at 10:00), take over the nearest in-window block painted
        // with a MOVABLE group and relocate THAT unit to a spare block instead -
        // two moves, zero lost work.
        private bool Steal(string g, double m)
        {
            var cands = new List<FlexRef>();
            foreach (var s in _shifts)
            {
                if (s.Unfilled) continue;
                for (var idx = 0; idx < s.Blocks.Count; idx++)
                {
                    var b = s.Blocks[idx];
                    if (b.Group == TaskGroups.Break || b.Group == TaskGroups.Checkout || b.Group == TaskGroups.Flex) continue;
                    if (b.Group == g) continue;
                    // Never rob a SCARCE skill to serve an abundant one. Admin is
                    // manager-only and a store has one manager on before the doors open;
                    // Load Running is open to everybody. Unbounded, the cascade did
                    // exactly the wrong trade - it took the opening manager off the
                    // Monday date checks (due by 08:30) to run load an SA could have run,
                    // and re-homed the admin later in the day, past its deadline. Two
                    // hours of date checks came out as one. A group may only take over
                    // work that is at least as easy to staff as itself.
                    if (b.Group != TaskGroups.NonOp && TaskGroups.EligibleRoleCount(b.Group) < TaskGroups.EligibleRoleCount(g)) continue;
                    if (!LegalAt(s, g, b.Time)) continue;
                    cands.Add(new FlexRef(s, idx, b.Time));
                }
            }
            Js.Sort(cands, (a, b) => Math.Abs(a.Time - m) - Math.Abs(b.Time - m));
            foreach (var c in cands)
            {
                if (!_sameTaskWindow(g, m, c.Time)) continue;
                var h = c.Shift.Blocks[c.Idx].Group;
                if (!TakeNearest(h, c.Time)) continue;
                c.Shift.Blocks[c.Idx].Group = g;
                return true;
            }
            return false;
        }

        private void MakeUp()
        {
            _flexRefs = new List<FlexRef>();
            foreach (var s in _shifts)
            {
                if (s.Unfilled) continue;
                for (var idx = 0; idx < s.Blocks.Count; idx++)
                    if (s.Blocks[idx].Group == TaskGroups.Flex) _flexRefs.Add(new FlexRef(s, idx, s.Blocks[idx].Time));
            }

            // ── Tills first, and exactly ────────────────────────────────────────────
            // The registers are the one thing on the canvas that cannot move: the till
            // engine says how many are open in each fifteen minutes and a till is manned
            // in its own block or not at all. Everything else on the floor CAN move -
            // inside its own window - which is exactly what a store does when the front
            // end gets busy and the aisle waits twenty minutes.
            //
            // So where a block is short of tills and every body on it is doing aisle
            // work, take one: the block becomes checkout and the job that was there goes
            // back on the pile, to be re-placed below somewhere else in its own window.
            // Admin is exempt - a manager is the only person who can do it, and pulling
            // the one manager onto a register to free an SA achieves nothing.
            foreach (var d in _demandByBlock)
            {
                while (Rem(d.Time, TaskGroups.Checkout) > 0)
                {
                    (PaintableShift Shift, ShiftBlock Block)? donor = null;
                    foreach (var sh in _nonMgrFirst)
                    {
                        if (HasBreakAt(sh, d.Time)) continue;
                        if (!TaskGroups.EligibleFor(sh.Role, TaskGroups.Checkout) || IsRestricted(sh, TaskGroups.Checkout)) continue;
                        var block = sh.Blocks.Find(b => b.Time == d.Time);
                        if (block == null || block.Group == TaskGroups.Checkout || block.Group == TaskGroups.Break) continue;
                        if (block.Group == TaskGroups.Admin) continue; // manager-only; freeing it gains nothing
                        if (block.Group == TaskGroups.Flex)
                        {
                            donor = (sh, block);
                            break;
                        }
                        donor ??= (sh, block);
                    }
                    if (donor == null) break;
                    var rem = _remaining[d.Time];
                    if (donor.Value.Block.Group != TaskGroups.Flex)
                    {
                        var g = donor.Value.Block.Group;
                        if (g == TaskGroups.NonOp) _remainingU[d.Time] = RemU(d.Time) + 1;
                        else rem[g] = rem.GetValueOrDefault(g, 0) + 1;
                    }
                    donor.Value.Block.Group = TaskGroups.Checkout;
                    rem[TaskGroups.Checkout] -= 1;
                }
            }

            foreach (var d in _demandByBlock)
            {
                foreach (var g in _priority)
                {
                    if (g == TaskGroups.Checkout) continue;
                    while (Rem(d.Time, g) > 0)
                    {
                        if (!TakeNearest(g, d.Time) && !Steal(g, d.Time)) break;
                        _remaining[d.Time][g] -= 1;
                    }
                }
                while (RemU(d.Time) > 0)
                {
                    if (!TakeNearest(TaskGroups.NonOp, d.Time)) break;
                    _remainingU[d.Time] -= 1;
                }
            }
        }

        private static bool Swappable(string g) => g != TaskGroups.Break && g != TaskGroups.Flex && g != TaskGroups.Checkout;

        // ── Continuity pass: stop people flip-flopping between tasks ────────────
        // The main pass picks each block's group independently, so as demand ebbs
        // block to block a person could read Admin, Specials, Admin, Admin - and two
        // people could swap tasks with each other in the same 15 minutes. Nobody
        // works that way.
        //
        // This REASSIGNS groups between people WITHIN a block; it never changes what
        // a block contains. The multiset of groups at each time is exactly what the
        // demand passes produced, so coverage, totals and the benchmark reconciliation
        // are untouched - only who does which job moves, chosen to continue whatever
        // that person was already doing.
        private void ContinuityPass()
        {
            var times = Js.Sorted(_shifts.SelectMany(s => s.Blocks.Select(b => b.Time)).Distinct(), (a, b) => a - b);
            var prevGroup = new Dictionary<PaintableShift, string>(ByRef);
            foreach (var m in times)
            {
                var slots = new List<(PaintableShift Shift, ShiftBlock Block)>();
                foreach (var s in _shifts)
                {
                    if (s.Unfilled) continue;
                    var block = s.Blocks.Find(b => b.Time == m);
                    if (block != null && Swappable(block.Group)) slots.Add((s, block));
                }
                if (slots.Count > 1)
                {
                    var pool = slots.Select(x => x.Block.Group).ToList();
                    bool CanTake(PaintableShift s, string g) =>
                        g == TaskGroups.NonOp || (TaskGroups.EligibleFor(s.Role, g) && !IsRestricted(s, g));
                    var taken = new bool[pool.Count];
                    var assign = Enumerable.Repeat(-1, slots.Count).ToArray();
                    // First choice to whoever can carry on with what they were doing.
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (!prevGroup.TryGetValue(slots[i].Shift, out var want)) continue;
                        var j = -1;
                        for (var k = 0; k < pool.Count; k++)
                            if (!taken[k] && pool[k] == want && CanTake(slots[i].Shift, pool[k]))
                            {
                                j = k;
                                break;
                            }
                        if (j == -1) continue;
                        taken[j] = true;
                        assign[i] = j;
                    }
                    // Everyone else takes what is left that they are allowed to do.
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (assign[i] >= 0) continue;
                        var j = -1;
                        for (var k = 0; k < pool.Count; k++)
                            if (!taken[k] && CanTake(slots[i].Shift, pool[k]))
                            {
                                j = k;
                                break;
                            }
                        if (j == -1) continue;
                        taken[j] = true;
                        assign[i] = j;
                    }
                    // Commit ONLY a complete matching. If the greedy could not place someone
                    // the block is left exactly as it was: writing a partial result would
                    // hand one group to two people and silently drop another, which is a
                    // corrupted plan rather than an untidy one. Leaving every shift on its
                    // own group is always a valid fallback, since that is what it already
                    // had.
                    if (assign.All(j => j >= 0))
                        for (var i = 0; i < slots.Count; i++) slots[i].Block.Group = pool[assign[i]];
                }
                foreach (var (s, block) in slots) prevGroup[s] = block.Group;
            }
        }

        // ── Gap pass: no blank tiles in the middle of a shift ───────────────────
        // A flex block is paid time the demand curve had no job for. Left blank it
        // reads as a hole in the middle of someone's shift, and a store cannot
        // roster that - the person is on the floor and has to be doing something.
        // An INTERIOR gap (work on both sides) therefore continues the job either
        // side of it, which is what happens in practice: the task simply runs on.
        //
        // BUT it may never lift a labour group above the hours the day demanded of
        // it. Continuing the neighbouring job is only honest while that job still
        // has hours owing; past that it is inventing work, and because the
        // Benchmark table counts tiles the invention reads as real planned hours.
        // On G248's Monday the evening blanks (admin is manager-only, checkout
        // excludes juniors, so the extra evening slots had nothing legal to do)
        // continued into Reset and pushed it hours past its benchmark - the
        // "45 tiles of decard" that was really 22 tiles of decard plus fabrication.
        //
        // With no headroom left the block stays BLANK - it is not re-coloured as
        // Non-Op either, which is a benchmarked group in its own right and normally
        // zero. A run of blank evening tiles is the honest signal that the shift is
        // longer than the day's work; the shaper flags the same over-cover in
        // hours, and that is the thing to fix in the plan rather than to hide.
        //
        // Gaps at the very start or end of a shift are still left alone entirely.
        private bool HasHeadroom(string g) => _groupPainted.GetValueOrDefault(g, 0) < _groupDemand.GetValueOrDefault(g, 0);

        private void Claim(string g) => _groupPainted[g] = _groupPainted.GetValueOrDefault(g, 0) + 1;

        // A group whose work belongs to a window (Admin's jobs, the quoted Cleaning
        // hour) continues into a gap only for a quarter-hour still owed INSIDE that
        // window - the owed one is then marked done. On the day's total alone, five
        // quarter-hours of decard owed at 18:45 were "continued" at 11:30 and the
        // loss hidden (review RC-19); for Reset that is now allowed (decision 11).
        private bool Windowed(string g) =>
            g == TaskGroups.Admin
                ? (_opts?.UnitWindows != null && _opts.UnitWindows.Get(TaskGroups.Admin) != null) ||
                  (_opts?.TaskWindows != null && _opts.TaskWindows.Get(TaskGroups.Admin) != null)
                : g == TaskGroups.Cleaning && _opts?.TaskWindows != null && _opts.TaskWindows.Get(TaskGroups.Cleaning) != null;

        private double? OwedFor(string g, double t)
        {
            double? best = null;
            foreach (var (m, rem) in _remaining)
            {
                if (rem.GetValueOrDefault(g, 0) <= 0 || !_sameTaskWindow(g, m, t)) continue;
                if (best == null || Math.Abs(m - t) < Math.Abs(best.Value - t)) best = m;
            }
            return best;
        }

        private bool CanContinue(string g, double t) => Windowed(g) ? OwedFor(g, t) != null : HasHeadroom(g);

        private void GapPass()
        {
            foreach (var d in _demandByBlock)
            {
                double directed = 0;
                foreach (var (g, n) in d.Groups)
                {
                    _groupDemand[g] = _groupDemand.GetValueOrDefault(g, 0) + n;
                    directed += n;
                }
                _groupDemand[TaskGroups.NonOp] = _groupDemand.GetValueOrDefault(TaskGroups.NonOp, 0) + Math.Max(0, d.Total - directed);
            }
            foreach (var s in _shifts)
            {
                if (s.Unfilled) continue;
                foreach (var b in s.Blocks)
                    if (Swappable(b.Group)) _groupPainted[b.Group] = _groupPainted.GetValueOrDefault(b.Group, 0) + 1;
            }
            foreach (var s in _shifts)
            {
                if (s.Unfilled) continue;
                for (var i = 0; i < s.Blocks.Count; i++)
                {
                    if (s.Blocks[i].Group != TaskGroups.Flex) continue;
                    var end = i;
                    while (end + 1 < s.Blocks.Count && s.Blocks[end + 1].Group == TaskGroups.Flex) end++;
                    // Look PAST breaks for the enclosing work: a break is not the end of the
                    // shift, so a gap sitting next to one is still mid-shift and the job
                    // before it carries on afterwards.
                    static bool Skip(string g) => g == TaskGroups.Break || g == TaskGroups.Flex;
                    var b = i - 1;
                    while (b >= 0 && Skip(s.Blocks[b].Group)) b--;
                    var f = end + 1;
                    while (f < s.Blocks.Count && Skip(s.Blocks[f].Group)) f++;
                    var before = b >= 0 ? s.Blocks[b].Group : null;
                    var after = f < s.Blocks.Count ? s.Blocks[f].Group : null;
                    // ONE usable neighbour is enough. Requiring both meant a hole with a
                    // till on one side of it was never closed - checkout is manned in its
                    // own block and can never be continued, so it never qualifies - and the
                    // job on the other side was left hanging either side of a blank. The
                    // hole still has to be interior: work of some kind on both sides.
                    if (!string.IsNullOrEmpty(before) && !string.IsNullOrEmpty(after) && (Swappable(before) || Swappable(after)))
                    {
                        for (var k = i; k <= end; k++)
                        {
                            var time = s.Blocks[k].Time;
                            string? pick = null;
                            foreach (var g in new[] { before!, after! })
                                if (Swappable(g) && LegalAt(s, g, time) && CanContinue(g, time))
                                {
                                    pick = g;
                                    break;
                                }
                            // Neither neighbour has hours owing: the block stays BLANK. Non-Op
                            // is not a spare-time colour - it is its own labour group with its
                            // own benchmark, usually zero, and painting spare minutes into it
                            // put hours on a row the benchmark said should carry none. A blank
                            // tile is over-cover: the shift is longer than the day's work, which
                            // is a fact about the plan and belongs on the shaper's over-cover
                            // flag, not hidden inside a labour group.
                            if (pick == null) continue;
                            if (Windowed(pick))
                            {
                                var owed = OwedFor(pick, time)!.Value;
                                _remaining[owed][pick] -= 1;
                            }
                            s.Blocks[k].Group = pick;
                            Claim(pick);
                        }
                    }
                    i = end;
                }
            }
        }

        // Leftover flex blocks stay FLEX. They used to be "filled" with the nearest
        // group's colour so no cell sat empty - but that fabricates labour-group
        // hours no demand asked for, and the Benchmark table counts tiles (WYSIWYG),
        // so the padding read as phantom over-allocation: on G242, ~2.5h of
        // manager-only Admin demand had no manager free to take it, the spare SA
        // time it couldn't legally use was painted as Reset, and Reset showed
        // +2.55h over benchmark while Admin showed short. A blank cell is the
        // honest picture: paid time with no demanded task behind it.
        // ── Morning admin ──
        // The opening manager's first 15 minutes are admin - tills up, safe, emails,
        // the day sheet - before they take any floor work. One block, first MGR
        // shift of the day only. Checkout claims are sacrosanct (a converted till
        // block would leave a register demanded-but-unmanned), so the admin block
        // goes on the first non-checkout block inside the opening hour; if every
        // one of those is a till claim, the mandate is skipped for the day.
        private void MorningAdmin()
        {
            var openingMgr = Js.Sorted(_real.Where(s => Roles.IsManager(s.Role)), (a, b) => a.Start - b.Start).FirstOrDefault();
            if (openingMgr == null) return;
            // ...unless the demand curve already has them on it. The mandate exists to
            // make sure the opening manager starts on admin, not to add a block on top
            // of one: on G248's Monday the catalogue already puts the manager on the
            // Longlife date checks from 06:15, and converting 06:00 as well pushed Admin
            // a quarter-hour past its benchmark and left the load a body short.
            var alreadyAdmin = openingMgr.Blocks.Any(b => b.Time < openingMgr.Start + 60 && b.Group == TaskGroups.Admin);
            // And it is made of admin the day already has, moved forward - never new
            // admin hours on top of the benchmark (owner decision 5). Converting a block
            // outright was the commonest way a day painted more admin than Stage 1
            // allocated. The opener swaps their first non-till block with a later admin
            // block of their own, where both jobs are legal at their new minute and the
            // admin stays inside its own task's window; with no such block, the quarter
            // is left as it is.
            if (alreadyAdmin) return;
            var candidate = openingMgr.Blocks.Find(b =>
                b.Time >= openingMgr.Start &&
                b.Time < openingMgr.Start + 60 &&
                b.Group != TaskGroups.Break &&
                b.Group != TaskGroups.Checkout);
            if (candidate == null || !LegalAt(openingMgr, TaskGroups.Admin, candidate.Time)) return;
            var donor = openingMgr.Blocks.Find(b =>
                b.Group == TaskGroups.Admin &&
                b.Time > candidate.Time &&
                _sameTaskWindow(TaskGroups.Admin, b.Time, candidate.Time) &&
                (candidate.Group == TaskGroups.Flex ||
                 (LegalAt(openingMgr, candidate.Group, b.Time) && _sameTaskWindow(candidate.Group, candidate.Time, b.Time))));
            if (donor == null) return;
            donor.Group = candidate.Group;
            candidate.Group = TaskGroups.Admin;
        }

        // ── No blank tiles, anywhere - and never at the price of a rule ─────────
        // The plan is not a set of shifts we then hunt for work to fill. It is the
        // work, with the shifts cut around it - so a blank block is not a hole to
        // paper over, it is a shift boundary in the wrong place, and the honest fix
        // is to move the boundary.
        //
        // A blank in the MIDDLE cannot simply be dropped without cutting the shift
        // in two, so it walks to an edge first. Walking is a swap with the next
        // block along: the person does at 16:00 what they were going to do at 16:15,
        // which is a free move as long as that job is legal at the earlier minute
        // and stays inside its own task window. A till never moves - it is manned in
        // its own block or not at all - so a blank walks around it, or trades the
        // till to someone else on at that minute. Once the blank reaches an edge the
        // shift gives that quarter hour up: paid time the day had no work for.
        //
        // Every one of those moves used to be made on trust, and the review of
        // 07/11/2026 found them breaking the rules the rest of the plan keeps: a
        // manager trimmed so nobody was in charge, a closer left alone, a junior with
        // two adults, a break stepped into the first hour or past the 5th, a fixed
        // roster cut short, and an "undo" that wrote back over a break. So each
        // attempt is now made against a SNAPSHOT of the whole day and kept only if
        // the day still passes every rule it passed before it (accepted() below):
        //   - a fixed roster or pinned shift keeps its times exactly;
        //   - no shift falls under three hours;
        //   - every break that was legal stays legal - entitled minutes, segments,
        //     not in the first hour, an hour of work after it, the meal by the 5th;
        //   - at every quarter-hour somebody leaves: no fewer than the floor
        //     minimum where there were enough, a manager still on where one was, a
        //     junior still with three adults, and the store never emptied while
        //     there is work to do.
        // Otherwise the snapshot is restored exactly. Safety beats tidiness: a blank
        // that cannot be cleared without breaking a rule stays, and is returned to
        // the caller to flag by name rather than hidden.
        private DaySnapshot Snapshot() => new(
            _real.Select(s => new ShiftState(s, s.Start, s.End, s.Blocks.Select(b => b.Clone()).ToList())).ToList(),
            CopyRemaining(_remaining),
            new Dictionary<double, double>(_remainingU));

        private void Restore(DaySnapshot snap)
        {
            foreach (var x in snap.Shifts)
            {
                x.Shift.Start = x.Start;
                x.Shift.End = x.End;
                x.Shift.Blocks = x.Blocks.Select(b => b.Clone()).ToList();
            }
            foreach (var t in _remaining.Keys.ToList())
                _remaining[t] = snap.Owed.TryGetValue(t, out var g) ? new Dictionary<string, double>(g) : new Dictionary<string, double>();
            foreach (var t in _remainingU.Keys.ToList())
            {
                // (A key the snapshot lacks reads as undefined in the TypeScript; it
                // cannot arise, since this pass never adds one.)
                if (snap.OwedU.TryGetValue(t, out var u)) _remainingU[t] = u;
                else _remainingU.Remove(t);
            }
        }

        // A blank quarter-hour at a minute where demanded work is still unpainted
        // takes that work - owed, never invented. A short till first for a store
        // assistant, last for a manager (register etiquette). Owed means both: still
        // outstanding at that minute, AND the group still under its demand for the
        // day - the gap pass settles some of a group's hours by continuing a
        // neighbouring job without saying which minute's demand it met, and taking
        // the same unit twice would paint the group above its allocation (R2).
        // Tills are counted per minute, so the minute alone decides them.
        private int PaintedOf(string g)
        {
            var n = 0;
            foreach (var sh in _real)
                foreach (var x in sh.Blocks)
                    if (x.Group == g) n++;
            return n;
        }

        private bool TakeOwed(PaintableShift s, ShiftBlock b)
        {
            if (_remaining.TryGetValue(b.Time, out var rem))
            {
                List<string> order = Roles.IsManager(s.Role)
                    ? [.. CandidateOrder(s.Role, _priority), TaskGroups.Checkout]
                    : [TaskGroups.Checkout, .. CandidateOrder(s.Role, _priority)];
                foreach (var g in order)
                {
                    if (rem.GetValueOrDefault(g, 0) <= 0 || !LegalAt(s, g, b.Time)) continue;
                    if (g != TaskGroups.Checkout && PaintedOf(g) >= _groupDemand.GetValueOrDefault(g, 0)) continue;
                    rem[g] = rem.GetValueOrDefault(g, 0) - 1;
                    b.Group = g;
                    return true;
                }
            }
            if (RemU(b.Time) > 0 && PaintedOf(TaskGroups.NonOp) < _groupDemand.GetValueOrDefault(TaskGroups.NonOp, 0))
            {
                _remainingU[b.Time] -= 1;
                b.Group = TaskGroups.NonOp;
                return true;
            }
            return false;
        }

        private static bool BreakLegal(string role, double start, double end, List<ShiftBlock> blocks)
        {
            var times = blocks.Where(b => b.Group == TaskGroups.Break).Select(b => b.Time).ToList();
            return Breaks.BreakTileError(times, Breaks.BreakForRole(role, (end - start) / 60).TotalMinutes, start, end) == null;
        }

        private static Cover CoverAt(IEnumerable<(string Role, double Start, double End)> spans, double m)
        {
            int people = 0, managers = 0, adults = 0, juniors = 0;
            foreach (var s in spans)
            {
                if (m < s.Start || m >= s.End) continue;
                people++;
                if (Roles.IsManager(s.Role)) managers++;
                if (s.Role == Roles.JSA) juniors++;
                else adults++;
            }
            return new Cover(people, managers, adults, juniors);
        }

        // Does the change from `was` to `now` at minute m break a cover rule that
        // held - or make one that was already broken worse?
        private bool CoverWorse(Cover was, Cover now, double m)
        {
            if (now.People == 0) return was.People > 0 && _demandTotal.GetValueOrDefault(m, 0) > 0;
            if (now.People < _minPeople && now.People < was.People) return true;
            if (now.Managers == 0 && was.Managers > 0) return true;
            if (now.Juniors > 0 && now.Adults < _minAdults && now.Adults < was.Adults) return true;
            return false;
        }

        // The managers' handover: the overlap of the opener and the closer.
        private static double? Handover(List<(string Role, double Start, double End)> sh)
        {
            var mgrs = sh.Where(x => Roles.IsManager(x.Role)).ToList();
            if (mgrs.Count != 2) return null;
            return Math.Min(mgrs[0].End, mgrs[1].End) - Math.Max(mgrs[0].Start, mgrs[1].Start);
        }

        private List<(string Role, double Start, double End)> RealSpans() => _real.Select(s => (s.Role, s.Start, s.End)).ToList();

        private static Dictionary<string, int> Tiles(IEnumerable<List<ShiftBlock>> blocks)
        {
            var n = new Dictionary<string, int>();
            foreach (var bs in blocks)
                foreach (var b in bs)
                    if (b.Group != TaskGroups.Flex && b.Group != TaskGroups.Break) n[b.Group] = n.GetValueOrDefault(b.Group, 0) + 1;
            return n;
        }

        private static Dictionary<double, int> TillsBy(IEnumerable<List<ShiftBlock>> blocks)
        {
            var n = new Dictionary<double, int>();
            foreach (var bs in blocks)
                foreach (var b in bs)
                    if (b.Group == TaskGroups.Checkout) n[b.Time] = n.GetValueOrDefault(b.Time, 0) + 1;
            return n;
        }

        private bool Accepted(DaySnapshot snap)
        {
            foreach (var x in snap.Shifts)
            {
                var s = x.Shift;
                if (s.Start != x.Start || s.End != x.End)
                {
                    if (s.Fixed) return false;
                    if (s.End - s.Start < MinShiftMin) return false;
                    // A manager shift is at least six hours (owner decision R7).
                    if (Roles.IsManager(s.Role) && s.End - s.Start < _mgrMinShiftMin && x.End - x.Start >= _mgrMinShiftMin) return false;
                }
                if (BreakLegal(s.Role, x.Start, x.End, x.Blocks) && !BreakLegal(s.Role, s.Start, s.End, s.Blocks))
                    return false;
            }
            // The handover is a rule (owner decision 5): clearing a blank never takes
            // it below the hour (review RC-14: the no-blank pass ate it on 18 days).
            var was = snap.Shifts.Select(x => (x.Shift.Role, x.Start, x.End)).ToList();
            var now = RealSpans();
            var hoWas = Handover(was);
            var hoNow = Handover(now);
            if (hoWas != null && hoNow != null && hoNow.Value < hoWas.Value && hoNow.Value < _handoverMin) return false;
            foreach (var x in snap.Shifts)
            {
                var s = x.Shift;
                for (var m = x.Start; m < x.End; m += Grid.BlockMin)
                {
                    if (m >= s.Start && m < s.End) continue; // still on at m
                    if (CoverWorse(CoverAt(was, m), CoverAt(now, m), m)) return false;
                }
            }
            // Clearing a blank must never cost real work. A trim that sends the work
            // on a shift's edge back to the pile, where nobody can take it, lost a
            // quarter-hour of the morning load at 09:45 to tidy a junior's two blank
            // tills after 10:00.
            var before = Tiles(snap.Shifts.Select(x => x.Blocks));
            var after = Tiles(_real.Select(s => s.Blocks));
            foreach (var (g, n) in before)
                if (after.GetValueOrDefault(g, 0) < n) return false;
            // ...and a till is counted quarter-hour by quarter-hour: one manned
            // later does not make up for one left empty now.
            var tillsBefore = TillsBy(snap.Shifts.Select(x => x.Blocks));
            var tillsAfter = TillsBy(_real.Select(s => s.Blocks));
            foreach (var (t, n) in tillsBefore)
                if (tillsAfter.GetValueOrDefault(t, 0) < n) return false;
            return true;
        }

        // Run one attempt; keep what it did only if the day is still within the
        // rules, otherwise put everything back exactly as it was.
        private bool Attempt(Func<bool> move)
        {
            var snap = Snapshot();
            var done = move();
            if (Accepted(snap)) return done;
            Restore(snap);
            return false;
        }

        // Hand back the quarter hour at one edge of a shift. A shorter shift can fall
        // into a smaller break entitlement (tiered: over 6h, two 30-minute breaks;
        // over 4h, one of 30; otherwise 15). The break is then cut to the new
        // entitlement - the legal run of it nearest the middle of the shift is kept
        // - and the minutes it frees go back to work: first the work the longer
        // break had pushed out (takeOwed), otherwise the rounds below move work in
        // or trim them off in turn. That is the owner's own example: 06:00-10:30
        // becomes 06:00-10:00, with a shorter break and two reset tiles moved down
        // into the gap. If the break cannot then sit legally, accepted() refuses
        // the trim.
        private void TrimEdge(PaintableShift s, bool atStart)
        {
            if (atStart)
            {
                if (s.Blocks.Count > 0) s.Blocks.RemoveAt(0);
                s.Start += Grid.BlockMin;
            }
            else
            {
                if (s.Blocks.Count > 0) s.Blocks.RemoveAt(s.Blocks.Count - 1);
                s.End -= Grid.BlockMin;
            }
            var want = Breaks.BreakForRole(s.Role, (s.End - s.Start) / 60).TotalMinutes;
            var held = s.Blocks.Where(b => b.Group == TaskGroups.Break).ToList();
            if (want > 0 && held.Count * Grid.BlockMin > want)
            {
                var times = held.Select(b => b.Time).ToList();
                List<double>? keep = null;
                var bestDist = double.PositiveInfinity;
                foreach (var t0 in times)
                {
                    var run = new List<double>();
                    for (var m = t0; m < t0 + want; m += Grid.BlockMin) run.Add(m);
                    if (!run.All(times.Contains)) continue;
                    var dist = Math.Abs(t0 + want / 2 - (s.Start + s.End) / 2);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        keep = run;
                    }
                }
                if (keep != null)
                {
                    foreach (var b in held)
                    {
                        if (keep.Contains(b.Time)) continue;
                        b.Group = TaskGroups.Flex;
                        TakeOwed(s, b);
                    }
                }
            }
            SlideBreaks(s);
        }

        // A trim moves a shift's edge towards its break: a later start can put the
        // break inside the first hour, an earlier finish leave less than an hour of
        // work after it, and the meal can end up past the 5th hour. A person would
        // just take the break a quarter-hour later or earlier, so the break slides:
        // it swaps with the job beside it one block at a time, while that job is
        // legal a quarter-hour along and inside its own task window. A till never
        // swaps - it is manned in its own block or not at all - and two breaks never
        // run together. If the break still cannot sit legally, accepted() refuses
        // the trim.
        private void SlideBreaks(PaintableShift s)
        {
            double IdxOf(double t) => (t - s.Start) / Grid.BlockMin;
            static bool CanSwap(string g) => g != TaskGroups.Checkout && g != TaskGroups.Break;
            for (var step = 0; step < 16; step++)
            {
                var times = Js.Sorted(s.Blocks.Where(b => b.Group == TaskGroups.Break).Select(b => b.Time), (a, b) => a - b);
                if (times.Count == 0) return;
                var runs = new List<(double Start, double Len)>();
                foreach (var t in times)
                {
                    if (runs.Count > 0 && runs[^1].Start + runs[^1].Len == t) runs[^1] = (runs[^1].Start, runs[^1].Len + Grid.BlockMin);
                    else runs.Add((t, Grid.BlockMin));
                }
                (double Start, double Len)? run = null;
                var dir = 1;
                var mealIdx = runs.FindIndex(r => r.Len >= 30);
                if (runs[0].Start < s.Start + BreakEdgeMin)
                {
                    run = runs[0];
                    dir = 1;
                }
                else if (runs[^1].Start + runs[^1].Len > s.End - BreakEdgeMin)
                {
                    run = runs[^1];
                    dir = -1;
                }
                else if (mealIdx != -1 && runs[mealIdx].Start > s.Start + MealLatestStartMin)
                {
                    run = runs[mealIdx];
                    dir = -1;
                }
                if (run == null) return; // where it sits is legal
                // The job that trades places with the break, and the minute it moves to.
                var r0 = run.Value;
                var from = dir == 1 ? r0.Start + r0.Len : r0.Start - Grid.BlockMin;
                var to = dir == 1 ? r0.Start : r0.Start + r0.Len - Grid.BlockMin;
                var beyond = dir == 1 ? from + Grid.BlockMin : from - Grid.BlockMin;
                var fb = At(s.Blocks, IdxOf(from));
                if (fb == null || !CanSwap(fb.Group)) return;
                if (At(s.Blocks, IdxOf(beyond))?.Group == TaskGroups.Break) return; // would merge two breaks
                var g = fb.Group;
                if (g != TaskGroups.Flex)
                {
                    if (!LegalAt(s, g, to)) return;
                    if (!_sameTaskWindow(g, from, to)) return;
                }
                At(s.Blocks, IdxOf(to))!.Group = g;
                fb.Group = TaskGroups.Break;
            }
        }

        // Walk ONE blank in this shift out to an edge. Returns the edge it reached
        // (0 or the last index), or -1 when it is boxed in - a till on one side, the
        // day's edge hour on the other. A walk that stops short keeps the ground it
        // won (if attempt() accepts it): every step is a legal move, and a blank
        // moved somewhere new can be handed to a shift that CAN finish the job.
        private int WalkToEdge(PaintableShift s)
        {
            if (s.Fixed) return -1; // its times are not ours to change
            var i = s.Blocks.FindIndex(b => b.Group == TaskGroups.Flex);
            if (i == -1) return -1;
            if (s.End - s.Start - Grid.BlockMin < MinShiftMin) return -1;
            var toStart = i;
            var toEnd = s.Blocks.Count - 1 - i;
            int[] dirs = toStart <= toEnd ? [-1, 1] : [1, -1];
            var landed = -1;
            var at = i;
            foreach (var dir in dirs)
            {
                var k = at;
                for (;;)
                {
                    var next = k + dir;
                    if (next < 0 || next >= s.Blocks.Count) break; // reached the edge
                    var nb = s.Blocks[next];
                    if (nb.Group == TaskGroups.Flex)
                    {
                        // Another blank: two empty blocks swapping places is not a move.
                        k = next;
                        continue;
                    }
                    if (nb.Group == TaskGroups.Break)
                    {
                        // The award fixes how LONG a break is, not its exact minute: step it
                        // one block along and let the blank past. accepted() checks the
                        // break is still legal where it lands.
                        s.Blocks[k].Group = TaskGroups.Break;
                        nb.Group = TaskGroups.Flex;
                        k = next;
                        continue;
                    }
                    var here = s.Blocks[k].Time;
                    bool Movable(string g) =>
                        g != TaskGroups.Checkout &&
                        g != TaskGroups.Break &&
                        g != TaskGroups.Flex &&
                        LegalAt(s, g, here) &&
                        _sameTaskWindow(g, nb.Time, here);
                    if (!Movable(nb.Group))
                    {
                        // This job cannot come back a quarter of an hour - a till, a window
                        // just shut, a task's own deadline. Trade it with somebody else on at
                        // that minute who can take it on, and take their job instead: the
                        // same work, by the same number of people, in the same 15 minutes.
                        var mine = nb.Group;
                        var mate = _nonMgrFirst.Find(o =>
                        {
                            if (ReferenceEquals(o, s) || o.Unfilled) return false;
                            var ob = o.Blocks.Find(x => x.Time == nb.Time);
                            if (ob == null || ob.Group == TaskGroups.Break || ob.Group == TaskGroups.Flex) return false;
                            if (!LegalAt(o, mine, nb.Time)) return false;
                            return Movable(ob.Group);
                        });
                        if (mate == null) break;
                        var mateBlock = mate.Blocks.Find(x => x.Time == nb.Time)!;
                        nb.Group = mateBlock.Group;
                        mateBlock.Group = mine;
                    }
                    s.Blocks[k].Group = nb.Group;
                    nb.Group = TaskGroups.Flex;
                    k = next;
                }
                at = k;
                if (k == 0 || k == s.Blocks.Count - 1)
                {
                    landed = k;
                    break;
                }
            }
            return landed;
        }

        // Two people idle at the same edge minute - both closers with nothing in
        // the last quarter-hour, say - cannot be trimmed one at a time: the first
        // trim would leave the other alone. Trimmed together they leave nobody, which
        // accepted() allows only where that minute has no work.
        private bool TrimTogether(PaintableShift s, bool atStart)
        {
            ShiftBlock? EdgeOf(PaintableShift o) =>
                o.Blocks.Count == 0 ? null : atStart ? o.Blocks[0] : o.Blocks[^1];
            var edge = EdgeOf(s);
            if (edge == null) return false;
            var m = edge.Time;
            var group = _real.Where(o =>
                !o.Fixed &&
                o.End - o.Start - Grid.BlockMin >= MinShiftMin &&
                EdgeOf(o)?.Group == TaskGroups.Flex &&
                EdgeOf(o)?.Time == m).ToList();
            if (group.Count < 2) return false;
            return Attempt(() =>
            {
                foreach (var o in group) TrimEdge(o, atStart);
                return true;
            });
        }

        // Pull work OUT of somebody else's edge and into the blank, then shorten
        // them. Where a blank is sealed in - a register on one side nobody free can
        // take, a labour window shut on the other - the day still has one body-block
        // more than it has work, and the surplus does not have to be next to the
        // blank to be given back. On G999's Sunday the closing manager sat blank at
        // 13:30 while the OPENER finished with two blocks of re-run at 11:00: the
        // closer does that stock and the opener goes home earlier.
        private bool PullFromEdge(PaintableShift a, int ai)
        {
            var t = a.Blocks[ai].Time;
            foreach (var b in _real)
            {
                if (ReferenceEquals(b, a) || b.Fixed) continue;
                if (b.End - b.Start - Grid.BlockMin < MinShiftMin) continue;
                foreach (var end in new[] { true, false })
                {
                    var bi = end ? b.Blocks.Count - 1 : 0;
                    var eb = bi >= 0 && bi < b.Blocks.Count ? b.Blocks[bi] : null;
                    if (eb == null || eb.Group == TaskGroups.Checkout || eb.Group == TaskGroups.Break || eb.Group == TaskGroups.Flex) continue;
                    var g = eb.Group;
                    if (!LegalAt(a, g, t) || !_sameTaskWindow(g, eb.Time, t)) continue;
                    // Try it; accepted() decides whether the floor can spare them.
                    var ok = Attempt(() =>
                    {
                        a.Blocks[ai].Group = g;
                        TrimEdge(b, !end);
                        return true;
                    });
                    if (ok) return true;
                }
            }
            return false;
        }

        // ...and if it is boxed in, pass it on: this shift takes the work of
        // somebody on at the same minute, they take the blank and try to clear it.
        // What was left on G999 was usually a manager, whose span is set by opening
        // and closing the store, so a blank can travel two or three shifts to reach
        // one with an edge to spare. A chain that dead-ends is restored from its
        // snapshot - every shift exactly as it was, breaks included.
        //
        // The search re-walks the same failed chains at every level - 12, 84, 420,
        // 1,680, 5,040, 10,080 attempts - so on a few early-closing weekend days it
        // never finished (review RC-2). Every day searches exactly as before until it
        // has used EXACT_PASS_WORK attempts; past that a shift a hand-over has
        // already dead-ended on (`failed`, cleared for every new blank) is not tried
        // again, which bounds the search, and the day says so (searchCapped).
        // BLANK_PASS_BUDGET stops the pass outright as a last backstop.
        private bool TryClear(PaintableShift s, int depth, HashSet<PaintableShift> seen)
        {
            if (++_passWork > BlankPassBudget) return false;
            var edge = -1;
            var walked = Attempt(() =>
            {
                edge = WalkToEdge(s);
                return edge != -1;
            });
            if (walked)
            {
                var atStart = edge == 0;
                if (Attempt(() =>
                    {
                        TrimEdge(s, atStart);
                        return true;
                    }))
                    return true;
                if (TrimTogether(s, atStart)) return true;
            }
            var pi = s.Blocks.FindIndex(b => b.Group == TaskGroups.Flex);
            if (pi != -1 && PullFromEdge(s, pi)) return true;
            if (depth <= 0) return false;
            var blankTimes = s.Blocks.Where(b => b.Group == TaskGroups.Flex).Select(b => b.Time).ToList();
            if (blankTimes.Count == 0) return false;
            foreach (var o in _real)
            {
                if (ReferenceEquals(o, s) || seen.Contains(o) || (_passWork > ExactPassWork && _failed.Contains(o))) continue;
                // Trade at the blank minute nearest one of THEIR edges, so the blank
                // they take on can go straight back out of their shift.
                double EdgeDist(double m) => Math.Min(m - o.Start, o.End - Grid.BlockMin - m);
                bool Tradeable(double m)
                {
                    if (m < o.Start || m >= o.End) return false;
                    var g = At(o.Blocks, (m - o.Start) / Grid.BlockMin)?.Group;
                    // A till can change hands at the same minute - the count is unchanged
                    // - and a manager with nothing else to do taking one so an assistant
                    // can go home is register etiquette, not a breach of it.
                    return g != null && g != TaskGroups.Break && g != TaskGroups.Flex && LegalAt(s, g, m);
                }
                double? t = null;
                foreach (var x in Js.Sorted(blankTimes, (x, y) => EdgeDist(x) - EdgeDist(y)))
                    if (Tradeable(x))
                    {
                        t = x;
                        break;
                    }
                if (t == null) continue;
                var ai = (int)((t.Value - s.Start) / Grid.BlockMin);
                var oi = (int)((t.Value - o.Start) / Grid.BlockMin);
                var g = o.Blocks[oi].Group;
                var snap = Snapshot();
                s.Blocks[ai].Group = g;
                o.Blocks[oi].Group = TaskGroups.Flex;
                seen.Add(o);
                if (TryClear(o, depth - 1, seen)) return true;
                seen.Remove(o);
                _failed.Add(o);
                Restore(snap);
            }
            return false;
        }

        private void NoBlankPass()
        {
            _handoverMin = _opts?.Handover?.MinMin ?? 60;
            _mgrMinShiftMin = _opts?.Handover?.ManagerMinShiftMin ?? 360;
            _minPeople = _opts?.Cover?.MinPeople ?? RosteringRules.Default.MinPeopleOnFloor;
            _minAdults = _opts?.Cover?.MinAdultsWithJunior ?? RosteringRules.Default.MinAdultsWithJsa;

            // Rounds, not one pass: a blank that only moved this time round may be
            // clearable next time, once its neighbours have moved too. A round counts as
            // progress if it removed a blank or shortened a shift (a shortened shift can
            // free break minutes, so the blank count alone can rise and still be
            // progress); stop at the first round that does neither.
            int BlanksLeft() => _real.Sum(s => s.Blocks.Count(b => b.Group == TaskGroups.Flex));
            double SpanLeft()
            {
                double n = 0;
                foreach (var s in _real) n = n + s.End - s.Start;
                return n;
            }
            for (var round = 0; round < 40; round++)
            {
                var blanks = BlanksLeft();
                var span = SpanLeft();
                if (blanks == 0) break;
                foreach (var s in _real)
                {
                    for (var guard = 0; guard < 60; guard++)
                    {
                        if (!s.Blocks.Any(b => b.Group == TaskGroups.Flex)) break;
                        _failed.Clear();
                        if (!TryClear(s, PassDepth, new HashSet<PaintableShift>(ByRef) { s })) break;
                    }
                }
                if (_passWork > BlankPassBudget) break;
                if (BlanksLeft() >= blanks && SpanLeft() >= span) break;
            }
            // Blanks that moved during the rounds may now sit where work is still owed.
            foreach (var s in _real)
                foreach (var b in s.Blocks)
                    if (b.Group == TaskGroups.Flex) TakeOwed(s, b);
        }

        private void Finish()
        {
            // Work no tile carries, by group, for the caller to flag.
            var demanded = new JsMap<string, double>();
            foreach (var d in _demandByBlock)
            {
                double gsum = 0;
                foreach (var (g, n) in d.Groups)
                {
                    demanded.Set(g, demanded.GetOr(g, 0) + n);
                    gsum += n;
                }
                if (d.Total > gsum) demanded.Set(TaskGroups.NonOp, demanded.GetOr(TaskGroups.NonOp, 0) + d.Total - gsum);
            }
            var painted = new Dictionary<string, double>();
            foreach (var s in _real)
                foreach (var b in s.Blocks)
                    painted[b.Group] = painted.GetValueOrDefault(b.Group, 0) + 1;
            Lost = demanded
                .Select(kv => new LostWork(kv.Key, kv.Value - painted.GetValueOrDefault(kv.Key, 0)))
                .Where(x => x.Blocks > 0)
                .ToList();
            // What could not be cleared without breaking a rule, for the caller to flag.
            Blanks = _real
                .Select(shift => (Shift: shift, Times: shift.Blocks.Where(b => b.Group == TaskGroups.Flex).Select(b => b.Time).ToList()))
                .Where(x => x.Times.Count > 0)
                .ToList();
            SearchCapped = _passWork > ExactPassWork;
        }
    }
}
