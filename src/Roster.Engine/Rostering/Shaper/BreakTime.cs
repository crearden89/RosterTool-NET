// Port of src/lib/stage2/breakTime.ts.
// ─── Time added so breaks don't cost work (master plan decision 4.2) ─────────
// On most days the plan holds exactly the benchmark hours, so when a break
// pulls someone off their work - an assistant's break taking a manager onto a
// till, say - there can be no free quarter-hour left inside the work's window
// to make it up, and the work ends up on nobody's shift. The owner's decision
// (14/11/2026): add the time instead. After a day is painted, while work is
// still on nobody's shift, lengthen a shift by a quarter- or half-hour at the
// end that helps and paint again, keeping the change that saves the most work
// for the least time; repeat until nothing is lost, nothing helps or the
// day's allowance is used. Every shift rule is kept: no shift past its
// longest legal length, nobody outside the staffed day, a junior inside
// 06:00-21:00 (16:00 on a school day), fixed-roster shifts untouched. It is
// not a re-painting solver (the owner deferred that): it only lengthens
// shifts and lets the ordinary painter use the time. Pure.

using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Engine.Rostering.Shaper;

public sealed record BreakTimeLimits
{
    public double StaffOpen { get; init; }
    public double StaffClose { get; init; }
    /// <summary>Longest legal shift by the clock, minutes, per slot type (a missing type has no limit).</summary>
    public IReadOnlyDictionary<string, double> MaxClock { get; init; } = new Dictionary<string, double>();
    public bool IsSchoolDay { get; init; }
    /// <summary>The most shift time the day may be given, minutes (the setting).</summary>
    public double BudgetMin { get; init; }
}

/// <summary>How a painted day stands: what decides whether a change helped.</summary>
public sealed record BreakTimeScore
{
    /// <summary>Quarter-hours of work on nobody's shift.</summary>
    public double Lost { get; init; }
    /// <summary>Their times, to aim the candidates.</summary>
    public IReadOnlyList<double> LostAt { get; init; } = [];
    /// <summary>Errors other than the lost work (a change may not add one).</summary>
    public double Errors { get; init; }
    /// <summary>Quarter-hours of shift time with no work.</summary>
    public double Blank { get; init; }

    public BreakTimeScore() { }

    public BreakTimeScore(double lost, IReadOnlyList<double> lostAt, double errors, double blank)
    {
        Lost = lost;
        LostAt = lostAt;
        Errors = errors;
        Blank = blank;
    }
}

public sealed record BreakTimeChange(string SlotId, string Role, ShiftTimes From, ShiftTimes To);

/// <summary>The roster's change: a named person's shift lengthened.</summary>
public sealed record RosterBreakTimeChange(string Who, ShiftTimes From, ShiftTimes To);

/// <summary>A person whose shift the roster swapped: <c>{ who, start, end }</c>.</summary>
public sealed record RosterSwapMove(string Who, double Start, double End);

public sealed record BreakTimeResult<TPainted>(
    List<PlanSlot> Slots,
    TPainted Painted,
    BreakTimeScore Score,
    List<BreakTimeChange> Changes,
    double AddedMin);

public static class BreakTime
{
    private static readonly double[] Steps = [Grid.BlockMin, 2 * Grid.BlockMin];
    /// <summary>Only shift edges this close to the lost work are tried (few, well-aimed candidates).</summary>
    private const double ReachMin = 4 * 60;
    private const double JuniorEarliest = 6 * 60;
    private const double JuniorLatest = 21 * 60;
    private const double JuniorSchoolDayStart = 16 * 60;

    private static string RoleLabel(string role) => role switch
    {
        SlotRoles.MGR => "Manager",
        SlotRoles.SA => "Store assistant",
        SlotRoles.JSA => "Junior",
        _ => "undefined", // ROLE_LABEL[role] for a role it does not list
    };

    // The notes on time given to breaks read as one family, each saying which
    // kind it is (see plannerNotes in lib/stage1/planShifts.ts for the other two):
    //   "Extra store assistant for the crew's breaks: ..."  - a person added;
    //   "Longer shifts for the crew's breaks: ..."          - existing shifts
    //                                                        lengthened for them;
    //   "Longer shift so a break doesn't cost work: ..."    - this one: a shift
    //                                                        lengthened so one
    //                                                        break costs no work.
    /// <summary>
    /// BREAK_TIME_NOTE: the planner's note on the time it added; the hours check
    /// reads the hours back. Plans held before the note was reworded say "Time
    /// added so breaks don't cost work: 0.25 h - ...", and are read the same.
    /// </summary>
    public static readonly Regex BreakTimeNotePattern = new(
        @"^(?:Longer shifts? so (?:a break doesn't|breaks don't) cost work|Time added so breaks don't cost work): ([\d.]+) h\b",
        RegexOptions.ECMAScript);

    /// <summary>Hours the planner added so breaks don't cost work, from its notes on the day.</summary>
    public static double BreakTimeAdded(IEnumerable<string>? notes) => HoursFrom(notes, BreakTimeNotePattern);

    private static double HoursFrom(IEnumerable<string>? notes, Regex pattern)
    {
        double h = 0;
        foreach (var n in notes ?? [])
        {
            var m = pattern.Match(n);
            if (m.Success) h += Js.Number(m.Groups[1].Value);
        }
        return h;
    }

    /// <summary>"15 minutes", "1 hour", "1 hour 30 minutes".</summary>
    public static string MinutesInWords(double min)
    {
        var h = Math.Floor(min / 60);
        var m = min % 60;
        var hours = Js.Truthy(h) ? $"{Js.NumberToString(h)} hour{(h == 1 ? "" : "s")}" : "";
        var mins = Js.Truthy(m) ? $"{Js.NumberToString(m)} minute{(m == 1 ? "" : "s")}" : "";
        var joined = string.Join(" ", new[] { hours, mins }.Where(s => s.Length > 0));
        return joined.Length > 0 ? joined : "0 minutes";
    }

    /// <summary>How a shift changed, in words: "starts 15 minutes earlier and finishes 15 minutes later".</summary>
    private static string HowChanged(ShiftTimes from, ShiftTimes to)
    {
        var parts = new List<string>();
        if (to.Start < from.Start) parts.Add($"starts {MinutesInWords(from.Start - to.Start)} earlier");
        if (to.End > from.End) parts.Add($"finishes {MinutesInWords(to.End - from.End)} later");
        return string.Join(" and ", parts);
    }

    /// <summary>"Manager now 11:15–17:30 (starts 15 minutes earlier)": the shift as it is now.</summary>
    private static string ShiftNow(string who, ShiftTimes from, ShiftTimes to) =>
        $"{who} now {Grid.MinToTime(to.Start)}–{Grid.MinToTime(to.End)} ({HowChanged(from, to)})";

    public static string? BreakTimeNote(IEnumerable<BreakTimeChange> changes)
    {
        var byId = new JsMap<string, BreakTimeChange>();
        // A shift lengthened twice is one change, from where it started.
        foreach (var c in changes)
        {
            var prev = byId.Get(c.SlotId);
            byId.Set(c.SlotId, prev is not null ? c with { From = prev.From } : c);
        }
        var all = byId.Values().ToList();
        double min = 0;
        foreach (var c in all) min = min + (c.To.End - c.To.Start) - (c.From.End - c.From.Start);
        if (min <= 0) return null;
        var grown = all.Where(c => c.To.Start < c.From.Start || c.To.End > c.From.End).ToList();
        var parts = grown.Select(c => ShiftNow(RoleLabel(c.Role), c.From, c.To));
        var head = grown.Count == 1 ? "Longer shift so a break doesn't cost work" : "Longer shifts so breaks don't cost work";
        return $"{head}: {Js.ToFixed(min / 60, 2)} h - {string.Join("; ", parts)}, so the work a break would push out is still done in its window";
    }

    // ─── The same on the roster ──────────────────────────────────────────────────
    // The roster fills the plan's shifts with real people, and the person in a
    // shift can be entitled to a different break from the one the plan priced (a
    // manager's 30 minutes in a store assistant's shift with 15). Where that puts
    // work on nobody's shift, the roster first tries swapping who holds two of the
    // day's shifts, then adds time the same way the plan does, within the same
    // setting (less what the plan already added that day). Its notes say so.

    /// <summary>
    /// ROSTER_BREAK_TIME_NOTE: the roster's note on time it added; the plan's own
    /// note is <see cref="BreakTimeNotePattern"/>. Rosters held before the note was
    /// reworded say "Time added on the roster so breaks don't cost work: 0.25 h -
    /// ...", and are read the same.
    /// </summary>
    public static readonly Regex RosterBreakTimeNotePattern = new(
        @"^(?:Longer shifts? on the roster so (?:a break doesn't|breaks don't) cost work|Time added on the roster so breaks don't cost work): ([\d.]+) h\b",
        RegexOptions.ECMAScript);

    /// <summary>Hours the roster added so breaks don't cost work, from its notes on the day.</summary>
    public static double RosterBreakTimeAdded(IEnumerable<string>? notes) => HoursFrom(notes, RosterBreakTimeNotePattern);

    public static string? RosterBreakTimeNote(IEnumerable<RosterBreakTimeChange> changes)
    {
        var grown = changes.Where(c => c.To.End - c.To.Start > c.From.End - c.From.Start).ToList();
        double min = 0;
        foreach (var c in grown) min = min + (c.To.End - c.To.Start) - (c.From.End - c.From.Start);
        if (min <= 0) return null;
        var parts = grown.Select(c => ShiftNow(c.Who, c.From, c.To));
        var head = grown.Count == 1
            ? "Longer shift on the roster so a break doesn't cost work"
            : "Longer shifts on the roster so breaks don't cost work";
        return $"{head}: {Js.ToFixed(min / 60, 2)} h - {string.Join("; ", parts)}, so the work a break would push out is still done in its window";
    }

    /// <summary>
    /// ROSTER_BREAK_CUT_NOTE: the roster's note when it cut shifts so their edge
    /// work fills time a shorter break left empty (rosterBreakCutNote in
    /// breakGap.ts); the hours it cut, which a check of the roster's hours against
    /// the plan's counts as explained.
    /// </summary>
    public static readonly Regex RosterBreakCutNotePattern = new(
        @"^Shorter shifts? on the roster so (?:a break leaves|breaks leave) no time empty: ([\d.]+) h\b",
        RegexOptions.ECMAScript);

    /// <summary>Hours the roster cut so a shorter break leaves no time empty, from its notes on the day.</summary>
    public static double RosterBreakCutHours(IEnumerable<string>? notes) => HoursFrom(notes, RosterBreakCutNotePattern);

    /// <summary>The roster's note when it swapped who holds some of the day's shifts so a break costs no work.</summary>
    public static string RosterSwapNote(IReadOnlyList<RosterSwapMove> moved)
    {
        var parts = moved.Select((m, i) => $"{m.Who}{(i == 0 ? " works" : "")} {Grid.MinToTime(m.Start)}–{Grid.MinToTime(m.End)}").ToList();
        var list = parts.Count > 1
            ? $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
            : parts.Count == 1 ? parts[0] : "undefined";
        return $"Shifts swapped so breaks don't cost work: {list}, because their breaks differ in length";
    }

    private static List<List<PlanSlot>> Candidates(List<PlanSlot> slots, BreakTimeLimits lim, IReadOnlyList<double> lostAt, double left)
    {
        var lo = Js.Min(lostAt) - ReachMin;
        var hi = Js.Max(lostAt) + Grid.BlockMin + ReachMin;
        var out_ = new List<List<PlanSlot>>();
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.Id.StartsWith("fixed-", StringComparison.Ordinal)) continue;
            var max = lim.MaxClock.TryGetValue(s.Role, out var mc) ? mc : double.PositiveInfinity;
            foreach (var d in Steps)
            {
                if (d > left || s.End - s.Start + d > max) continue;
                // Earlier start.
                var start = s.Start - d;
                var earliest = Math.Max(
                    lim.StaffOpen,
                    s.Role == SlotRoles.JSA ? (lim.IsSchoolDay ? JuniorSchoolDayStart : JuniorEarliest) : double.NegativeInfinity);
                if (start >= earliest && s.Start >= lo && s.Start <= hi)
                    out_.Add(Replace(slots, i, s.With(start, s.End)));
                // Later end.
                var end = s.End + d;
                var latest = Math.Min(lim.StaffClose, s.Role == SlotRoles.JSA ? JuniorLatest : double.PositiveInfinity);
                if (end <= latest && s.End >= lo && s.End <= hi) out_.Add(Replace(slots, i, s.With(s.Start, end)));
            }
        }
        return out_;
    }

    /// <summary><c>slots.map((o, k) => (k === i ? replacement : o))</c></summary>
    private static List<PlanSlot> Replace(List<PlanSlot> slots, int i, PlanSlot replacement)
    {
        var copy = new List<PlanSlot>(slots);
        copy[i] = replacement;
        return copy;
    }

    private static double Better(BreakTimeScore a, double aMin, BreakTimeScore b, double bMin) =>
        (a.Lost - b.Lost).Or(a.Errors - b.Errors).Or(a.Blank - b.Blank).Or(aMin - bMin);

    private static double Length(IEnumerable<PlanSlot> slots)
    {
        double n = 0;
        foreach (var s in slots) n = n + s.End - s.Start;
        return n;
    }

    /// <summary>
    /// Lengthen shifts, a quarter- or half-hour at a time, while work is on
    /// nobody's shift and a change saves some of it without adding an error.
    /// <paramref name="paint"/> paints and scores a set of slots; <paramref name="start"/> is the day as painted.
    /// </summary>
    public static BreakTimeResult<TPainted> AddTimeForBreaks<TPainted>(
        List<PlanSlot> slots,
        BreakTimeLimits lim,
        Func<List<PlanSlot>, ScoredPaint<TPainted, BreakTimeScore>> paint,
        ScoredPaint<TPainted, BreakTimeScore> start)
    {
        var curSlots = slots;
        var curPainted = start.Painted;
        var curScore = start.Score;
        var changes = new List<BreakTimeChange>();
        double addedMin = 0;
        while (curScore.Lost > 0 && addedMin < lim.BudgetMin)
        {
            (List<PlanSlot> Slots, TPainted Painted, BreakTimeScore Score, double Min)? best = null;
            foreach (var cand in Candidates(curSlots, lim, curScore.LostAt, lim.BudgetMin - addedMin))
            {
                var r = paint(cand);
                // Less work lost and no new error, or as little lost and an error gone.
                var helps =
                    (r.Score.Lost < curScore.Lost && r.Score.Errors <= curScore.Errors) ||
                    (r.Score.Lost <= curScore.Lost && r.Score.Errors < curScore.Errors);
                if (!helps) continue;
                var min = Length(cand) - Length(curSlots);
                if (best is null || Better(r.Score, min, best.Value.Score, best.Value.Min) < 0)
                    best = (cand, r.Painted, r.Score, min);
            }
            if (best is null) break;
            var b = best.Value;
            for (var i = 0; i < b.Slots.Count; i++)
            {
                var s = b.Slots[i];
                var was = curSlots[i];
                if (s.Start != was.Start || s.End != was.End)
                    changes.Add(new BreakTimeChange(s.Id, s.Role, new ShiftTimes(was.Start, was.End), new ShiftTimes(s.Start, s.End)));
            }
            addedMin += b.Min;
            curSlots = b.Slots;
            curPainted = b.Painted;
            curScore = b.Score;
        }
        return new BreakTimeResult<TPainted>(curSlots, curPainted, curScore, changes, addedMin);
    }
}
