// Port of src/lib/stage2/shaper.ts: shapePlan and its passes (levelling, the
// pre-open compression, the manager pair). The entry points' other half is
// PlanShaper.cs; the types are ShaperTypes.cs.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Rostering.Shaper;

public static partial class PlanShaper
{
    /// <summary>A quarter-hour with an hourly pay premium (Stage 1's definition).</summary>
    private static double PremiumHead(double m) => m < Stage1Engine.PremiumBeforeMin || m >= Stage1Engine.PremiumFromMin ? 1 : 0;

    private static string Fmt(double m) => Grid.MinToTime(m);

    // ─── Work levelling (lean smoothing before the cut) ──────────────────────────
    // Stage 1 places each group's hours greedily, which stacks narrow two-high
    // towers no legal ≥3h shift can trace - the padding those towers force is
    // exactly the "planned above the curve" over-cover that blows the daily
    // allowance. Every group except checkout is movable inside its legal window,
    // so re-place them by WATER-FILLING: tightest window first, each 15-min unit
    // dropped on the block with the lowest running total (ties resolved toward the
    // middle of the day, where a step merges into existing shifts instead of
    // stranding a fragment at the day's edge). Checkout and undirected (non-op)
    // demand stay pinned. Group sums are preserved exactly - work moves in time,
    // it never appears or disappears.
    //
    // Levelling moves work only where Stage 1 left it free to move. Pooling a
    // group across its whole window erased the placements Stage 1 makes on
    // purpose (Roster Engine Review 07/11/2026, RC4): G999's quoted hour of
    // cleaning at 09:00-10:00 went wherever cleaning's window was quietest - the
    // evening - and the load moved in from a public holiday, which Stage 1 puts
    // in the evening before, went back to the morning. So:
    //   - work Stage 1 put OUTSIDE its group's window stays where it is. Stage 1
    //     does that only on purpose (the public-holiday load after 18:00), and
    //     the window is the one place levelling would otherwise send it;
    //   - the hours Stage 1 put inside a QUOTED window stay inside it, levelled
    //     across that window one layer deep, as Stage 1 placed them.
    private sealed class Pool
    {
        public required string Group { get; init; }
        public double Units { get; init; }
        public required List<double> Blocks { get; init; }
        public double Cap { get; init; }
    }

    private static void LevelFlexibleWork(
        JsMap<double, DemandBlock> demand,
        Func<double, IReadOnlyList<string>?>? allowedAt,
        IReadOnlyList<string> pinned,
        IReadOnlyList<QuotedWindow> quoted,
        JsMap<string, string>? lean,
        (double From, double Floor)? closeDown)
    {
        // Checkout is always pinned - a till is manned in its own block or not at
        // all. `pinned` adds the groups Stage 1 placed TASK-FIRST: those hours are
        // named jobs sitting inside their own deadline (the Monday date checks are
        // due before 08:30), and pooling them across the group's whole legal window
        // moved them out of it. Levelling is for bulk work with real slack, not for
        // a job with a time on it.
        var pinnedSet = new JsSet<string>([TaskGroups.Checkout, .. pinned]);
        var times = Js.Sorted(demand.Keys(), (a, b) => a - b);
        if (times.Count == 0) return;
        var span = new List<double>();
        for (var m = times[0]; m < times[^1] + B; m += B) span.Add(m);
        var dayMid = (span[0] + span[^1] + B) / 2;
        bool InWindow(string g, double m) => allowedAt is null || (allowedAt(m)?.Contains(g) ?? false);

        // Split each block into what stays put (checkout, pinned groups, undirected
        // demand, work outside its group's window) and what may move.
        var movable = new JsMap<string, JsMap<double, double>>(); // group -> block -> units
        var seen = new JsMap<string, (double From, double To)>();
        var total = new JsMap<double, double>();
        var pinnedGroups = new JsMap<double, JsMap<string, double>>();
        var undirected = new JsMap<double, double>();
        foreach (var m in span)
        {
            var d = demand.Get(m);
            double pinnedCount = 0;
            var pg = new JsMap<string, double>();
            if (d is not null)
            {
                double gsum = 0;
                foreach (var n in d.Groups.Values()) gsum += n;
                var u = Math.Max(0, d.Total - gsum);
                undirected.Set(m, u);
                pinnedCount += u;
                foreach (var (g, n) in d.Groups)
                {
                    if (!Js.Truthy(n)) continue;
                    if (pinnedSet.Has(g) || !InWindow(g, m))
                    {
                        pg.Set(g, pg.GetOr(g, 0) + n);
                        pinnedCount += n;
                        continue;
                    }
                    var byBlock = movable.Get(g) ?? new JsMap<double, double>();
                    byBlock.Set(m, byBlock.GetOr(m, 0) + n);
                    movable.Set(g, byBlock);
                    seen.Set(g, seen.TryGet(g, out var sp) ? (Math.Min(sp.From, m), Math.Max(sp.To, m + B)) : (m, m + B));
                }
            }
            pinnedGroups.Set(m, pg);
            total.Set(m, pinnedCount);
        }
        if (movable.Size == 0) return;

        List<double> LegalBlocks(string g)
        {
            if (allowedAt is not null)
            {
                var out_ = span.Where(m => InWindow(g, m)).ToList();
                if (out_.Count > 0) return out_;
            }
            var sp = seen.Get(g);
            return span.Where(m => m >= sp.From && m < sp.To).ToList();
        }

        // Units levelled together over their own blocks, at most `cap` per block.
        var pools = new List<Pool>();
        // A quoted window's hours first, in start order as Stage 1 filled them:
        // what Stage 1 put inside the window, up to the quota and one layer deep,
        // leaves the group's pool and is levelled inside the window alone.
        foreach (var w in Js.Sorted(quoted, (a, b) => a.Start - b.Start))
        {
            var byBlock = movable.Get(w.Group);
            if (byBlock is null) continue;
            var blocks = span.Where(m => m >= w.Start && m < w.End && InWindow(w.Group, m)).ToList();
            var want = Math.Min(ToBlocks(w.Hours), blocks.Count);
            double units = 0;
            foreach (var m in blocks)
            {
                if (want <= 0) break;
                var take = Math.Min(byBlock.GetOr(m, 0), want);
                if (take <= 0) continue;
                byBlock.Set(m, byBlock.GetOr(m, 0) - take);
                want -= take;
                units += take;
            }
            if (units > 0) pools.Add(new Pool { Group = w.Group, Units = units, Blocks = blocks, Cap = 1 });
        }
        // The rest of a quoted group levels OUTSIDE its quoted windows: the morning
        // gets exactly its quoted hour, and levelling may not add to it (owner
        // decision 12; review RC-21: the morning hour was painted six deep where
        // Stage 1 had four).
        bool InQuoted(string g, double m) => quoted.Any(w => w.Group == g && m >= w.Start && m < w.End);
        foreach (var (g, byBlock) in movable)
        {
            double units = 0;
            foreach (var n in byBlock.Values()) units += n;
            if (units <= 0) continue;
            var legal = LegalBlocks(g);
            var outside = legal.Where(m => !InQuoted(g, m)).ToList();
            pools.Add(new Pool { Group = g, Units = units, Blocks = outside.Count > 0 ? outside : legal, Cap = double.PositiveInfinity });
        }

        // Tightest window first (Load Running's 4h window has no slack; admin's 10h
        // window can flow around whatever the tight groups need).
        Js.Sort(pools, (a, b) => a.Blocks.Count - b.Blocks.Count);
        var placed = new JsMap<double, JsMap<string, double>>();
        foreach (var p in pools)
        {
            var mine = new JsMap<double, double>();
            var way = lean?.Get(p.Group);
            for (var units = p.Units; units > 0; units--)
            {
                double best = -1;
                foreach (var m in p.Blocks)
                {
                    if (mine.GetOr(m, 0) >= p.Cap) continue;
                    // In the close-down a window was run on into (evening Cleaning after
                    // a 21:00 close), work is levelled in only up to the people who must
                    // be on (owner decisions 2 and 3): the second person kept in gets
                    // the evening Cleaning, and nobody is kept on past the close for
                    // work that could be done before it.
                    if (closeDown is { } cd && m >= cd.From && total.GetOr(m, 0) >= cd.Floor) continue;
                    if (best == -1)
                    {
                        best = m;
                        continue;
                    }
                    // Ties: normal-rate time before premium time (before 07:00, from
                    // 18:00) - Stage 1's own tie-break, which levelling had none of: it
                    // moved Load Running before 07:00 on the review's sample, 2,555 ->
                    // 3,046 quarter-hours (owner decision R16; review RC-40). Then the way
                    // the group's Planning Order points, else toward the middle of the day.
                    var t = total.GetOr(m, 0);
                    var bt = total.GetOr(best, 0);
                    var pm = PremiumHead(m);
                    var pb = PremiumHead(best);
                    var nearer = way == "late" ? m > best : way == "early" ? m < best : Math.Abs(m - dayMid) < Math.Abs(best - dayMid) - 1e-9;
                    if (t < bt || (t == bt && (pm < pb || (pm == pb && nearer)))) best = m;
                }
                // Never reached: a capped pool holds no more units than its blocks, and
                // every other pool has its whole window. Work is never dropped.
                if (best == -1)
                {
                    if (p.Blocks.Count == 0) continue; // (the TypeScript filed it under `undefined`, off the day)
                    best = p.Blocks[0];
                }
                mine.Set(best, mine.GetOr(best, 0) + 1);
                total.Set(best, total.GetOr(best, 0) + 1);
                var e = placed.Get(best) ?? new JsMap<string, double>();
                e.Set(p.Group, e.GetOr(p.Group, 0) + 1);
                placed.Set(best, e);
            }
        }

        // Rebuild the demand map from pinned + levelled units.
        demand.Clear();
        foreach (var m in span)
        {
            var groups = pinnedGroups.Get(m)?.Copy() ?? new JsMap<string, double>();
            if (placed.TryGet(m, out var pl))
                foreach (var (g, n) in pl)
                    if (Js.Truthy(n)) groups.Set(g, groups.GetOr(g, 0) + n);
            double gsum = 0;
            foreach (var n in groups.Values()) gsum += n;
            var t = gsum + undirected.GetOr(m, 0);
            if (t > 0) demand.Set(m, new DemandBlock(m, t, groups));
        }
    }

    // ─── Pre-open compression (safety floor, floating start) ─────────────────────
    // If any pre-open block would have fewer than 2 people, re-stack the pre-open
    // workload right-aligned against trading-open so ≥2 people start TOGETHER at a
    // later time and finish exactly at open. Total hours are preserved - the start
    // simply floats later (06:00 is a floor, never a fixed start).
    //
    // Only work that is free to move, and only inside its own window. Re-stacking
    // everything moved the Monday date checks past 08:30 and started the load
    // before the time its rule sets (Roster Engine Review 07/11/2026, RC3). A
    // pre-open holding pinned work - Stage 1's named tasks, each with its own
    // deadline - is left as placed, and so is one whose re-stack would put any
    // unit outside its group's window; the head escort (step 3c) then brings a
    // second person in instead.
    private static void CompressPreOpen(
        JsMap<double, DemandBlock> demand,
        double tradingOpen,
        Func<double, IReadOnlyList<string>?>? allowedAt,
        IReadOnlyList<string> pinned,
        IReadOnlyList<QuotedWindow> quoted,
        RosteringRules r)
    {
        var seg = Js.Sorted(demand.Values().Where(d => d.Time < tradingOpen && d.Total > 0), (a, b) => a.Time - b.Time);
        if (seg.Count == 0) return;
        var minConc = Js.Min(seg.Select(d => d.Total));
        if (minConc >= r.MinPeopleOnFloor) return; // already ≥2 throughout - leave as-is
        var pinnedSet = new JsSet<string>(pinned);
        if (seg.Any(d => d.Groups.Any(kv => kv.Value > 0 && pinnedSet.Has(kv.Key)))) return;
        // A quoted window's hours are held to that window too, and the re-stack
        // deals units out wherever the compressed footprint falls.
        if (seg.Any(d => quoted.Any(w => d.Time >= w.Start && d.Time < w.End && d.GroupCount(w.Group) > 0))) return;

        double h = 0; // body-blocks of pre-open work
        foreach (var d in seg) h += d.Total;
        var maxConc = Js.Max(seg.Select(d => d.Total));
        var rows = Math.Max(r.MinPeopleOnFloor, maxConc);
        var width = Math.Ceiling(h / rows);
        var newStart = tradingOpen - width * B;

        // Pool the segment's group units (plus undirected remainder), then deal them
        // back out over the compressed footprint - into `dealt` first, so the
        // re-stack can be checked before it replaces anything.
        var pool = new JsMap<string, double>();
        double undirected = 0;
        foreach (var d in seg)
        {
            double gsum = 0;
            foreach (var (g, n) in d.Groups)
            {
                pool.Set(g, pool.GetOr(g, 0) + n);
                gsum += n;
            }
            undirected += Math.Max(0, d.Total - gsum);
        }
        var dealt = new List<DemandBlock>();

        // Row lengths: distribute H as evenly as possible, right-aligned at open.
        var baseLen = Math.Floor(h / rows);
        var extra = h - baseLen * rows;
        var rowCount = double.IsNaN(rows) ? 0 : (int)Math.Max(0, Math.Floor(rows)); // Array.from({ length: rows })
        var rowLen = new List<double>();
        for (var i = 0; i < rowCount; i++) rowLen.Add(baseLen + (i < extra ? 1 : 0));

        for (var j = 0; j < width; j++)
        {
            var time = newStart + j * B;
            var fromEnd = width - j; // blocks between this one and trading-open
            double total = rowLen.Count(len => len >= fromEnd);
            if (total == 0) continue;
            var groups = new JsMap<string, double>();
            double assigned = 0;
            while (assigned < total)
            {
                // Take from the largest remaining pool group; fall back to undirected.
                string? bestG = null;
                foreach (var (g, n) in pool)
                    if (n > 0 && (bestG is null || pool.GetOr(bestG, 0) < n))
                        bestG = g;
                if (!string.IsNullOrEmpty(bestG))
                {
                    pool.Set(bestG, pool.GetOr(bestG, 0) - 1);
                    groups.Set(bestG, groups.GetOr(bestG, 0) + 1);
                    assigned++;
                }
                else if (undirected > 0)
                {
                    undirected--;
                    assigned++; // undirected units raise total without a group entry
                }
                else
                {
                    total = assigned; // pool exhausted (rounding) - shrink to what exists
                    break;
                }
            }
            if (total > 0) dealt.Add(new DemandBlock(time, total, groups));
        }
        if (allowedAt is not null)
        {
            foreach (var d in dealt)
            {
                var ok = allowedAt(d.Time) ?? [];
                if (d.Groups.Any(kv => kv.Value > 0 && !ok.Contains(kv.Key))) return;
            }
        }
        foreach (var d in seg) demand.Delete(d.Time);
        foreach (var d in dealt) demand.Set(d.Time, d);
    }

    // ─── Manager pair: opener + closer with mandatory overlap ────────────────────
    // The pair is cut on COVER, not length. Where the opener hands over to the
    // closer is the hour two managers are on; it goes where the day has the most
    // work for them, then as evenly as the span allows. It used to be cut to a
    // hand-typed list of "efficient" work lengths (9.5, 9.25, 9, 6 and 5.75h)
    // chosen before any assistant's shift existed - shift length deciding the
    // build, when the owner asked for it to be a final, optional step - and on
    // G999 it bought nothing (Roster Engine Review 07/11/2026, P5, A04-4). The
    // cost of each length is judged once, last, against the finished day.
    private static ((double Start, double End) Opener, (double Start, double End) Closer) ShapeManagers(
        double spanStart,
        double spanEnd,
        ShaperRule rule,
        List<string> flags,
        Func<double, double> demandAt,
        // Quarter-hours that need BOTH managers: Admin two deep, which Stage 1
        // plans only for the handover (owner decision 7).
        Func<double, double> pairAt,
        RosteringRules r)
    {
        var S = spanEnd - spanStart;
        // Cut WORK, not clock: step 3b adds each slot's break allowance afterwards,
        // so a pair cut at the full clock span would land over the cap once the
        // break is added, and the roster then can't legally fill it. The cap is on
        // PAID hours, so MaxWorkMinutes does the conversion - 9 paid hours is a 9.5h
        // clock shift carrying a flat 30-min unpaid break, i.e. 9h of manager work.
        var type = rule.BreakType ?? BreakTypes.Flat30Unpaid;
        // Manager shifts are at least six hours on the clock (owner decision R7) -
        // as work, the clock less its break.
        var minClock = Js.Max(r.ManagerMinShiftMin, ToBlocks(rule.MinShiftHours) * B, 180);
        var minL = minClock - Breaks.BreakForShift(type, minClock / 60).TotalMinutes;
        var maxL = Math.Max(minL, Breaks.MaxWorkMinutes(type, rule.MaxShiftHours));

        // A day too long for two max-length shifts + minimum overlap: cover the
        // edges, flag the mid-day manager gap (flag-don't-force).
        if (S + r.OverlapMinMin > 2 * maxL)
        {
            flags.Add($"Manager gap {Fmt(spanStart + maxL)}–{Fmt(spanEnd - maxL)} - the day is longer than two max-length manager shifts can cover");
            return ((spanStart, spanStart + maxL), (spanEnd - maxL, spanEnd));
        }

        // A tiny day where even two minimum shifts overlap more than the span: both
        // managers take a min-length shift pinned to the edges (safety floor wins).
        if (S <= minL)
        {
            var end = Math.Min(spanStart + minL, spanEnd + (minL - S));
            flags.Add("Very short day - both manager shifts cover the whole span");
            return ((spanStart, end), (spanStart, end));
        }

        // Choose opener/closer lengths: smallest overlap ≥60min that admits a legal
        // pair - with no upper limit (owner decision R7) - then the handover over
        // the quarter-hours that need both managers on Admin, then where there is
        // most work for two, then balance.
        // Most Admin-for-two covered first (a longer handover if that is what it
        // takes), then the smallest overlap, then work for two, then balance.
        (double La, double Lc, double[] Score)? best = null;
        static bool Better(double[] a, double[] b)
        {
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return a[i] < b[i];
            return false;
        }
        for (var ov = r.OverlapMinMin; ov <= S; ov += B)
        {
            var T = S + ov;
            for (var la = minL; la <= Math.Min(maxL, S); la += B)
            {
                var lc = T - la;
                if (lc < minL || lc > maxL || lc > S) continue;
                double work = 0;
                double pair = 0;
                for (var m = spanEnd - lc; m < spanStart + la; m += B)
                {
                    work += demandAt(m);
                    pair += pairAt(m);
                }
                double[] score = [-pair, ov, -work, Math.Abs(la - lc)];
                if (best is null || Better(score, best.Value.Score)) best = (la, lc, score);
            }
        }
        if (best is null)
        {
            // Fallback (shouldn't happen): split at the midpoint with min overlap.
            var la = Math.Min(maxL, Math.Ceiling((S + r.OverlapMinMin) / 2 / B) * B);
            var lc = Math.Min(maxL, S + r.OverlapMinMin - la);
            best = (la, Math.Max(minL, lc), [9, 9]);
        }
        return ((spanStart, spanStart + best.Value.La), (spanEnd - best.Value.Lc, spanEnd));
    }

    /// <summary>A fixed shift as the shaper keeps it (trimmed to the staffed day); compared by reference.</summary>
    private sealed class Reserved(double start, double end, string role)
    {
        public double Start { get; } = start;
        public double End { get; } = end;
        public string Role { get; } = role;
    }

    /// <summary>A shift the staircase has opened and not yet closed; compared by reference.</summary>
    private sealed class OpenShift(double start)
    {
        public double Start { get; set; } = start;
    }

    private sealed record BreakWindow(string Group, double Start, double End, bool Early);

    private readonly record struct BreakSpan(double Lo, double Hi, double Seg);

    // ─── The shaper ──────────────────────────────────────────────────────────────
    public static ShapedDay ShapePlan(ShaperInput input)
    {
        var R = RosteringRules.Resolve(input.Rules);
        _rules = R;
        var flags = new List<string>();
        var demand = new JsMap<double, DemandBlock>();
        foreach (var d in input.DemandByBlock)
            if (d.Total > 0) demand.Set(d.Time, new DemandBlock(d.Time, d.Total, d.Groups.Copy()));
        if (demand.Size == 0) return new ShapedDay();

        double DemandTotal(double m) => demand.TryGet(m, out var d) ? d.Total : 0;
        double DemandGroup(double m, string g) => demand.TryGet(m, out var d) ? d.GroupCount(g) : 0;
        ShaperRule RuleOf(string role) => role == SlotRoles.MGR ? input.MgrRule : role == SlotRoles.JSA ? input.JsaRule : input.SaRule;
        string TypeOf(string role) => RuleOf(role).BreakType ?? (role == SlotRoles.MGR ? BreakTypes.Flat30Unpaid : BreakTypes.Tiered);
        var pinnedIn = input.PinnedGroups ?? [];
        var quotedIn = input.QuotedWindows ?? [];

        // 1. Level movable work across its windows so the curve cuts into long
        //    efficient shifts, then float the pre-open start later so ≥2 begin
        //    together (safety floor).
        LevelFlexibleWork(
            demand,
            input.PlaceAt ?? input.AllowedAt,
            pinnedIn,
            quotedIn,
            input.LevelLean,
            input.CloseDownFrom is { } cdf ? (cdf, R.MinPeopleOnFloor) : null);
        CompressPreOpen(demand, input.TradingOpen, input.PlaceAt ?? input.AllowedAt, pinnedIn, quotedIn, R);

        var times = Js.Sorted(demand.Keys(), (a, b) => a - b);

        // Fixed rosters (owner decision R6; review RC-18). The person's set hours
        // are kept, and cover is brought in around them: the day's span includes
        // them, so nobody works a fixed shift alone or without a manager. A fixed
        // shift is trimmed only where it breaks a shift rule itself - past the
        // close, over the maximum - and the store is told, to agree it with the
        // person; one under three hours is flagged, not stretched.
        var reserved = new List<Reserved>();
        foreach (var f in input.FixedShifts ?? [])
        {
            var rule = RuleOf(f.Role);
            var type = rule.BreakType ?? (f.Role == SlotRoles.MGR ? BreakTypes.Flat30Unpaid : BreakTypes.Tiered);
            var max = Breaks.MaxClockMinutes(type, rule.MaxShiftHours);
            var start = Math.Max(f.Start, input.StaffOpen);
            var end = Math.Min(f.End, input.StaffClose);
            var why = new List<string>();
            if (f.End > input.StaffClose) why.Add("it ran past the close");
            if (end - start > max)
            {
                end = start + max;
                why.Add($"it was longer than the {Js.ToFixed(max / 60, 2)}-hour maximum");
            }
            if (why.Count > 0)
                flags.Add($"Fixed shift {Fmt(f.Start)}–{Fmt(f.End)} trimmed to {Fmt(start)}–{Fmt(end)}: {string.Join(" and ", why)} - agree it with the person");
            if (end - start >= B && end - start < 180)
                flags.Add($"Fixed shift {Fmt(start)}–{Fmt(end)} is under the 3-hour minimum - agree it with the person");
            if (end - start >= B) reserved.Add(new Reserved(start, end, f.Role));
        }
        var spanStart = Js.Min([times[0], .. reserved.Select(f => f.Start)]);
        var spanEnd = Js.Max([times[^1] + B, .. reserved.Select(f => f.End)]);

        // 2. Exactly two manager slots: opener + closer with mandatory overlap.
        var (opener, closer) = ShapeManagers(
            spanStart,
            spanEnd,
            input.MgrRule,
            flags,
            DemandTotal,
            m => Math.Max(0, DemandGroup(m, TaskGroups.Admin) - 1),
            R);
        // A fixed manager who opens or closes the day stands in for that half of
        // the pair (R6); the other half is cut to hand over to them.
        var fixedMgrs = reserved.Where(f => f.Role == SlotRoles.MGR).ToList();
        var fixedOpener = fixedMgrs.FirstOrDefault(f => f.Start <= spanStart + B);
        var fixedCloser = fixedMgrs.FirstOrDefault(f => !ReferenceEquals(f, fixedOpener) && f.End >= spanEnd - B);
        if (fixedOpener is not null) opener = (fixedOpener.Start, fixedOpener.End);
        if (fixedCloser is not null) closer = (fixedCloser.Start, fixedCloser.End);
        if (fixedOpener is not null && fixedCloser is null)
            closer = (Math.Min(closer.Start, Math.Max(spanStart, fixedOpener.End - R.OverlapMinMin)), spanEnd);
        if (fixedCloser is not null && fixedOpener is null)
            opener = (spanStart, Math.Max(opener.End, Math.Min(spanEnd, fixedCloser.Start + R.OverlapMinMin)));
        var slots = new List<PlanSlot>();
        if (fixedOpener is null) slots.Add(new PlanSlot("mgr-open", SlotRoles.MGR, opener.Start, opener.End));
        if (fixedCloser is null) slots.Add(new PlanSlot("mgr-close", SlotRoles.MGR, closer.Start, closer.End));
        // The pair slots cut here (a fixed half is counted with the fixed shifts).
        double MgrCoverAt(double m) =>
            (fixedOpener is null && opener.Start <= m && m < opener.End ? 1 : 0) +
            (fixedCloser is null && closer.Start <= m && m < closer.End ? 1 : 0);

        // Handover placement check: the overlap should sit where demand already
        // funds the second body (it always does when the ≥2-floor holds).
        for (var m = Math.Max(opener.Start, closer.Start); m < Math.Min(opener.End, closer.End); m += B)
        {
            if (DemandTotal(m) < 2)
            {
                flags.Add($"Manager handover {Fmt(Math.Max(opener.Start, closer.Start))}–{Fmt(Math.Min(opener.End, closer.End))} sits where demand is below 2 - over-covers slightly");
                break;
            }
        }

        // 3. Residual demand (after the managers absorb one body each) cut into SA
        //    slots with a STAIRCASE sweep: a shift OPENS at each step-up in the
        //    curve and runs FORWARD into whatever work comes later; when the curve
        //    steps down, the shifts that close are the ones already at an efficient
        //    length (peak-hour helpers flow on into the rest of the day instead of
        //    stranding short orphan shifts - the interview's "a 7-10 and a 10-1 are
        //    one 7-1 shift"). A shift under 3h can never close, so it simply keeps
        //    working the next demand - over-cover only happens when a shift is
        //    genuinely too young to end, and it is measured + flagged.
        // 3a. Reserve the day's fixed-roster shifts BEFORE cutting the rest. They
        //     are commitments, not preferences: the person works them whatever the
        //     plan says, so the honest plan is one that shows them and spends the
        //     remaining demand around them. Their coverage is subtracted from the
        //     residual exactly like a manager's.
        //     Clipped to STAFF hours, not to the demand span: the shift is worked
        //     whether or not the benchmark asked for cover at that minute, and
        //     trimming it to the curve would hide hours the store is committed to
        //     paying. Any part of it outside the demand span simply reads as
        //     over-cover, which is the truth.
        var fixedN = 0;
        foreach (var f in reserved) slots.Add(new PlanSlot($"fixed-{++fixedN}", f.Role, f.Start, f.End));
        // A fixed junior counts only toward work a junior may do (R6): not the
        // tills, not a manager's Admin.
        double FixedCoverAt(double m)
        {
            double c = 0;
            foreach (var f in reserved)
            {
                if (!(f.Start <= m && m < f.End)) continue;
                if (f.Role != SlotRoles.JSA)
                {
                    c += 1;
                    continue;
                }
                var juniorWork = demand.TryGet(m, out var d) ? d.Total - d.GroupCount(TaskGroups.Checkout) - d.GroupCount(TaskGroups.Admin) : 0;
                c += juniorWork > 0 ? 1 : 0;
            }
            return c;
        }
        // A reserved shift's times are contractual. No later pass may reshape it:
        // not the break extension, not the tail escort, not the consolidator, and
        // not the shave - the person works exactly those hours, so trimming the slot
        // would only hide capacity the store is already committed to paying for.
        // Any of it above the curve is honest over-cover and is flagged as such.
        static bool IsFixed(PlanSlot s) => s.Id.StartsWith("fixed-", StringComparison.Ordinal);

        var residual = new JsMap<double, double>();
        foreach (var (m, d) in demand) residual.Set(m, Math.Max(0, d.Total - MgrCoverAt(m) - FixedCoverAt(m)));

        var minBlocks = Math.Max(12, ToBlocks(input.SaRule.MinShiftHours)); // ≥3h always
        // The cutter cuts WORK; the break-allowance extension (3b) then grows each
        // slot's clock to its full span. The rule's maximum is PAID hours, so a
        // Store Assistant capped at 9 paid hours occupies 9.5h of clock and delivers
        // 8.5h of tasks (60-min entitlement, 30 of it unpaid).
        var saType = input.SaRule.BreakType ?? BreakTypes.Tiered;
        var maxBlocks = Math.Floor(Breaks.MaxClockMinutes(saType, input.SaRule.MaxShiftHours) / B);
        var workMaxMin = Breaks.MaxWorkMinutes(saType, input.SaRule.MaxShiftHours);
        var saN = 0;

        var open = new List<OpenShift>();
        void CloseShift(OpenShift o, double at)
        {
            slots.Add(new PlanSlot($"sa-{++saN}", SlotRoles.SA, o.Start, at));
            open.RemoveAt(open.IndexOf(o));
        }
        for (var m = spanStart; m <= spanEnd; m += B)
        {
            var need = m < spanEnd ? residual.GetOr(m, 0) : 0;

            // Force-split anyone at max WORK length; reopen immediately if the curve
            // still needs the body (a split point, not a departure).
            foreach (var o in open.ToList())
            {
                if (m - o.Start >= workMaxMin)
                {
                    CloseShift(o, m);
                    if (open.Count < need) open.Add(new OpenShift(m));
                }
            }

            // BRIDGE short dips: when demand returns to the current level after a dip
            // but only holds it for LESS than a legal 3h shift, a close-then-reopen
            // spawns a doomed-short shift that must be padded - holding the current
            // crew through the dip is cheaper whenever the dip's over-cover is smaller
            // than that padding. (A dip followed by a long-lasting rise pads nothing,
            // so normal closes still happen.)
            bool BridgeCheaper()
            {
                double level = open.Count;
                double bridgeMin = 0;
                for (var m2 = m; m2 < Math.Min(m + minBlocks * B, spanEnd); m2 += B)
                {
                    var rr = residual.GetOr(m2, 0);
                    if (rr >= level)
                    {
                        var runEnd = m2;
                        while (runEnd < spanEnd && residual.GetOr(runEnd, 0) >= level) runEnd += B;
                        var reopenPad = Math.Max(0, minBlocks * B - (runEnd - m2));
                        return reopenPad > 0 && bridgeMin <= reopenPad;
                    }
                    bridgeMin += (level - rr) * B;
                }
                return false;
            }

            // Step-down: close the surplus shifts - eligible (≥3h) ones, longest-
            // running first. (It used to pick the "best length" to close, from a
            // ranking of clock lengths applied to cuts that are WORK lengths - the
            // break is added later - and so ranked the wrong length: A04-4.)
            // Ineligible young shifts stay open and over-cover (counted below).
            while (open.Count > need)
            {
                if (BridgeCheaper()) break;
                var eligible = Js.Sorted(open.Where(o => m - o.Start >= minBlocks * B), (a, b) => a.Start - b.Start);
                if (eligible.Count == 0) break;
                CloseShift(eligible[0], m);
            }
            if (m >= spanEnd) break;

            // Step-up: open shifts to meet the curve.
            while (open.Count < need) open.Add(new OpenShift(m));
        }
        // Day end: anything still open closes at spanEnd.
        //
        // A shift that opened too late to make a legal three hours is the awkward
        // case, and padding it BACKWARD - which is what this used to do - is the
        // worst answer available. The curve stepped up at 17:15 because the evening
        // wants a second body from 17:15; dragging that shift's start back to 16:15
        // buys an hour nothing asked for, and the painter has nothing to put in it.
        // On G999 that single line produced four blank tiles at the head of a
        // three-hour evening shift, twice over on some days.
        //
        // The plan is not tied to particular shifts, so ask the other question
        // first: can somebody simply stay on and do the work? Stretch a shift, else
        // add one (owner decision 15) - and the shift stretched must be one that
        // would otherwise have LEFT by the time the orphan starts. Stretching one
        // still on (it used to take any shift finishing after the orphan's start)
        // deleted the orphan's body where both were needed: G134's Thursday had
        // three bodies needed 18:15-19:00 and two rostered (review RC-23).
        foreach (var o in open.ToList())
        {
            if (spanEnd - o.Start < minBlocks * B)
            {
                var host = Js.Sorted(
                    slots.Where(h =>
                    {
                        // (no break-cover slots exist yet - 3b-bis runs after this.)
                        if (IsFixed(h) || h.Role == SlotRoles.MGR) return false;
                        if (h.End >= spanEnd || h.End > o.Start) return false;
                        var rule = h.Role == SlotRoles.JSA ? input.JsaRule : input.SaRule;
                        var type = rule.BreakType ?? BreakTypes.Tiered;
                        return spanEnd - h.Start <= Breaks.MaxClockMinutes(type, rule.MaxShiftHours);
                    }),
                    // The latest finisher needs the smallest extension.
                    (a, b) => b.End - a.End).FirstOrDefault();
                if (host is not null)
                {
                    host.End = spanEnd;
                    open.RemoveAt(open.IndexOf(o));
                    continue;
                }
                o.Start = Math.Max(spanStart, spanEnd - minBlocks * B);
            }
            CloseShift(o, spanEnd);
        }
        // 3b. Break allowance: a break is time OFF the floor, so a shift's clock
        //     length must be its work PLUS its break or the day's tiles come up
        //     short of the benchmark (the Benchmark table carries a separate Breaks
        //     row for exactly this). Extend every slot by its break entitlement -
        //     SA/JSA slots grow at the end (spilling to the start when the staffed
        //     close is in the way), the managers grow toward each other (opener's
        //     end / closer's start), widening the handover rather than moving open
        //     or close. Fixpoint on the tier: an extension that bumps the shift into
        //     a higher break tier extends again until stable. The break itself is
        //     placed MID-SHIFT by the painter (award timing) - the extension just
        //     provides the slack that keeps the workload whole.
        //
        //     But only INTO WORK. The allowance is slack to absorb a break, and it
        //     only does that where another body is still wanted; past that point it
        //     is paid time with nothing in it. The tail used to run to the end of
        //     the day's demand span, so three 06:00-10:00 load shifts on G248's
        //     Monday were grown to 10:30 - straight past 10:00, where the load
        //     window shuts and the curve collapses from seven bodies to three. The
        //     day bought ninety minutes of idle time at the exact moment it had
        //     least use for it, and the painter was left holding a shift with a hole
        //     in the middle of it. Worse, the fixpoint made it self-feeding: a 4.00h
        //     shift takes a 15-minute break, the extension makes it 4.25h, and 4.25h
        //     is over the award's four-hour tier, so the entitlement doubles to 30
        //     and the shift grows again.
        double CoverAt(double m)
        {
            double c = 0;
            foreach (var x in slots)
                if (x.Start <= m && m < x.End) c++;
            return c;
        }
        // How far past `from` this slot is still wanted: while the blocks ahead are
        // short of cover, growing into them is real; the first block that is already
        // covered ends it.
        // "Wanted" is deliberately generous: a block still counts while cover has
        // only just MET the demand, and stops at the first block that is already
        // over-covered. The point is not to squeeze the day to the curve - the shave
        // below does that - it is to refuse the one thing the allowance must never
        // buy, another quarter hour in a stretch that already has more bodies than
        // work. On G248's Monday that stretch is 10:00, where the load window shuts
        // and the curve drops from seven bodies to three in a single block.
        double WantedTo(double from)
        {
            var m = from;
            while (m < spanEnd && CoverAt(m) <= DemandTotal(m)) m += B;
            return m;
        }
        double WantedFrom(double to)
        {
            var m = to;
            while (m - B >= spanStart && CoverAt(m - B) <= DemandTotal(m - B)) m -= B;
            return m;
        }
        foreach (var s in slots)
        {
            if (IsFixed(s)) continue; // contractual span - already includes its break
            var rule = RuleOf(s.Role);
            var type = TypeOf(s.Role);
            var maxMin = Breaks.MaxClockMinutes(type, rule.MaxShiftHours);
            double ext = 0;
            for (var i = 0; i < 4; i++)
            {
                var next = Breaks.BreakForShift(type, (s.End - s.Start + ext) / 60).TotalMinutes;
                if (next == ext) break;
                ext = next;
            }
            ext = Math.Min(ext, Math.Max(0, maxMin - (s.End - s.Start)));
            ext = Math.Floor(ext / B) * B;
            if (ext <= 0) continue;
            if (s.Role == SlotRoles.MGR)
            {
                // The managers are the exception: their allowance grows them TOWARD
                // each other, widening the mandatory handover rather than the day, so
                // it is never bought outside the work - the overlap IS the work.
                if (s.Id == "mgr-open") s.End += ext;
                else s.Start = Math.Max(spanStart, s.Start - ext);
            }
            else
            {
                var tail = Math.Min(ext, WantedTo(s.End) - s.End);
                s.End += tail;
                s.Start = Math.Max(WantedFrom(s.Start), s.Start - (ext - tail));
            }
        }

        // 3b-bis. Break cover where the work cannot wait ─────────────────────────
        // 3b gives every slot its break entitlement by lengthening its clock, which
        // is enough whenever the displaced work can just be done a little later. In
        // a window that CLOSES early it is not: the load has to be off the floor
        // before the store is properly busy, so a break taken between 06:00 and
        // 10:00 is work that never gets done - and lengthening a 06:00-10:30 shift
        // to 10:45 buys nothing, because the extra quarter hour is outside the
        // window the load lives in.
        //
        // The award compounds it. No break inside the first hour, none inside the
        // last, and the first segment of a long shift falls in its first HALF - so a
        // crew that all starts at 06:00 all breaks before 10:00, in the one window
        // of the day with no slack in it. On G248's Monday five shifts owed ten
        // blocks of break inside the load window against zero spare minutes, and
        // Load Running came out 1.25h under its benchmark every week.
        //
        // So measure it rather than assume it: a window absorbs as many break
        // blocks as its own slack covers. While the breaks FORCED inside outrun
        // that slack, add a body - which is exactly what a store does when the load
        // has to be run and the crew still has to eat. The addition is pinned, so
        // the shave below cannot quietly take it back.
        //
        // Every group's window is checked, not just the morning's. A break in the
        // evening displaces specials or cleaning that "has the rest of the evening
        // to be made up in" only if the evening has a spare body in it, and when
        // the day's capacity was shaved to its total the evening often had none -
        // four closers on four people's work, each breaking in turn, and the work
        // their breaks displaced simply lost. Only a window that closes early - the
        // morning load, with nothing after it - gets a whole new body if stretching
        // cannot help ("add a body if necessary for load running", U2). Elsewhere a
        // body is three hours to cover a quarter of one, so the window is
        // stretched where a shift can reach it, and what is still lost is flagged
        // from the painted result. Tills have their own check (3b-ter), and admin
        // is a manager's work that an assistant's slack cannot absorb.
        var midTrading = (input.TradingOpen + input.TradingClose) / 2;
        var breakWindows = new List<BreakWindow>();
        {
            var firstAt = new JsMap<string, double>();
            var lastAt = new JsMap<string, double>();
            foreach (var (m, d) in demand)
            {
                foreach (var (g, n) in d.Groups)
                {
                    if (n <= 0) continue;
                    var allowed = input.AllowedAt?.Invoke(m);
                    if (allowed is not null && allowed.Count > 0 && !allowed.Contains(g)) continue;
                    firstAt.Set(g, Math.Min(firstAt.GetOr(g, double.PositiveInfinity), m));
                    lastAt.Set(g, Math.Max(lastAt.GetOr(g, -1), m));
                }
            }
            foreach (var (g, last) in lastAt)
            {
                if (g == TaskGroups.Checkout || g == TaskGroups.Admin) continue;
                breakWindows.Add(new BreakWindow(g, firstAt.Get(g), last + B, last + B < midTrading));
            }
        }

        // Break blocks a slot CANNOT take outside `w`: the segment's whole legal
        // span sits inside it. Mirrors the painter's award timing - first/last hour
        // excluded, and a two-segment entitlement takes its first segment in the
        // shift's first half - closely enough to answer "can this break escape the
        // window?", which is all this needs to know.
        // Each break segment's legal span, the way the painter will place it -
        // first/last hour excluded, a two-segment entitlement split at the middle,
        // a single one by the fifth hour.
        List<BreakSpan> BreakSpans(PlanSlot sl)
        {
            var len = sl.End - sl.Start;
            var brk = Breaks.BreakForShift(TypeOf(sl.Role), len / 60);
            if (brk.TotalMinutes <= 0) return [];
            var seg = Js.Round(brk.TotalMinutes / brk.Segments / B) * B;
            if (seg <= 0 || seg * brk.Segments >= len) return [];
            double edge = 60;
            var needed = seg * brk.Segments + (brk.Segments > 1 ? 60 : 0);
            while (edge > B && len - 2 * edge < needed) edge -= B;
            var lo = sl.Start + edge;
            var hi = sl.End - edge;
            if (hi - lo < seg) return [];
            if (brk.Segments == 1) return [new BreakSpan(lo, Math.Max(Math.Min(hi, sl.Start + 5 * 60 + seg), lo + seg), seg)];
            var mid = sl.Start + Math.Floor(len / 2 / B) * B;
            return
            [
                new BreakSpan(lo, Math.Max(Math.Min(mid, hi - seg), lo + seg), seg),
                new BreakSpan(Math.Min(mid, hi - seg), hi, seg),
            ];
        }

        // Break blocks a slot will take inside `w`: every segment whose span
        // reaches into the window and that has no position wholly outside it with
        // a body to spare. Escaping the window is not enough on its own - a break
        // that could legally wait until after 10:00 still lands in the load when
        // everyone after 10:00 is on a till, which is how a morning planned with
        // break cover for three blocks lost nine.
        double ForcedBreakBlocks(PlanSlot s, BreakWindow w)
        {
            double n = 0;
            foreach (var sp in BreakSpans(s))
            {
                if (sp.Lo >= w.End || sp.Hi <= w.Start) continue;
                var escapes = false;
                for (var c = sp.Lo; c + sp.Seg <= sp.Hi && !escapes; c += B)
                {
                    if (c < w.End && c + sp.Seg > w.Start) continue;
                    var spare = true;
                    for (var m = c; m < c + sp.Seg && spare; m += B)
                        if (CoverAt(m) - DemandTotal(m) < 1) spare = false;
                    escapes = spare;
                }
                if (!escapes) n += sp.Seg / B;
            }
            return n;
        }
        double ForcedIn(BreakWindow w)
        {
            double n = 0;
            foreach (var s in slots.Where(s => s.Start < w.End && s.End > w.Start).ToList()) n += ForcedBreakBlocks(s, w);
            return n;
        }

        double MinBreakSlack(string group) => input.MinBreakSlack?.GetOr(group, 0) ?? 0;

        var addedCover = 0;
        double coverGrown = 0;
        var addedBodies = new List<string>();
        // Runs twice: here, and again once the length passes have moved slot
        // edges (they can shave off exactly the slack this bought).
        // How far past the morning load's window its work may be made up: the
        // minutes Stage 1 allows Load Running after the window for the crew's
        // staggered breaks (owner decision 4).
        double StretchEnd(BreakWindow w)
        {
            if (!w.Early) return w.End;
            var e = w.End;
            while (e < spanEnd && (input.AllowedAt?.Invoke(e)?.Contains(w.Group) ?? false)) e += B;
            return e;
        }
        void CoverWindowBreaks()
        {
            foreach (var w in breakWindows)
            {
                var reach = StretchEnd(w);
                for (var guard = 0; guard < 40; guard++)
                {
                    var forced = ForcedIn(w);
                    var need = Math.Max(forced, MinBreakSlack(w.Group));
                    if (need == 0) break;
                    // Slack inside the window, and in the load's make-up minutes after
                    // it. Staying on makes up only what the breaks displace: the tills
                    // open as the load ends and take whoever is spare, so anything else the
                    // painter still lost (minBreakSlack above the breaks) needs room inside
                    // the window - counting the make-up minutes for it lost the load on 60
                    // of the sample's 264 days.
                    double inWindow = 0;
                    for (var m = w.Start; m < w.End; m += B) inWindow += Math.Max(0, CoverAt(m) - DemandTotal(m));
                    double after = 0;
                    for (var m = w.End; m < reach; m += B) after += Math.Max(0, CoverAt(m) - DemandTotal(m));
                    var absorbable = inWindow + Math.Min(after, forced);
                    if (absorbable >= need) break;
                    // STRETCH BEFORE YOU HIRE. What is needed here is slack - a body free
                    // at the minute someone else is on their break - and that is bought a
                    // block at a time. A whole extra shift is the last resort, not the
                    // first: the legal minimum is three hours, so a window owed one hour
                    // of break got a three-hour body and the other two hours were dead
                    // time the painter could not fill. On G999 that alone put seven holes
                    // in a Monday.
                    //
                    // So grow a shift that is already there, one block into the window,
                    // choosing the block that is shortest of cover. Only when nothing can
                    // reach does a new body get rostered.
                    (PlanSlot S, string Edge, double Need)? bestGrow = null;
                    foreach (var s in slots)
                    {
                        if (IsFixed(s)) continue;
                        if (s.End - s.Start + B > Breaks.MaxClockMinutes(TypeOf(s.Role), RuleOf(s.Role).MaxShiftHours)) continue;
                        // A block INSIDE the window that this slot could newly cover.
                        var cands = new List<(string Edge, double M)>();
                        if (s.Start - B >= Math.Max(spanStart, w.Start) && s.Start - B < w.End) cands.Add(("start", s.Start - B));
                        // The load's crew may stay on into its make-up minutes (decision 4).
                        if (s.End >= w.Start && s.End < Math.Min(reach, spanEnd)) cands.Add(("end", s.End));
                        foreach (var c in cands)
                        {
                            var cneed = DemandTotal(c.M) - CoverAt(c.M);
                            // A manager's hour is the dearest way to buy cover for floor work:
                            // a manager is stretched only when no assistant can reach.
                            var mgr = s.Role == SlotRoles.MGR;
                            var bestMgr = bestGrow?.S.Role == SlotRoles.MGR;
                            if (bestGrow is null || (bestMgr && !mgr) || (bestMgr == mgr && cneed > bestGrow.Value.Need))
                                bestGrow = (s, c.Edge, cneed);
                        }
                    }
                    if (bestGrow is { } bg)
                    {
                        if (bg.Edge == "start") bg.S.Start -= B;
                        else bg.S.End += B;
                        coverGrown += B;
                        continue;
                    }
                    // Nothing to stretch: every shift in the window is already at the
                    // edge of the day or at its award maximum. Outside the morning load
                    // that is where it stops - the painter flags what is still lost. In
                    // the load, the crew first breaks one at a time and stays on into the
                    // load's make-up minutes (the stretch above reaches into them): no
                    // extra three-hour body for what staying on can absorb (owner decision
                    // 4; review RC-13: on G548 a body bought for half an hour of trapped
                    // breaks carried 2.5 hours of nothing). Only when staying on cannot
                    // absorb it does a body come in, rather than the load being lost:
                    // with none at all the load went short on 89 of the sample's 264
                    // days, the tills taking the crew as the doors opened.
                    if (!w.Early) break;
                    // In the morning load window, add a body - "add a body
                    // if necessary for load running" (U2), and if necessary means if
                    // necessary (owner decision 6). This used to weigh the body's cost
                    // against the shortfall and, below half a shift, plan the load short
                    // instead; the store asked for the load to be run.
                    var len = minBlocks * B;
                    var start = Math.Max(spanStart, w.End - len);
                    if (w.End - start < minBlocks * B)
                    {
                        flags.Add(
                            $"{Js.ToFixed((forced - absorbable) * B / 60, 2)}h of break falls inside a window that closes at {Fmt(w.End)}, and the day is too short there to add a {Js.ToFixed(len / 60, 2)}h shift - that work will be planned short");
                        break;
                    }
                    slots.Add(new PlanSlot($"sa-cover-{++addedCover}", SlotRoles.SA, start, w.End));
                    addedBodies.Add(
                        $"Extra body {Fmt(start)}-{Fmt(w.End)} so the crew can take its breaks without losing the work - {Js.ToFixed(forced * B / 60, 2)}h of award break falls inside a window that closes at {Fmt(w.End)}");
                }
            }
        }
        CoverWindowBreaks();
        // 3b-ter. Break cover for the tills ───────────────────────────────────────
        // A till is manned in its own block or not at all, and everyone who can
        // work one has to break somewhere. Where the tills need every till-capable
        // body on, a break there leaves a register empty. The planned admin used to
        // hide this - hours above the benchmark kept an extra manager on - and once
        // admin is planned at its benchmark the tills came up one short in the
        // afternoons (Roster Engine Review 07/11/2026; owner decision 3).
        //
        // So check every till-capable slot's break the way the painter will place
        // it: if no legal position leaves a till body to spare, stretch the nearest
        // store assistant across the best one - they take the till while the other
        // breaks, which is work, not idle time - or failing that bring in a body.
        double TillsAt(double m) => DemandGroup(m, TaskGroups.Checkout);
        // Who can take a till at each quarter-hour: every adult on, less the
        // managers Stage 1 has on Admin then. The Admin is a manager's; a manager
        // counted as till cover took the register when an assistant broke and the
        // Admin was lost - a quarter of all lost Admin on the review's sample
        // (review RC-7).
        JsMap<double, double> TillCover()
        {
            var cover = new JsMap<double, double>();
            var mgrs = new JsMap<double, double>();
            foreach (var x in slots)
            {
                if (x.Role == SlotRoles.JSA) continue;
                for (var m = x.Start; m < x.End; m += B)
                {
                    cover.Set(m, cover.GetOr(m, 0) + 1);
                    if (x.Role == SlotRoles.MGR) mgrs.Set(m, mgrs.GetOr(m, 0) + 1);
                }
            }
            foreach (var (m, n) in cover) cover.Set(m, n - Math.Min(DemandGroup(m, TaskGroups.Admin), mgrs.GetOr(m, 0)));
            return cover;
        }
        // Quarter-hours whose tills outnumber the people who can take them.
        double TillShortBlocks()
        {
            var cover = TillCover();
            double n = 0;
            foreach (var (m, d) in demand)
                if (d.GroupCount(TaskGroups.Checkout) > cover.GetOr(m, 0)) n++;
            return n;
        }
        // The breaks with no legal position that leaves the tills manned: the
        // first one found, and how many there are.
        // `keys` collects which breaks are stuck (slot and segment), so a junior
        // label can be judged by WHICH breaks it strands, not how many: relabelling
        // an assistant whose own break was stuck took it off the count while
        // stranding a colleague's, the count held, and the closing manager took
        // the till with Fresh Ordering due (G006, 11/11).
        (double From, double To, double Count)? TillStuck(JsSet<string>? keys = null)
        {
            var cover = TillCover();
            (double From, double To)? first = null;
            double count = 0;
            foreach (var sl in slots)
            {
                if (sl.Role == SlotRoles.JSA) continue;
                var spans = BreakSpans(sl);
                for (var si = 0; si < spans.Count; si++)
                {
                    var sp = spans[si];
                    (double C, double Short)? best = null;
                    for (var c = sp.Lo; c + sp.Seg <= sp.Hi; c += B)
                    {
                        double shortN = 0;
                        for (var m = c; m < c + sp.Seg; m += B)
                            if (cover.GetOr(m, 0) - 1 < TillsAt(m)) shortN++;
                        if (best is null || shortN < best.Value.Short) best = (c, shortN);
                        if (shortN == 0) break;
                    }
                    if (best is { } b && b.Short > 0)
                    {
                        count++;
                        keys?.Add($"{sl.Id}:{si}");
                        first ??= (b.C, b.C + sp.Seg);
                    }
                }
            }
            return first is { } f ? (f.From, f.To, count) : null;
        }
        double tillCoverAdded = 0;
        // Runs twice, like CoverWindowBreaks.
        void CoverTillBreaks()
        {
            for (var guard = 0; guard < 40; guard++)
            {
                if (TillStuck() is not { } stuck) break;
                var (from, to, _) = stuck;
                (PlanSlot Sl, double Start, double End, double Grow)? pick = null;
                foreach (var sl in slots)
                {
                    if (sl.Role == SlotRoles.JSA || sl.Role == SlotRoles.MGR || IsFixed(sl)) continue;
                    if (sl.Start <= from && sl.End >= to) continue; // already there
                    if (sl.End < from || sl.Start > to) continue; // only a stretch that joins up
                    var start = Math.Min(sl.Start, from);
                    var end = Math.Max(sl.End, to);
                    if (end - start > Breaks.MaxClockMinutes(input.SaRule.BreakType ?? BreakTypes.Tiered, input.SaRule.MaxShiftHours)) continue;
                    var grow = end - start - (sl.End - sl.Start);
                    if (pick is null || grow < pick.Value.Grow) pick = (sl, start, end, grow);
                }
                if (pick is { } p)
                {
                    tillCoverAdded += p.Grow;
                    p.Sl.Start = p.Start;
                    p.Sl.End = p.End;
                }
                else
                {
                    var len = Math.Max(minBlocks * B, to - from);
                    var start = Math.Max(spanStart, Math.Min(from, spanEnd - len));
                    var end = Math.Min(spanEnd, start + len);
                    if (end - start < minBlocks * B) break;
                    slots.Add(new PlanSlot($"sa-cover-{++addedCover}", SlotRoles.SA, start, end));
                    tillCoverAdded += end - start;
                }
            }
        }
        CoverTillBreaks();

        // How short the day's break cover is: break blocks forced into an early
        // window beyond its slack, plus till breaks with nowhere legal to go. The
        // length passes below may move slot edges only where this does not grow -
        // the shave used to take back exactly the slack bought above, so G999's
        // 10/09 said "0.75h added so the crew can take its breaks without losing
        // the work" and still lost 0.75h of load (Roster Engine Review 07/11/2026,
        // A02-5).
        double WindowShortBy(BreakWindow w)
        {
            var forced = ForcedIn(w);
            // The load's make-up minutes count as in coverWindowBreaks - for the
            // breaks only: the shave trimmed the crew's stay-on straight back off,
            // seeing no harm in it.
            double inWindow = 0;
            for (var m = w.Start; m < w.End; m += B) inWindow += Math.Max(0, CoverAt(m) - DemandTotal(m));
            double after = 0;
            var reach = StretchEnd(w);
            for (var m = w.End; m < reach; m += B) after += Math.Max(0, CoverAt(m) - DemandTotal(m));
            return Math.Max(0, Math.Max(forced, MinBreakSlack(w.Group)) - inWindow - Math.Min(after, forced));
        }
        double BreakDeficit()
        {
            double n = 0;
            foreach (var w in breakWindows) n += WindowShortBy(w);
            return n + (TillStuck()?.Count ?? 0);
        }

        // Cover added for breaks is a commitment, not slack: the shave must not
        // reclaim it, or the next pass simply re-creates the shortfall it fixed.
        static bool IsCover(PlanSlot s) => s.Id.StartsWith("sa-cover-", StringComparison.Ordinal);

        // 3c. Tail escort (safety floor - interview: nobody locks up alone): if the
        //     final stretch of the day has fewer than 2 people planned, the latest-
        //     ending SA stays through to the staffed close with the closing manager.
        var tailFrom = spanEnd;
        while (tailFrom - B >= spanStart && CoverAt(tailFrom - B) < R.MinPeopleOnFloor) tailFrom -= B;
        if (tailFrom < spanEnd)
        {
            var escort = Js.Sorted(
                slots.Where(s =>
                    s.Role == SlotRoles.SA &&
                    !IsFixed(s) &&
                    !IsCover(s) &&
                    s.End <= tailFrom &&
                    spanEnd - s.Start <= maxBlocks * B),
                (a, b) => b.End - a.End).FirstOrDefault();
            if (escort is not null) escort.End = spanEnd;
        }
        // ...and nobody opens alone either. When the day starts with fewer than 2
        // people planned - a manager alone on the Monday date checks before the
        // load's start time, say - the earliest-starting SA comes in with them.
        // Moving the date checks later, or the load earlier, broke the owner's own
        // rules to keep this one; this keeps all three. The escort's first minutes
        // may hold no work - the painter keeps them as cover and flags them.
        var headTo = spanStart;
        while (headTo + B <= spanEnd && CoverAt(headTo) < R.MinPeopleOnFloor) headTo += B;
        if (headTo > spanStart)
        {
            var escort = Js.Sorted(
                slots.Where(s =>
                    s.Role == SlotRoles.SA &&
                    !IsFixed(s) &&
                    !IsCover(s) &&
                    s.Start >= headTo &&
                    s.End - spanStart <= maxBlocks * B),
                (a, b) => a.Start - b.Start).FirstOrDefault();
            if (escort is not null) escort.Start = spanStart;
        }
        // ...nor alone at any point in between. The canvas can hold one person's
        // work through the middle of the day - a manager's admin, say, once admin is
        // planned at its benchmark rather than above it - and cutting shifts to the
        // work then leaves one person on the floor. The lone-worker floor is a
        // safety rule, "always", so the stretch is covered: the nearest store
        // assistant's shift is stretched across it (within the award maximum), or
        // failing that a cover body of the legal minimum comes in. Its extra
        // minutes may hold no work; the painter keeps them as cover and flags them.
        double floorCover = 0;
        for (var guard = 0; guard < 24; guard++)
        {
            double from = -1;
            double to = -1;
            for (var m = spanStart; m < spanEnd; m += B)
            {
                var n = CoverAt(m);
                if (n > 0 && n < R.MinPeopleOnFloor)
                {
                    if (from < 0) from = m;
                    to = m + B;
                }
                else if (from >= 0) break;
            }
            if (from < 0) break;
            (PlanSlot S, double Start, double End, double Grow)? pick = null;
            foreach (var s in slots)
            {
                if (s.Role != SlotRoles.SA || IsFixed(s)) continue;
                var start = Math.Min(s.Start, from);
                var end = Math.Max(s.End, to);
                if (end - start > maxBlocks * B) continue;
                // Only a stretch that joins the shift to the lone stretch, never a jump.
                if (s.End < from && s.End != from) continue;
                if (s.Start > to && s.Start != to) continue;
                var grow = end - start - (s.End - s.Start);
                if (pick is null || grow < pick.Value.Grow) pick = (s, start, end, grow);
            }
            if (pick is { } p)
            {
                p.S.Start = p.Start;
                p.S.End = p.End;
            }
            else
            {
                var len = Math.Max(minBlocks * B, to - from);
                var start = Math.Max(spanStart, to - len);
                var end = Math.Min(spanEnd, start + len);
                start = Math.Max(spanStart, end - len);
                if (end - start < minBlocks * B) break;
                slots.Add(new PlanSlot($"sa-cover-{++addedCover}", SlotRoles.SA, start, end));
            }
            floorCover += to - from;
        }
        if (floorCover > 0)
            flags.Add($"{Js.ToFixed(floorCover / 60, 2)}h of shift added so nobody works alone - the plan has one person's work there");

        // 3d. Shave the fat: the staircase holds shifts through every sub-3h dip in
        //     the curve (a legal shift can't close and reopen around it), and on a
        //     rippling till curve those holds stack into hours of capacity above the
        //     workload - which the painter would have to fill with invented tiles,
        //     blowing the daily allowance. While total WORK capacity (clock minus
        //     each slot's break) exceeds the demanded workload, trim one block off a
        //     shift edge that sits on an over-covered block: never below the
        //     2-person floor, never under the block's demand, never below a legal
        //     3h shift, managers stay pinned to the day's open and close. The dips
        //     the shifts still straddle then hold breaks (the painter prefers
        //     surplus blocks) instead of invented work.
        double demandTotalSum = 0;
        foreach (var d in demand.Values()) demandTotalSum += d.Total;
        var demandTotalMin = demandTotalSum * B;
        double BreakMinFor(string role, double lenMin) => Breaks.BreakForShift(TypeOf(role), lenMin / 60).TotalMinutes;
        double WorkedMin()
        {
            double sum = 0;
            foreach (var s in slots) sum = sum + (s.End - s.Start) - BreakMinFor(s.Role, s.End - s.Start);
            return sum;
        }

        // 3d-bis. ...and put back what the day is actually short of.
        //
        // A slot delivers its clock MINUS its break, so a day rostered to exactly
        // its demand delivers less than its demand - every break is a block of work
        // that does not happen. The break allowance in 3b used to cover that by
        // lengthening shifts, but it grew them into whatever came next, which on a
        // morning-load day is the dead half hour after the load window shuts. Now
        // that the allowance only grows into work, the funding has to come from
        // somewhere honest instead: grow the day until its WORK capacity meets its
        // workload, one block at a time, at the block that is shortest of cover.
        //
        // This is the exact mirror of the shave below, and the two cannot fight:
        // one runs while capacity is short, the other while it is over.
        for (var guard = 0; guard < 400; guard++)
        {
            if (demandTotalMin - WorkedMin() < B) break;
            (PlanSlot S, string Edge, double Need, double Delta)? best = null;
            foreach (var s in slots)
            {
                // Managers are not a lever here: the pair is shaped deliberately - one
                // opener, one closer, a mandatory handover overlap and the efficient
                // lengths - and stretching one to fund a break would quietly undo that.
                if (IsFixed(s) || s.Role == SlotRoles.MGR) continue;
                var len = s.End - s.Start;
                var rule = s.Role == SlotRoles.JSA ? input.JsaRule : input.SaRule;
                var type = rule.BreakType ?? BreakTypes.Tiered;
                if (len + B > Breaks.MaxClockMinutes(type, rule.MaxShiftHours)) continue;
                foreach (var edge in Edges)
                {
                    var m = edge == "end" ? s.End : s.Start - B;
                    if (m < spanStart || m >= spanEnd) continue;
                    // Growing across a break tier costs more break than it adds work.
                    var delta = B - (BreakMinFor(s.Role, len + B) - BreakMinFor(s.Role, len));
                    if (delta <= 0) continue;
                    var need = DemandTotal(m) - CoverAt(m);
                    if (best is null || need > best.Value.Need || (need == best.Value.Need && delta > best.Value.Delta))
                        best = (s, edge, need, delta);
                }
            }
            if (best is not { } bb) break;
            if (bb.Edge == "end") bb.S.End += B;
            else bb.S.Start -= B;
        }

        // Admin is manager-only: a manager's edge is never trimmed off a
        // quarter-hour whose Admin needs every manager on - two deep inside the
        // handover (owner decision 7). Counting heads alone, the shave cut the
        // opener from 13:45 to 12:30 under Saturday's Fresh Ordering pair, and a
        // quarter-hour of the close-down was lost (G015 14/11).
        bool AdminNeedsMgr(PlanSlot s, double m)
        {
            if (s.Role != SlotRoles.MGR) return false;
            double others = 0;
            foreach (var o in slots)
                if (!ReferenceEquals(o, s) && o.Role == SlotRoles.MGR && o.Start <= m && m < o.End) others++;
            return DemandGroup(m, TaskGroups.Admin) > others;
        }
        bool AnotherManagerOn(PlanSlot s, double m) =>
            slots.Any(o => !ReferenceEquals(o, s) && o.Role == SlotRoles.MGR && o.Start <= m && m < o.End);
        // The handover is a rule (owner decision 5): no trim takes the managers'
        // overlap below the hour.
        bool CutsHandover(PlanSlot s)
        {
            var other = slots.FirstOrDefault(o => !ReferenceEquals(o, s) && o.Role == SlotRoles.MGR);
            return other is not null && Math.Min(s.End, other.End) - Math.Max(s.Start, other.Start) - B < R.OverlapMinMin;
        }
        // One quarter-hour shaved off an over-covered edge, if any can be without
        // costing the break cover bought above. `keep` is never shaved.
        bool ShaveOnce(double deficit, PlanSlot? keep = null)
        {
            var cands = new List<(PlanSlot S, string Edge, double Delta, double Badness)>();
            foreach (var s in slots)
            {
                if (ReferenceEquals(s, keep) || IsFixed(s) || IsCover(s)) continue;
                var len = s.End - s.Start;
                if (len - B < (s.Role == SlotRoles.MGR ? Math.Max(180, R.ManagerMinShiftMin) : 180)) continue;
                foreach (var edge in Edges)
                {
                    if (s.Role == SlotRoles.MGR && (s.Id == "mgr-open" ? edge == "start" : edge == "end")) continue;
                    // The handover is a rule (owner decision 5): trimming cuts assistants'
                    // shifts, never the managers' overlap below the hour. The shave used to
                    // eat it - 60 -> 120 -> 45 minutes on G001's Tuesday (review RC-14).
                    if (s.Role == SlotRoles.MGR && CutsHandover(s)) continue;
                    var m = edge == "end" ? s.End - B : s.Start;
                    var d = DemandTotal(m);
                    if (CoverAt(m) - 1 < Math.Max(d, R.MinPeopleOnFloor)) continue;
                    if (AdminNeedsMgr(s, m)) continue;
                    // A manager on every quarter-hour anyone is (interview): never shave
                    // the handover so far that the opener leaves before the closer arrives.
                    if (s.Role == SlotRoles.MGR && !AnotherManagerOn(s, m)) continue;
                    var delta = len - BreakMinFor(s.Role, len) - (len - B - BreakMinFor(s.Role, len - B));
                    if (delta <= 0) continue; // shaving across a break tier saves nothing
                    cands.Add((s, edge, delta, -(CoverAt(m) - Math.Max(d, R.MinPeopleOnFloor))));
                }
            }
            // Most work saved first, then where the floor is most over-covered (not
            // a length ranking - A04-4); never at the price of the break cover bought
            // above.
            Js.Sort(cands, (x, y) => (y.Delta - x.Delta).Or(x.Badness - y.Badness));
            foreach (var c in cands)
            {
                if (c.Edge == "end") c.S.End -= B;
                else c.S.Start += B;
                if (BreakDeficit() <= deficit) return true;
                if (c.Edge == "end") c.S.End += B;
                else c.S.Start -= B;
            }
            return false;
        }
        // When no edge can be shaved - the over-covered edge belongs to a shift
        // already at the 3-hour minimum, or to the cover bought for the crew's
        // breaks - a store assistant's shift can still MOVE: it comes in later (or
        // leaves earlier) by up to an hour and works on at its other end, where the
        // floor is short of cover or where a colleague can then be shaved instead.
        // The same work is covered, with less shift time standing idle. Typical
        // case: five on the morning load where four and a half are needed, the
        // opening manager idle beside them (G193 11/11: the manager blank five
        // times before 10:00) - one of the load runners starts at 08:15 and stays
        // to 11:15, and the assistant who would have started at 10:00 starts at
        // 11:00 (owner decision 3: work moved in so nobody stands idle; decision 5:
        // assistants' shifts are cut, never the managers'). The opening manager
        // starts with the load and the closing manager stays to the close; fixed
        // and break-cover shifts keep their place, as in the shave.
        double Shortfall()
        {
            double n = 0;
            foreach (var (m, d) in demand) n += Math.Max(0, d.Total - CoverAt(m));
            return n;
        }
        bool SlideShave(double deficit)
        {
            var worked0 = WorkedMin();
            var short0 = Shortfall();
            foreach (var s in slots)
            {
                if (IsFixed(s) || s.Role != SlotRoles.SA) continue;
                foreach (var dir in new[] { 1, -1 })
                {
                    var was0 = s.Start;
                    var was1 = s.End;
                    for (var k = 1; k <= 4; k++)
                    {
                        // The quarter-hour it gives up must be over-covered.
                        var off = dir > 0 ? was0 + (k - 1) * B : was1 - k * B;
                        if (CoverAt(off) - 1 < Math.Max(DemandTotal(off), R.MinPeopleOnFloor)) break;
                        s.Start = was0 + dir * k * B;
                        s.End = was1 + dir * k * B;
                        if (s.Start < spanStart || s.End > spanEnd) break;
                        if (BreakDeficit() <= deficit)
                        {
                            // Where the floor was short, the move alone helps...
                            if (Shortfall() < short0 && WorkedMin() <= worked0) return true;
                            // ...elsewhere a colleague must give the time up.
                            if (ShaveOnce(deficit, s)) return true;
                        }
                        s.Start = was0;
                        s.End = was1;
                    }
                    s.Start = was0;
                    s.End = was1;
                }
            }
            return false;
        }
        for (var guard = 0; guard < 400; guard++)
        {
            if (WorkedMin() - demandTotalMin < B) break;
            var deficit = BreakDeficit();
            if (ShaveOnce(deficit)) continue;
            if (input.SlideIdle && SlideShave(deficit)) continue;
            break;
        }

        var residualOverCover = Math.Max(0, WorkedMin() - demandTotalMin) / 60;
        if (residualOverCover > 0.26)
            flags.Add($"{Js.ToFixed(residualOverCover, 2)}h planned above the curve - 3h-minimum shifts and the ≥2-person close-out escort");

        // 3b. Consolidate: merge back-to-back SA slots into one longer shift when
        //     the combined length is legal and at least as cost-efficient - the
        //     interview's "a 7-10 and a 10-1 become one 7-1 shift". Coverage is
        //     identical (the two slots were disjoint in time) with one fewer head.
        var mergedAny = true;
        while (mergedAny)
        {
            mergedAny = false;
            var sas = Js.Sorted(slots.Where(s => s.Role == SlotRoles.SA && !IsFixed(s) && !IsCover(s)), (a, b) => a.Start - b.Start);
            foreach (var a in sas)
            {
                var b = sas.FirstOrDefault(x => !ReferenceEquals(x, a) && x.Start == a.End);
                if (b is null) continue;
                var len = b.End - a.Start;
                if (len > maxBlocks * B) continue;
                // One fewer person for the same cover - taken when it is no dearer: no
                // more paid time and no less work (it compared length ranks).
                var t = input.SaRule.BreakType ?? BreakTypes.Tiered;
                var la = a.End - a.Start;
                var lb = b.End - b.Start;
                if (ShiftLengthCost.PaidMinutes(t, len) > ShiftLengthCost.PaidMinutes(t, la) + ShiftLengthCost.PaidMinutes(t, lb)) continue;
                if (ShiftLengthCost.WorkMinutes(t, len) < ShiftLengthCost.WorkMinutes(t, la) + ShiftLengthCost.WorkMinutes(t, lb)) continue;
                var deficit = BreakDeficit();
                var aEnd = a.End;
                var at = slots.IndexOf(b);
                a.End = b.End;
                slots.RemoveAt(at);
                if (BreakDeficit() > deficit)
                {
                    slots.Insert(at, b);
                    a.End = aEnd;
                    continue;
                }
                mergedAny = true;
                break;
            }
        }

        // 3e. Shift length, last ─────────────────────────────────────────────────
        // The owner's final step (U6): "optimising shift length per the attached
        // table. This is not compulsory but where possible the cheapest shift
        // length should be used". So it runs on the finished day and only ever
        // takes a strictly cheaper day - less paid time for no less work, or more
        // work for no more paid time - priced from the award's own break tiers
        // (ShiftLengthCost). It used to run in the middle, compare length
        // RANKS (81% of the swaps it took were exactly cost-neutral), and be undone
        // by the passes after it (Roster Engine Review 07/11/2026, P5, A04-8).
        double PaidOf(PlanSlot sl, double? len = null) => ShiftLengthCost.PaidMinutes(TypeOf(sl.Role), len ?? sl.End - sl.Start);
        double WorkOf(PlanSlot sl, double? len = null) => ShiftLengthCost.WorkMinutes(TypeOf(sl.Role), len ?? sl.End - sl.Start);
        static bool Cheaper(double paid, double work, double paid0, double work0) =>
            paid <= paid0 && work >= work0 && (paid < paid0 || work > work0);
        //
        // (a) Hand a block across where two shifts meet. Cover is unchanged - the
        //     same count, a different body - so only the two lengths, and with
        //     them the breaks, change. The prize case is the six-hour tier: a
        //     6.25h shift is paid more and works less than a 6.00h one.
        double MinLen(PlanSlot x) => Math.Max(180, ToBlocks((x.Role == SlotRoles.JSA ? input.JsaRule : input.SaRule).MinShiftHours) * B);
        double MaxLen(PlanSlot x)
        {
            var rule = x.Role == SlotRoles.JSA ? input.JsaRule : input.SaRule;
            return Breaks.MaxClockMinutes(rule.BreakType ?? BreakTypes.Tiered, rule.MaxShiftHours);
        }
        for (var guard = 0; guard < 200; guard++)
        {
            var moved = false;
            var movable = slots.Where(x => !IsFixed(x) && !IsCover(x) && x.Role != SlotRoles.MGR).ToList();
            foreach (var a in movable)
            {
                foreach (var b in movable)
                {
                    if (ReferenceEquals(a, b)) continue;
                    var options = new List<(double AStart, double AEnd, double BStart, double BEnd)>();
                    if (a.End == b.Start) options.Add((a.Start, a.End - B, b.Start - B, b.End));
                    if (a.Start == b.End) options.Add((a.Start + B, a.End, b.Start, b.End + B));
                    foreach (var o in options)
                    {
                        var aLen = o.AEnd - o.AStart;
                        var bLen = o.BEnd - o.BStart;
                        if (aLen < MinLen(a) || bLen < MinLen(b) || bLen > MaxLen(b)) continue;
                        if (o.BStart < spanStart || o.BEnd > spanEnd) continue;
                        var paid0 = PaidOf(a) + PaidOf(b);
                        var work0 = WorkOf(a) + WorkOf(b);
                        if (!Cheaper(PaidOf(a, aLen) + PaidOf(b, bLen), WorkOf(a, aLen) + WorkOf(b, bLen), paid0, work0)) continue;
                        var was = (a.Start, a.End, b.Start, b.End);
                        var deficit = BreakDeficit();
                        a.Start = o.AStart;
                        a.End = o.AEnd;
                        b.Start = o.BStart;
                        b.End = o.BEnd;
                        if (BreakDeficit() > deficit)
                        {
                            (a.Start, a.End, b.Start, b.End) = was;
                            continue;
                        }
                        moved = true;
                        break;
                    }
                    if (moved) break;
                }
                if (moved) break;
            }
            if (!moved) break;
        }
        // (b) The owner's own example (U5): "Why would I not change one 6-1030 to
        //     be 6-10, minimise the break length and then move the 2 reset tiles
        //     down a shift into the gap?" A shift a quarter-hour past a break tier
        //     - 4.25h, 6.25h - is paid more than one a quarter shorter and delivers
        //     no more work: the extra quarter is eaten by the longer break. Hand
        //     back that quarter wherever the floor is over-covered there. The shave
        //     refused these ("shaving across a break tier saves nothing") because
        //     it counted work, not pay (A04-5).
        for (var guard = 0; guard < 200; guard++)
        {
            var trimmed = false;
            foreach (var sl in slots)
            {
                if (IsFixed(sl) || IsCover(sl)) continue;
                var len = sl.End - sl.Start;
                var rule = RuleOf(sl.Role);
                if (len - B < Js.Max(180, ToBlocks(rule.MinShiftHours) * B, sl.Role == SlotRoles.MGR ? R.ManagerMinShiftMin : 0)) continue;
                if (!Cheaper(PaidOf(sl, len - B), WorkOf(sl, len - B), PaidOf(sl), WorkOf(sl))) continue;
                foreach (var edge in Edges)
                {
                    if (sl.Role == SlotRoles.MGR && (sl.Id == "mgr-open" ? edge == "start" : edge == "end")) continue;
                    // The handover and the Admin pair hold here too (decisions 5 and 7).
                    if (sl.Role == SlotRoles.MGR && CutsHandover(sl)) continue;
                    var m = edge == "end" ? sl.End - B : sl.Start;
                    if (CoverAt(m) - 1 < Math.Max(DemandTotal(m), R.MinPeopleOnFloor)) continue;
                    if (sl.Role == SlotRoles.MGR && !AnotherManagerOn(sl, m)) continue;
                    if (AdminNeedsMgr(sl, m)) continue;
                    var deficit = BreakDeficit();
                    if (edge == "end") sl.End -= B;
                    else sl.Start += B;
                    if (BreakDeficit() > deficit)
                    {
                        if (edge == "end") sl.End += B;
                        else sl.Start -= B;
                        continue;
                    }
                    trimmed = true;
                    break;
                }
            }
            if (!trimmed) break;
        }

        // Break cover, checked last. Everything above that moves slot edges is
        // held to the break cover already bought, but the escorts and the
        // consolidation change lengths - and with them break entitlements - so the
        // two checks run once more on the finished day.
        CoverWindowBreaks();
        CoverTillBreaks();
        if (coverGrown > 0)
            flags.Add($"{Js.ToFixed(coverGrown / 60, 2)}h added to existing shifts so the crew can take its breaks without losing the work");
        flags.AddRange(addedBodies);
        if (tillCoverAdded > 0)
            flags.Add($"{Js.ToFixed(tillCoverAdded / 60, 2)}h added so the tills stay manned while the crew takes its breaks");

        // 4. Safety floor check across the staffed span (data should guarantee ≥2;
        //    if it doesn't, say so rather than silently padding cost in).
        double loneFrom = -1;
        for (var m = spanStart; m < spanEnd; m += B)
        {
            var lone = CoverAt(m) < R.MinPeopleOnFloor;
            if (lone && loneFrom == -1) loneFrom = m;
            if (!lone && loneFrom != -1)
            {
                flags.Add($"Only 1 person planned {Fmt(loneFrom)}–{Fmt(m)} - below the {Js.NumberToString(R.MinPeopleOnFloor)}-person floor (check the benchmark demand)");
                loneFrom = -1;
            }
        }
        if (loneFrom != -1)
            flags.Add($"Only 1 person planned {Fmt(loneFrom)}–{Fmt(spanEnd)} - below the {Js.NumberToString(R.MinPeopleOnFloor)}-person floor (check the benchmark demand)");

        // 5. JSA labelling (see LabelJsaSlots). A label is kept only if it costs
        // no break cover and leaves no quarter-hour with fewer people able to take
        // a till than there are tills (J4's own condition; review RC-7 - four days
        // of the sample had enough till-capable people before the label and one to
        // four quarter-hours short after it, a junior idle beside the empty till).
        var deficitBeforeJsa = BreakDeficit();
        var tillShortBeforeJsa = TillShortBlocks();
        var stuckBeforeJsa = new JsSet<string>();
        TillStuck(stuckBeforeJsa);
        bool StrandsNoBreak()
        {
            var now = new JsSet<string>();
            TillStuck(now);
            foreach (var k in now)
                if (!stuckBeforeJsa.Has(k)) return false;
            return true;
        }
        LabelJsaSlots(
            slots,
            input.IsSchoolDay,
            input.JsaRule,
            null,
            () => BreakDeficit() <= deficitBeforeJsa && TillShortBlocks() <= tillShortBeforeJsa && StrandsNoBreak());

        // 6. Hierarchy order: MGR opener, MGR closer, SAs by start, JSAs by start.
        static double Rank(string role) => role == SlotRoles.MGR ? 0 : role == SlotRoles.SA ? 1 : role == SlotRoles.JSA ? 2 : double.NaN;
        Js.Sort(slots, (a, b) => (Rank(a.Role) - Rank(b.Role)).Or(a.Start - b.Start).Or(a.End - b.End));

        var breakSlack = new JsMap<string, double>();
        foreach (var w in breakWindows)
        {
            double n = 0;
            for (var m = w.Start; m < w.End; m += B) n += Math.Max(0, CoverAt(m) - DemandTotal(m));
            breakSlack.Set(w.Group, n);
        }

        return new ShapedDay
        {
            Slots = slots,
            DemandByBlock = Js.Sorted(demand.Values(), (a, b) => a.Time - b.Time),
            Flags = flags,
            BreakSlack = breakSlack,
        };
    }

    private static readonly string[] Edges = ["end", "start"];
}
