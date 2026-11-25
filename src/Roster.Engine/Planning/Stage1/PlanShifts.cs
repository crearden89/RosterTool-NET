// Port of src/lib/stage1/planShifts.ts - the pure parts. The data layer
// (D2's Services) runs computePlanShifts' reads: the day's slots (E6's
// PlanSlots), the task priority, the rule overrides and the day check's
// options, and labels the tiles (labelPlanDay) between Unlabelled and Finish.
//
// ─── Generic Shift Plan (the Task view) ───────────────────────────────────────
// The PLAN is the demand-driven ideal template (interview model): the shaper
// (Rostering/Shaper) turns Stage 1's demand curve into typed slots - exactly
// two manager shifts (opener + closer with the mandatory handover overlap),
// then SA/JSA shifts hugging the curve at the cost-efficient lengths. This
// module just paints each slot's 15-min blocks with its tasks for display.
// The ROSTER fills these same slots with real names - the structures match by
// construction because both read computeDaySlots.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Planning.Stage1;

public sealed record PlanShift
{
    public string SlotId { get; init; } = "";
    /// <summary>"MGR", "SA" or "JSA" (RoleBadges).</summary>
    public string PlanRole { get; init; } = RoleBadges.Sa;
    public double Start { get; init; }
    public double End { get; init; }
    public double BreakMinutes { get; init; } // total off-floor break
    public double UnpaidBreakMinutes { get; init; } // portion deducted from paid hours
    public List<DisplayBlock> Blocks { get; init; } = [];

    /// <summary>The shift as the day checker reads it.</summary>
    public CheckedShift ToChecked() => new() { PlanRole = PlanRole, SlotId = SlotId, Start = Start, End = End, Blocks = Blocks };
}

public sealed record PlanShiftComputation
{
    public bool HasData { get; init; }
    public double TradingOpen { get; init; }
    public double TradingClose { get; init; }
    public List<PlanShift> Shifts { get; init; } = [];
    // Everything the Plan page and the Review show for the day: the planner's
    // notes, then the day checker's findings.
    public List<string> Flags { get; init; } = [];
    // The planner's own notes on what it did (an extra body for the load, time
    // added for breaks), kept apart so an edit can re-check the day without
    // losing them. Absent on snapshots from before the day checker.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Notes { get; init; }
    // The day checker's findings on the shifts as they stand (DayChecker)
    // and its hours check - refreshed by every edit.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Checks { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HoursCheck? Hours { get; init; }
}

public static class PlanShifts
{
    /// <summary>computePlanShifts' answer for a day with no data (or no shaped slots).</summary>
    public static PlanShiftComputation Empty() => new() { HasData = false, TradingOpen = 0, TradingClose = 0, Shifts = [], Flags = [] };

    // Planner notes the day checker replaces with findings read from the finished
    // shifts (review RC-29): "above the curve" was worked out before the last
    // trims and named the wrong cause; "nobody works alone" covered only the
    // middle of the day; the "planned short" forecast was false wherever it
    // appeared. Cover, blank time and lost work are re-read from the shifts.
    // (JavaScript's $ is \z here; [0-9] is \d.)
    private static readonly Regex[] SupersededNotes =
    [
        new("planned above the curve"),
        new("^Only 1 person planned"),
        new("of shift added so nobody works alone"),
        new(@"that work will be planned short\z"),
    ];

    private static readonly Regex ExtraBody = new(
        @"^Extra body ([0-9][0-9]:[0-9][0-9])-([0-9][0-9]:[0-9][0-9]) so the crew can take its breaks without losing the work - ([0-9.]+)h of award break falls inside a window that closes at ([0-9][0-9]:[0-9][0-9])\z");
    // The shaper's notes on shift time it added for the crew's breaks.
    private static readonly Regex CoverGrown = new(@"^([0-9.]+)h added to existing shifts so the crew can take its breaks without losing the work\z");
    private static readonly Regex TillCover = new(@"^([0-9.]+)h added so the tills stay manned while the crew takes its breaks\z");

    // The notes on time given to breaks read as one family, each saying which
    // kind it is: a person added for the crew's breaks, existing shifts made
    // longer for them, or (BreakTime) a shift made longer so one break costs no
    // work. Nothing here matches an error pattern (ReviewChecks): they are notes
    // the day can be OK with.
    private static double Hhmm(string t) => Js.Number(t[..2]) * 60 + Js.Number(t[3..5]);

    private sealed class Extra
    {
        public int At;
        public string Role = "SA";
        public double Start;
        public double End;
        public string Closes = "";
        public int People;
        public double BreakH;
    }

    /// <summary>
    /// The planner's notes as they stand on the published shifts. An "Extra body"
    /// note is written when the shaper adds the shift and was never updated: it
    /// described shifts later relabelled as a junior's, shifts merged away, and
    /// "0.00h of award break" (review RC-29). Each is matched to the cover shift it
    /// describes and reworded from it, or dropped. Two people added at the same
    /// times read as one line ("2 extra store assistants ..."), not as a note
    /// written twice.
    /// </summary>
    public static List<string> PlannerNotes(IReadOnlyList<string> flags, IReadOnlyList<PlanShift> shifts)
    {
        var covers = shifts.Where(s => s.SlotId.StartsWith("sa-cover-", StringComparison.Ordinal)).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var out_ = new List<string>();
        // One line per role, times and window: how many people, and the most break
        // the shaper counted in the window (each note counts all of it so far).
        var extra = new JsMap<string, Extra>();
        foreach (var f in flags)
        {
            if (SupersededNotes.Any(p => p.IsMatch(f))) continue;
            var grown = CoverGrown.Match(f);
            if (grown.Success)
            {
                out_.Add($"Longer shifts for the crew's breaks: {grown.Groups[1].Value} h added to existing shifts, because breaks fall in work that has to be finished in its window");
                continue;
            }
            var till = TillCover.Match(f);
            if (till.Success)
            {
                out_.Add($"Longer shifts for the crew's breaks at the tills: {till.Groups[1].Value} h added so the tills stay manned while people take their breaks");
                continue;
            }
            var m = ExtraBody.Match(f);
            if (!m.Success)
            {
                out_.Add(f);
                continue;
            }
            var (from, to, breakH, closes) = (m.Groups[1].Value, m.Groups[2].Value, Js.Number(m.Groups[3].Value), m.Groups[4].Value);
            if (breakH <= 0) continue;
            var a = Hhmm(from);
            var b = Hhmm(to);
            double Overlap(PlanShift s) => Math.Min(b, s.End) - Math.Max(a, s.Start);
            var shift = Js.Sorted(covers.Where(s => !used.Contains(s.SlotId) && Overlap(s) > 0), (x, y) => Overlap(y) - Overlap(x))
                .FirstOrDefault();
            if (shift is null) continue;
            used.Add(shift.SlotId);
            var role = shift.PlanRole == RoleBadges.Jsa ? "JSA" : "SA";
            var key = $"{role} {Js.NumberToString(shift.Start)} {Js.NumberToString(shift.End)} {closes}";
            if (extra.TryGet(key, out var seen))
            {
                seen.People++;
                seen.BreakH = Js.Max(seen.BreakH, breakH);
                continue;
            }
            extra.Set(key, new Extra { At = out_.Count, Role = role, Start = shift.Start, End = shift.End, Closes = closes, People = 1, BreakH = breakH });
            out_.Add(""); // its place, filled in below
        }
        foreach (var e in extra.Values())
        {
            var (one, many) = e.Role == "JSA" ? ("junior", "juniors") : ("store assistant", "store assistants");
            var times = $"{Grid.MinToTime(e.Start)}–{Grid.MinToTime(e.End)}";
            out_[e.At] =
                (e.People == 1
                    ? $"Extra {one} for the crew's breaks: {times}"
                    : $"{e.People} extra {many} for the crew's breaks: {(e.People == 2 ? "both" : "all")} {times}") +
                $", because {Js.ToFixed(e.BreakH, 2)} h of breaks fall in work that has to be finished by {e.Closes}";
        }
        return out_;
    }

    /// <summary>
    /// Every painted slot as a plan shift, before its tiles are labelled: the
    /// break it is owed and its blocks in time order. Paint every slot's blocks
    /// from the (shaper-adjusted) demand curve, window-aware - the exact painter
    /// the roster uses (E6's PlanSlots.PaintSlots, or the day's held paint), so
    /// tiles always agree. Display-only Registers/SCO split of each Checkout tile:
    /// break tiles are already placed by the painter (till-safe positions, work
    /// made up elsewhere) - no midpoint overlay here.
    /// </summary>
    public static List<PlanShift> Unlabelled<S>(IReadOnlyList<S> paintable, Func<S, PlanSlot> slotOf) where S : PaintableShift
    {
        var resolved = Display.SplitCheckout(paintable.Select(p => p.Blocks), b => b.Group);
        return paintable.Select((p, pi) =>
        {
            var brk = Breaks.BreakForRole(p.Role, (p.End - p.Start) / 60);
            var blocks = Js.Sort(
                p.Blocks.Select((b, bi) => new DisplayBlock { Time = b.Time, Group = resolved[pi][bi] }).ToList(),
                (x, y) => x.Time - y.Time);
            var slot = slotOf(p);
            return new PlanShift
            {
                SlotId = slot.Id,
                PlanRole = slot.Role,
                Start = p.Start,
                End = p.End,
                BreakMinutes = brk.TotalMinutes,
                UnpaidBreakMinutes = brk.UnpaidMinutes,
                Blocks = blocks,
            };
        }).ToList();
    }

    /// <summary>
    /// The day's plan from its labelled shifts: the planner's notes on what it did,
    /// then the day checker's findings on the shifts as painted - cover, who does
    /// what, windows, lengths, tills, work on nobody's shift, blank time and the
    /// hours check (owner decisions 8, R2, R4; review RC-27, RC-29).
    /// </summary>
    /// <param name="shapedFlags">The shaper's flags (ShapedDay.Flags).</param>
    /// <param name="shifts">Unlabelled's shifts with their tiles labelled (labelPlanDay).</param>
    /// <param name="searchCapped">The painter's PaintResult.SearchCapped.</param>
    /// <param name="searchCapFlag">The painter's SEARCH_CAP_FLAG (E5's Painter).</param>
    public static PlanShiftComputation Finish(
        Stage1Computation stage1,
        IReadOnlyList<string> shapedFlags,
        List<PlanShift> shifts,
        bool searchCapped,
        string searchCapFlag,
        DayCheckOptions checkOptions)
    {
        var notes = PlannerNotes(shapedFlags, shifts);
        if (searchCapped) notes.Add(searchCapFlag);
        var check = DayChecker.CheckPlanDay(
            stage1,
            shifts.Select(s => s.ToChecked()).ToList(),
            checkOptions with { BreakTimeAdded = BreakTime.BreakTimeAdded(notes) });
        return new PlanShiftComputation
        {
            HasData = true,
            TradingOpen = stage1.TradingOpen,
            TradingClose = stage1.TradingClose,
            // Slots arrive in hierarchy order from the shaper (MGR opener, MGR
            // closer, SAs, JSAs) - preserved as-is.
            Shifts = shifts,
            Flags = [.. notes, .. check.Flags],
            Notes = notes,
            Checks = check.Flags,
            Hours = check.Hours,
        };
    }
}
