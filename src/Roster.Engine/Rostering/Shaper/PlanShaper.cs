// Port of src/lib/stage2/shaper.ts (its types are ShaperTypes.cs).
// ─── Plan Shaper: demand curve → the day's ideal shift slots ─────────────────
// Implements the interview model (July 2026) with the build order of lean
// retail scheduling (Ernst et al.'s rostering decomposition: demand modelling →
// shift design → task assignment): the PLAN allocates EXACTLY the benchmark
// workload - never padded for contracts (that mismatch is the roster's job to
// surface). Its output is a set of typed slots (MGR / SA / JSA) the roster then
// fills with real names.
//
// Hard rules encoded here, applied in this order:
//  1. LEVELLING: checkout is pinned to its blocks (a till is manned in its
//     block or not at all), but every other labour group is movable within its
//     legal window - so its hours are water-filled across that window to make
//     the total curve wide, flat steps instead of jagged towers. Wide steps cut
//     into long efficient shifts with almost no padding; that is what keeps
//     the day inside its benchmark allowance ("schedule workload into
//     capacity, don't trace a jagged curve").
//  2. MANAGERS FIRST: exactly TWO manager slots per day - one opener (from the
//     start of demand) and one closer (to the end of demand), with a MANDATORY
//     handover overlap (~1h). Manager hours come OUT of the benchmark budget.
//  3. The remaining demand is cut into SA slots that hug the levelled curve,
//     preferring the cost-efficient shift lengths (interview):
//       Tier 1: 9.5, 9.25, 9, 6, 5.75 h    Tier 2: 8-8.75, 5-5.5, 4 h
//     Other lengths are allowed when the contour needs them; 3h is the hard
//     minimum. The plan never over-covers just to land on a nice length.
//  4. ≥2 people on the floor at all times (safety). Pre-open work (Load
//     Running) is NOT bound to 06:00 - a small pre-open workload is compressed
//     to a later start so ≥2 people start together and finish by trading-open.
//  5. Slots that legally fit a junior (window, length, ≥3 other adults in
//     every block) are labelled JSA - the cheapest way to staff them.
//  6. Output in hierarchy order: MGR opener, MGR closer, SAs, JSAs.
//
// Break placement lives in the task painter (engine.ts) under award timing
// rules - the shaper only grows each slot's clock by its break entitlement so
// tiles still deliver the full benchmark workload.

using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Engine.Rostering.Shaper;

public static class PlanShaper
{
    private const double B = Grid.BlockMin;

    private static double ToBlocks(double hours) => Js.Round(hours * (60 / B));

    // Active rostering rules (handover overlap bounds, the everyone-on-floor
    // safety minimum, adults-with-junior floor, junior window). Values live in
    // RosteringRules and may be overridden via the Rostering Rules admin page;
    // the shaper is fully synchronous, so setting this at each exported entry
    // point (ShapePlan, LabelJsaSlots) is safe and saves threading it through
    // every closure below. Per thread: the TypeScript module variable lived in
    // a single-threaded process, and two requests shaping at once must not see
    // each other's rules.
    [ThreadStatic] private static RosteringRules? _rules;

    private static RosteringRules R => _rules ?? RosteringRules.Default;

    // ─── Preferred shift lengths ────────────────────────────────────────────────
    // Derived, not listed. A shift is paid for its clock less any unpaid break and
    // delivers its clock less the whole break, so the price of an hour of real work
    // depends on the length - and the ranking falls straight out of the award's
    // break tiers (ShiftLengthCost). The wage cancels, so one ranking serves
    // every store and every grade, and it stays correct on its own when the break
    // rules are edited.
    //
    // The shape of it, at the client's $43.23: 9.5h is the cheapest hour of work on
    // the board at $45.77, 6.00h the cheapest short shift at $45.82, and 6.25h the
    // single worst at $47.35 - a quarter hour longer than six, over the tier, so
    // the entitlement doubles and the shift delivers LESS work for MORE money.
    //
    // Kept as a small integer badness because that is what the tie-breaks below
    // want; 0 is the best length available to a Store Assistant.
    private const double SaMinMin = 180;
    private const double SaMaxMin = 570; // 9.5h clock - the longest a tiered role may work
    private const double MgrMaxMin = 750; // 12.5h clock - the longest a flat-break manager may work

    public static double LengthBadness(double lenMin, string type = BreakTypes.Tiered) =>
        type == BreakTypes.Tiered
            ? ShiftLengthCost.LengthRank(type, lenMin, SaMinMin, SaMaxMin)
            // A flat unpaid half hour has no tier to fall off, so a manager's cost
            // per worked hour only ever improves with length - rank plainly by it.
            : ShiftLengthCost.LengthRank(type, lenMin, SaMinMin, MgrMaxMin);

    // ─── The shaper ──────────────────────────────────────────────────────────────
    public static ShapedDay ShapePlan(ShaperInput input) => throw new NotImplementedException();

    /// <summary>
    /// JSA labelling: which SA slots a junior may legally be given. Latest-starting
    /// first (evening shifts are the classic junior fill), when the slot fits the
    /// JSA window and length caps and every block keeps >=3 OTHER adults on the
    /// floor. Public so the audit harness applies the SAME rule to a frozen plan
    /// rather than a second copy of it drifting out of step.
    /// </summary>
    /// <param name="keep">
    /// Asked after each re-label; false puts the slot back. ShapePlan uses it
    /// to keep the tills and break cover it bought: a junior cannot work a
    /// till, and a slot added so the tills stay manned through the breaks was
    /// being handed to a junior - who then could not take the till.
    /// </param>
    public static void LabelJsaSlots(
        List<PlanSlot> slots,
        bool isSchoolDay,
        ShaperRule jsaRule,
        IReadOnlyDictionary<string, double>? rules = null,
        Func<bool>? keep = null)
    {
        // Direct callers (audit harness, planShifts) resolve their own rule set; the
        // internal call from ShapePlan passes nothing and inherits the rules it just
        // set - resolving null would wrongly reset an override run to defaults, so
        // only re-resolve when a rules argument is actually given.
        if (rules is not null) _rules = RosteringRules.Resolve(rules);
        var r = R;
        var j = RosteringRules.JsaWindowFor(r, isSchoolDay);
        var jsaMax = Math.Min(j.MaxLenMin, Breaks.MaxClockMinutes(jsaRule.BreakType ?? BreakTypes.Tiered, jsaRule.MaxShiftHours));
        var jsaMin = Math.Max(180, ToBlocks(jsaRule.MinShiftHours) * B);
        // A reserved fixed shift is already assigned to a named person - never
        // re-label it for a junior.
        var saSlots = Js.Sorted(
            slots.Where(s => s.Role == SlotRoles.SA && !s.Id.StartsWith("fixed-", StringComparison.Ordinal)),
            (a, b) => b.Start - a.Start);
        var jsaN = 0;
        foreach (var s in saSlots)
        {
            var len = s.End - s.Start;
            if (len < jsaMin || len > jsaMax) continue;
            if (s.Start < j.Start || s.End > j.Latest) continue;
            var ok = true;
            for (var m = s.Start; m < s.End && ok; m += B)
            {
                // Adults present besides this slot (managers + other adult slots).
                double adults = 0;
                foreach (var o in slots)
                    if (!ReferenceEquals(o, s) && o.Role != SlotRoles.JSA && o.Start <= m && m < o.End) adults++;
                if (adults < r.MinAdultsWithJsa) ok = false;
            }
            if (!ok) continue;
            s.Role = SlotRoles.JSA;
            if (keep is not null && !keep())
            {
                s.Role = SlotRoles.SA;
                continue;
            }
            s.Id = $"jsa-{++jsaN}";
        }
    }
}
