// Port of src/lib/stage2/handoverCut.ts.
// ─── The handover hour with less work than the two managers ──────────────────
// The managers' handover is a rule: at least an hour, never cut (owner
// decision 5, R7). At a one-till store that hour can hold less work than the
// two managers on it and the assistant beside them: one till, a little Admin
// and Reset. The shaper cannot cut the assistant there, because the
// quarter-hour after the opening manager leaves needs that assistant to keep
// two people on, and the next assistant only starts later (G351 11/11: an
// assistant on 08:00-16:00, the next starting 15:45, a manager idle three
// quarter-hours of the 14:30-15:30 handover).
//
// The owner's decision (14/11/2026): build the better cut. Where the painted
// day has a manager with no work inside the handover, this tries giving the
// managers the work an assistant's shift edge carried there: the assistant
// leaves earlier (or starts later), and a colleague whose shift begins (or
// ends) beside the gap may come in that much earlier (or stay that much
// later) to keep the floor covered. Reset left without a home moves inside
// its own window, which the painter already does (decision 11); decision 3 -
// where two people must be on, work is moved in so both have work.
//
// Each candidate is painted by the ordinary painter and scored by the day
// checker; one is kept only if it is better on every count: less shift time
// with no work, and no more work lost, errors, hours worked or hours paid.
// The managers' shifts, fixed shifts and the cover bought for the crew's
// breaks are never touched; every shift keeps the 3-hour minimum and its
// longest legal length; a junior stays inside their hours. It is not a
// re-painting solver (the owner deferred that) and it never paints a 'spare'
// tile: it only moves assistants' shift edges and lets the painter work. Pure.

using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Engine.Rostering.Shaper;

public sealed record HandoverCutLimits
{
    public double StaffOpen { get; init; }
    public double StaffClose { get; init; }
    /// <summary>Longest legal shift by the clock, minutes, per slot type (a missing or zero entry has no limit).</summary>
    public IReadOnlyDictionary<string, double> MaxClock { get; init; } = new Dictionary<string, double>();
    public bool IsSchoolDay { get; init; }
    /// <summary>The shortest shift, minutes (the 3-hour minimum).</summary>
    public double MinShiftMin { get; init; }
    /// <summary>Tills wanted in each quarter-hour: the cut is for a handover hour with one.</summary>
    public Func<double, double>? TillsAt { get; init; }
}

/// <summary>A quarter-hour of a slot: <c>{ slotId, time }</c>.</summary>
public sealed record SlotTime(string SlotId, double Time);

/// <summary>How a painted day stands, by the day checker.</summary>
public sealed record HandoverCutScore
{
    /// <summary>Quarter-hours of work on nobody's shift.</summary>
    public double Lost { get; init; }
    /// <summary>Errors other than the lost work.</summary>
    public double Errors { get; init; }
    /// <summary>Quarter-hours of shift time with no work.</summary>
    public double Blank { get; init; }
    /// <summary>Each blank quarter-hour, on whose slot.</summary>
    public IReadOnlyList<SlotTime> BlankAt { get; init; } = [];

    public HandoverCutScore() { }

    public HandoverCutScore(double lost, double errors, double blank, IReadOnlyList<SlotTime> blankAt)
    {
        Lost = lost;
        Errors = errors;
        Blank = blank;
        BlankAt = blankAt;
    }
}

public sealed record HandoverCutChange(string SlotId, string Role, ShiftTimes From, ShiftTimes To);

public sealed record HandoverCutResult<TPainted>(
    List<PlanSlot> Slots,
    TPainted Painted,
    HandoverCutScore Score,
    List<HandoverCutChange> Changes);

/// <summary>Minutes worked and paid by a set of slots: <c>{ worked, paid }</c>.</summary>
public sealed record WorkedPaid(double Worked, double Paid);

public static class HandoverCut
{
    private const double B = Grid.BlockMin;
    /// <summary>The furthest an assistant's edge moves (a shift is never cut by more).</summary>
    private const double MaxTrimMin = 2 * 60;
    /// <summary>How far from the gap a colleague may start (or end) and still be moved to it.</summary>
    private const double PartnerReachMin = 2 * 60;
    private const double JuniorEarliest = 6 * 60;
    private const double JuniorLatest = 21 * 60;
    private const double JuniorSchoolDayStart = 16 * 60;

    private static bool IsManager(PlanSlot s) => s.Role == SlotRoles.MGR;

    private static bool IsMovable(PlanSlot s) =>
        !IsManager(s) && !s.Id.StartsWith("fixed-", StringComparison.Ordinal) && !s.Id.StartsWith("sa-cover-", StringComparison.Ordinal);

    /// <summary>Minutes worked and paid by a set of slots (shift less its break).</summary>
    public static WorkedPaid SlotHours(IEnumerable<PlanSlot> slots)
    {
        double worked = 0;
        double paid = 0;
        foreach (var s in slots)
        {
            var len = s.End - s.Start;
            var brk = Breaks.BreakForShift(IsManager(s) ? BreakTypes.Flat30Unpaid : BreakTypes.Tiered, len / 60);
            worked += len - brk.TotalMinutes;
            paid += len - brk.UnpaidMinutes;
        }
        return new WorkedPaid(worked, paid);
    }

    /// <summary>The managers' handover: where the opening and closing managers overlap.</summary>
    public static ShiftTimes? HandoverOf(IReadOnlyList<PlanSlot> slots)
    {
        var mgrs = slots.Where(IsManager).ToList();
        if (mgrs.Count < 2) return null;
        var opener = Js.Sorted(mgrs, (a, b) => (a.Start - b.Start).Or(a.End - b.End))[0];
        var closer = Js.Sorted(mgrs, (a, b) => (b.End - a.End).Or(b.Start - a.Start))[0];
        if (ReferenceEquals(opener, closer)) return null;
        var start = Math.Max(opener.Start, closer.Start);
        var end = Math.Min(opener.End, closer.End);
        return end > start ? new ShiftTimes(start, end) : null;
    }

    /// <summary>Manager quarter-hours with no work inside the handover.</summary>
    public static List<double> IdleInHandover(IReadOnlyList<PlanSlot> slots, IReadOnlyList<SlotTime> blankAt)
    {
        var ho = HandoverOf(slots);
        if (ho is null) return [];
        var mgrIds = new JsSet<string>(slots.Where(IsManager).Select(s => s.Id));
        return blankAt.Where(b => mgrIds.Has(b.SlotId) && b.Time >= ho.Start && b.Time < ho.End).Select(b => b.Time).ToList();
    }

    /// <summary>
    /// Whether a day is the owner's case at all: a manager with no work inside the
    /// handover, and one till wanted through it (a one-till store, or a one-till
    /// hour). Where more are wanted, the assistant is on a till the managers would
    /// have to take over two-deep. Cheap: callers ask before scoring a day.
    /// </summary>
    public static bool WantsHandoverCut(IReadOnlyList<PlanSlot> slots, IReadOnlyList<SlotTime> blankAt, Func<double, double>? tillsAt = null)
    {
        if (IdleInHandover(slots, blankAt).Count == 0) return false;
        var ho = HandoverOf(slots)!;
        if (tillsAt is not null)
            for (var t = ho.Start; t < ho.End; t += B)
                if (tillsAt(t) > 1) return false;
        return true;
    }

    private static bool Legal(string role, double start, double end, HandoverCutLimits lim)
    {
        var len = end - start;
        if (len < lim.MinShiftMin) return false;
        // `if (max && len > max)`: a missing (or zero) maximum is no limit.
        if (lim.MaxClock.TryGetValue(role, out var max) && Js.Truthy(max) && len > max) return false;
        if (start < lim.StaffOpen || end > lim.StaffClose) return false;
        if (role == SlotRoles.JSA)
        {
            if (start < (lim.IsSchoolDay ? JuniorSchoolDayStart : JuniorEarliest)) return false;
            if (end > JuniorLatest) return false;
        }
        return true;
    }

    /// <summary>
    /// The candidate cuts: an assistant on during the handover leaves earlier (or
    /// starts later) by up to two hours, alone or with a colleague whose shift
    /// begins (or ends) beside the gap moved to meet it. Only cuts that take shift
    /// time away, and no more than the day's shift time with no work could give
    /// (plus a break tier), are tried.
    /// </summary>
    public static List<List<PlanSlot>> HandoverCandidates(IReadOnlyList<PlanSlot> slots, HandoverCutLimits lim, double blankQh)
    {
        var ho = HandoverOf(slots);
        if (ho is null) return [];
        var out_ = new List<List<PlanSlot>>();
        // At most the day's blank time, plus an hour a shorter break can give back.
        var maxNetCut = blankQh * B + 60;
        List<PlanSlot> WithChange(params (string Id, ShiftTimes To)[] changes)
        {
            var byId = new JsMap<string, ShiftTimes>();
            foreach (var (id, to) in changes) byId.Set(id, to);
            return slots.Select(s => byId.TryGet(s.Id, out var c) ? s.With(c.Start, c.End) : s).ToList();
        }
        foreach (var a in slots)
        {
            if (!IsMovable(a) || a.End <= ho.Start || a.Start >= ho.End) continue;
            foreach (var edge in new[] { "end", "start" })
            {
                for (var k = 1; k * B <= MaxTrimMin; k++)
                {
                    var cut = edge == "end" ? new ShiftTimes(a.Start, a.End - k * B) : new ShiftTimes(a.Start + k * B, a.End);
                    // The time given up must reach into the handover.
                    if (edge == "end" ? cut.End >= ho.End : cut.Start <= ho.Start) continue;
                    if (edge == "end" ? cut.End < ho.Start : cut.Start > ho.End) break;
                    if (!Legal(a.Role, cut.Start, cut.End, lim)) break;
                    if (k * B <= maxNetCut) out_.Add(WithChange((a.Id, cut)));
                    // A colleague beside the gap moves to meet it.
                    foreach (var b in slots)
                    {
                        if (ReferenceEquals(b, a) || !IsMovable(b)) continue;
                        var gap = edge == "end" ? b.Start - cut.End : cut.Start - b.End;
                        if (gap <= 0 || gap > PartnerReachMin) continue;
                        for (var j = 1; j * B <= gap && j < k; j++)
                        {
                            if ((k - j) * B > maxNetCut) continue;
                            var moved = edge == "end" ? new ShiftTimes(b.Start - j * B, b.End) : new ShiftTimes(b.Start, b.End + j * B);
                            if (!Legal(b.Role, moved.Start, moved.End, lim)) continue;
                            out_.Add(WithChange((a.Id, cut), (b.Id, moved)));
                        }
                    }
                }
            }
        }
        return out_;
    }

    /// <summary>
    /// Cut an assistant's shift beside a handover hour that has less work than the
    /// managers on it, when the painted day is better for it on every count.
    /// <paramref name="paint"/> paints and scores a set of slots; <paramref name="start"/> is the day as painted.
    ///
    /// <paramref name="lostWithTieredManagers"/>, when given, paints a set of slots with someone
    /// owed a store assistant's breaks (an RSM) in each manager slot and returns
    /// the work lost. The cut leaves the handover hour with no slack, and the
    /// roster may fill a manager slot with an RSM, whose hour of breaks on a long
    /// shift is twice a manager's: on the generated test stores that lost the
    /// closing manager's End of Day. A cut is kept only if it loses no more work
    /// that way than the uncut day would: it may not make the day more fragile.
    /// </summary>
    public static HandoverCutResult<TPainted> CutBesideHandover<TPainted>(
        List<PlanSlot> slots,
        HandoverCutLimits lim,
        Func<List<PlanSlot>, ScoredPaint<TPainted, HandoverCutScore>> paint,
        ScoredPaint<TPainted, HandoverCutScore> start,
        Func<List<PlanSlot>, double>? lostWithTieredManagers = null)
    {
        var none = new HandoverCutResult<TPainted>(slots, start.Painted, start.Score, []);
        if (!WantsHandoverCut(slots, start.Score.BlankAt, lim.TillsAt)) return none;
        var h0 = SlotHours(slots);
        // The idle manager time in the hour is what the cut is for: once a
        // candidate clears all of it, nothing better is looked for (few paints).
        var idle = IdleInHandover(slots, start.Score.BlankAt).Count;
        var target = start.Score.Blank - idle;
        var cands = Js.Sort(
            HandoverCandidates(slots, lim, start.Score.Blank)
                .Select(c => (C: c, H: SlotHours(c)))
                // Cheap first: no more hours worked or paid, and no less work time than
                // the day's work needs (the shift time with no work is all that may go).
                .Where(x => x.H.Worked <= h0.Worked && x.H.Paid <= h0.Paid && h0.Worked - x.H.Worked <= start.Score.Blank * B)
                .ToList(),
            // Closest first to giving up exactly the idle time, then fewest hours.
            (a, b) =>
                (Math.Abs(h0.Worked - a.H.Worked - idle * B) - Math.Abs(h0.Worked - b.H.Worked - idle * B))
                    .Or(a.H.Worked - b.H.Worked)
                    .Or(a.H.Paid - b.H.Paid));
        double? lost0 = null;
        bool Holds(List<PlanSlot> c)
        {
            if (lostWithTieredManagers is null) return true;
            lost0 ??= lostWithTieredManagers(slots);
            return lostWithTieredManagers(c) <= lost0.Value;
        }
        var better = new List<(List<PlanSlot> Slots, TPainted Painted, HandoverCutScore Score, double Worked, double Paid)>();
        (List<PlanSlot> Slots, TPainted Painted, HandoverCutScore Score, double Worked, double Paid)? best = null;
        foreach (var (c, h) in cands)
        {
            var r = paint(c);
            var s = r.Score;
            if (s.Blank >= start.Score.Blank || s.Lost > start.Score.Lost || s.Errors > start.Score.Errors) continue;
            var x = (Slots: c, Painted: r.Painted, Score: s, Worked: h.Worked, Paid: h.Paid);
            if (s.Blank <= target && Holds(c))
            {
                best = x;
                break;
            }
            better.Add(x);
        }
        if (best is null)
        {
            // Least shift time with no work, then fewest hours worked, then paid.
            Js.Sort(better, (a, b) => (a.Score.Blank - b.Score.Blank).Or(a.Worked - b.Worked).Or(a.Paid - b.Paid));
            foreach (var x in better)
            {
                if (!Holds(x.Slots)) continue;
                best = x;
                break;
            }
        }
        if (best is null) return none;
        var won = best.Value;
        var changes = new List<HandoverCutChange>();
        for (var i = 0; i < won.Slots.Count; i++)
        {
            var s = won.Slots[i];
            var was = slots[i];
            if (s.Start != was.Start || s.End != was.End)
                changes.Add(new HandoverCutChange(s.Id, s.Role, new ShiftTimes(was.Start, was.End), new ShiftTimes(s.Start, s.End)));
        }
        return new HandoverCutResult<TPainted>(won.Slots, won.Painted, won.Score, changes);
    }
}
