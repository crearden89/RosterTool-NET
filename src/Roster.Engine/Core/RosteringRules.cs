// Port of src/lib/stage2/rules.ts.
// ─── Rostering rules: every tunable constraint the engines obey ──────────────
// Pure (shared by engines, tests and the admin page). The values here are the
// shipped defaults; the rostering_rules table stores OVERRIDES only, keyed by
// the RosteringRules field name. Resolve() merges them, and the engines receive
// the result at generate time - deleting an override on the admin page
// therefore restores the default, never leaves a hole.
//
// kind = "legal" marks rules that encode legislation or the retail award
// (rest gaps, junior windows and caps, the safety floor); changing those can
// put a roster in breach - the admin page carries a standing disclaimer.
// "policy" rules are business practice and safe to tune.

namespace Roster.Engine.Core;

public sealed record RosteringRules
{
    // Shift geometry
    public double MinPieceMin { get; init; } = 180; // hard minimum length of any shift or split piece (minutes)
    // Cross-day / weekly limits
    public double RestGapMin { get; init; } = 11 * 60; // minutes between one day's shift end and the next day's start
    public double MaxConsecDays { get; init; } = 6; // max consecutive worked days
    // Separate consecutive-day cap for the SALARIED managers (SM/ASM), who are
    // already exempt from the 11-hour rest gap. 14 = the whole fortnight, i.e.
    // no restriction: they work as many days in a row as the store needs.
    public double MaxConsecDaysSalaried { get; init; } = 14;
    // Advisory only: warn when anyone's run of worked days exceeds this. It
    // never blocks a roster - it exists because the salaried pair have no hard
    // run cap, so a very long run is legal but worth a manager's eye. 0 = off.
    public double LongRunWarnDays { get; init; } = 10;
    public double MaxWorkedPerWeek { get; init; } = 5; // max worked days per Mon-Sun week
    // Floor safety
    public double MinPeopleOnFloor { get; init; } = 2; // everyone-on-floor minimum, always
    public double MinAdultsWithJsa { get; init; } = 3; // adults present in every block a junior works
    // Junior (JSA) legality
    public double JsaEarliestMin { get; init; } = 6 * 60; // absolute earliest start (minutes from midnight)
    public double JsaLatestMin { get; init; } = 21 * 60; // absolute latest finish
    public double JsaSchoolDayStartMin { get; init; } = 16 * 60; // earliest start on a school day (close shifts only)
    public double JsaTermMaxShiftH { get; init; } = 5; // longest single shift in a school-term week (hours)
    public double JsaHolidayMaxShiftH { get; init; } = 6; // longest single shift in a holiday week (hours)
    public double JsaTermWeekCapH { get; init; } = 15; // weekly hours cap in a school-term week
    public double JsaHolidayWeekCapH { get; init; } = 30; // weekly hours cap in a holiday week
    // Manager pair
    public double OverlapMinMin { get; init; } = 60; // minimum opener/closer handover overlap (minutes)
    // No longer a cap (owner decision R7: the handover is at least an hour,
    // with no upper limit). Kept so a stored override still parses.
    public double OverlapMaxMin { get; init; } = 120;
    public double ManagerMinShiftMin { get; init; } = 6 * 60; // shortest manager shift, clock minutes (R7)
    // How far past its window the load may run for the crew's staggered breaks
    // (owner decision 4), minutes.
    public double LoadBreakStretchMin { get; init; } = DefaultLoadBreakStretchMin;
    // Shift time the planner may add in a day so breaks don't cost work (master
    // plan decision 4.2), minutes; 0 turns it off.
    public double BreakTimeAddMaxMin { get; init; } = 60;
    // Values that were fixed in code (owner decision R15).
    public double CloseDownMin { get; init; } = 30; // the close-down after close, every day (decision 2)
    public double LrEarliestStartMin { get; init; } = 6 * 60; // the load's earliest start (a large load)
    public double LrLatestStartMin { get; init; } = 7 * 60; // the load's latest start (a small load)
    public double LrFinishQuietMin { get; init; } = 10 * 60; // the load's finish on a quiet first hour
    public double LrFinishSteadyMin { get; init; } = 9 * 60 + 30; // ...on a steady one
    public double LrFinishBusyMin { get; init; } = 9 * 60; // ...on a busy one
    public double BorrowMorningEndMin { get; init; } = 10 * 60; // the checkout borrow: Load Running before this
    public double BorrowEveningFromMin { get; init; } = 18 * 60; // ...Cleaning from this; Reset and Admin between
    public double AdminHeadcount { get; init; } = 1; // managers on Admin at once (decision 7)
    public double AdminHeadcountHandover { get; init; } = 2; // ...inside the handover
    // Sub-task shares, percent of the group's tiles (SubTasks; decision 4.4 of
    // the master plan). Labels only: they never change what is planned.
    public double SubCleaningFrontEndPct { get; init; } = 25;
    public double SubCleaningShopFloorPct { get; init; } = 50;
    public double SubCleaningBohPct { get; init; } = 25;
    public double SubSpecialsBuildingPct { get; init; } = 50;
    public double SubSpecialsTidyPct { get; init; } = 20;
    public double SubSpecialsRunningPct { get; init; } = 30;
    // Load Running's split when the load board has no history for the weekday.
    public double SubLoadProducePct { get; init; } = 12;
    public double SubLoadLongLifePct { get; init; } = 20;
    public double SubLoadFreezerPct { get; init; } = 12;
    public double SubLoadAmbientPct { get; init; } = 36;
    public double SubLoadBreadPct { get; init; } = 5;
    public double SubLoadShortLifePct { get; init; } = 10;
    public double SubLoadFreezeToThawPct { get; init; } = 5;

    // Off by default: measured on the review's sample, letting the crew stay on
    // past the load's window instead of adding a body lost the load on 60 of 264
    // days - the tills take the crew as the doors open (see the decisions doc).
    public const double DefaultLoadBreakStretchMin = 0;

    public static readonly RosteringRules Default = new();

    /// <summary>Every rule, by its TypeScript key, in the order DEFAULT_RULES lists them.</summary>
    public static readonly IReadOnlyList<(string Key, Func<RosteringRules, double> Get, Func<RosteringRules, double, RosteringRules> With)> Fields =
    [
        ("minPieceMin", r => r.MinPieceMin, (r, v) => r with { MinPieceMin = v }),
        ("restGapMin", r => r.RestGapMin, (r, v) => r with { RestGapMin = v }),
        ("maxConsecDays", r => r.MaxConsecDays, (r, v) => r with { MaxConsecDays = v }),
        ("maxConsecDaysSalaried", r => r.MaxConsecDaysSalaried, (r, v) => r with { MaxConsecDaysSalaried = v }),
        ("longRunWarnDays", r => r.LongRunWarnDays, (r, v) => r with { LongRunWarnDays = v }),
        ("maxWorkedPerWeek", r => r.MaxWorkedPerWeek, (r, v) => r with { MaxWorkedPerWeek = v }),
        ("minPeopleOnFloor", r => r.MinPeopleOnFloor, (r, v) => r with { MinPeopleOnFloor = v }),
        ("minAdultsWithJsa", r => r.MinAdultsWithJsa, (r, v) => r with { MinAdultsWithJsa = v }),
        ("jsaEarliestMin", r => r.JsaEarliestMin, (r, v) => r with { JsaEarliestMin = v }),
        ("jsaLatestMin", r => r.JsaLatestMin, (r, v) => r with { JsaLatestMin = v }),
        ("jsaSchoolDayStartMin", r => r.JsaSchoolDayStartMin, (r, v) => r with { JsaSchoolDayStartMin = v }),
        ("jsaTermMaxShiftH", r => r.JsaTermMaxShiftH, (r, v) => r with { JsaTermMaxShiftH = v }),
        ("jsaHolidayMaxShiftH", r => r.JsaHolidayMaxShiftH, (r, v) => r with { JsaHolidayMaxShiftH = v }),
        ("jsaTermWeekCapH", r => r.JsaTermWeekCapH, (r, v) => r with { JsaTermWeekCapH = v }),
        ("jsaHolidayWeekCapH", r => r.JsaHolidayWeekCapH, (r, v) => r with { JsaHolidayWeekCapH = v }),
        ("overlapMinMin", r => r.OverlapMinMin, (r, v) => r with { OverlapMinMin = v }),
        ("overlapMaxMin", r => r.OverlapMaxMin, (r, v) => r with { OverlapMaxMin = v }),
        ("managerMinShiftMin", r => r.ManagerMinShiftMin, (r, v) => r with { ManagerMinShiftMin = v }),
        ("loadBreakStretchMin", r => r.LoadBreakStretchMin, (r, v) => r with { LoadBreakStretchMin = v }),
        ("breakTimeAddMaxMin", r => r.BreakTimeAddMaxMin, (r, v) => r with { BreakTimeAddMaxMin = v }),
        ("closeDownMin", r => r.CloseDownMin, (r, v) => r with { CloseDownMin = v }),
        ("lrEarliestStartMin", r => r.LrEarliestStartMin, (r, v) => r with { LrEarliestStartMin = v }),
        ("lrLatestStartMin", r => r.LrLatestStartMin, (r, v) => r with { LrLatestStartMin = v }),
        ("lrFinishQuietMin", r => r.LrFinishQuietMin, (r, v) => r with { LrFinishQuietMin = v }),
        ("lrFinishSteadyMin", r => r.LrFinishSteadyMin, (r, v) => r with { LrFinishSteadyMin = v }),
        ("lrFinishBusyMin", r => r.LrFinishBusyMin, (r, v) => r with { LrFinishBusyMin = v }),
        ("borrowMorningEndMin", r => r.BorrowMorningEndMin, (r, v) => r with { BorrowMorningEndMin = v }),
        ("borrowEveningFromMin", r => r.BorrowEveningFromMin, (r, v) => r with { BorrowEveningFromMin = v }),
        ("adminHeadcount", r => r.AdminHeadcount, (r, v) => r with { AdminHeadcount = v }),
        ("adminHeadcountHandover", r => r.AdminHeadcountHandover, (r, v) => r with { AdminHeadcountHandover = v }),
        ("subCleaningFrontEndPct", r => r.SubCleaningFrontEndPct, (r, v) => r with { SubCleaningFrontEndPct = v }),
        ("subCleaningShopFloorPct", r => r.SubCleaningShopFloorPct, (r, v) => r with { SubCleaningShopFloorPct = v }),
        ("subCleaningBohPct", r => r.SubCleaningBohPct, (r, v) => r with { SubCleaningBohPct = v }),
        ("subSpecialsBuildingPct", r => r.SubSpecialsBuildingPct, (r, v) => r with { SubSpecialsBuildingPct = v }),
        ("subSpecialsTidyPct", r => r.SubSpecialsTidyPct, (r, v) => r with { SubSpecialsTidyPct = v }),
        ("subSpecialsRunningPct", r => r.SubSpecialsRunningPct, (r, v) => r with { SubSpecialsRunningPct = v }),
        ("subLoadProducePct", r => r.SubLoadProducePct, (r, v) => r with { SubLoadProducePct = v }),
        ("subLoadLongLifePct", r => r.SubLoadLongLifePct, (r, v) => r with { SubLoadLongLifePct = v }),
        ("subLoadFreezerPct", r => r.SubLoadFreezerPct, (r, v) => r with { SubLoadFreezerPct = v }),
        ("subLoadAmbientPct", r => r.SubLoadAmbientPct, (r, v) => r with { SubLoadAmbientPct = v }),
        ("subLoadBreadPct", r => r.SubLoadBreadPct, (r, v) => r with { SubLoadBreadPct = v }),
        ("subLoadShortLifePct", r => r.SubLoadShortLifePct, (r, v) => r with { SubLoadShortLifePct = v }),
        ("subLoadFreezeToThawPct", r => r.SubLoadFreezeToThawPct, (r, v) => r with { SubLoadFreezeToThawPct = v }),
    ];

    private static readonly IReadOnlyDictionary<string, Func<RosteringRules, double>> Getters =
        Fields.ToDictionary(f => f.Key, f => f.Get);

    /// <summary>A rule's value by its TypeScript key ("minPeopleOnFloor"); null for an unknown key.</summary>
    public double? Get(string key) => Getters.TryGetValue(key, out var g) ? g(this) : null;

    /// <summary>
    /// resolveRules: the defaults with every finite override for a known key
    /// applied. Null overrides give the defaults themselves.
    /// </summary>
    public static RosteringRules Resolve(IReadOnlyDictionary<string, double>? overrides)
    {
        if (overrides is null) return Default;
        var out_ = Default;
        foreach (var (key, _, with) in Fields)
            if (overrides.TryGetValue(key, out var v) && double.IsFinite(v)) out_ = with(out_, v);
        return out_;
    }

    // The junior legal window under a given rule set.
    public static JsaWindow JsaWindowFor(RosteringRules rules, bool isSchoolDay) => new(
        isSchoolDay ? Math.Max(rules.JsaEarliestMin, rules.JsaSchoolDayStartMin) : rules.JsaEarliestMin,
        rules.JsaLatestMin,
        (isSchoolDay ? rules.JsaTermMaxShiftH : rules.JsaHolidayMaxShiftH) * 60);
}

public sealed record JsaWindow(double Start, double Latest, double MaxLenMin);

// ─── Admin-page metadata ─────────────────────────────────────────────────────

/// <summary>unit: minutes, hours, days, people, time or percent; kind: legal or policy.</summary>
public sealed record RuleMeta(string Key, string Label, string Description, string Unit, string Kind, double Min, double Max, string Category);

public static class RuleMetas
{
    public static readonly IReadOnlyList<RuleMeta> All = Build();

    private static List<RuleMeta> Build()
    {
        var list = new List<RuleMeta>
        {
            new("minPieceMin", "Minimum shift length", "The shortest shift (or split piece) anyone may be rostered.", "minutes", "legal", 60, 480, "Shift limits"),
            new("restGapMin", "Rest gap between days", "Minimum time between one day's clock-off and the next day's clock-on. Also what makes a close-then-open ('clopen') illegal.", "minutes", "legal", 0, 16 * 60, "Weekly limits"),
            new("maxConsecDays", "Max consecutive days", "The longest run of worked days across the fortnight, for everyone except the salaried SM/ASM.", "days", "legal", 1, 14, "Weekly limits"),
            new("maxConsecDaysSalaried", "Max consecutive days (SM/ASM)", "Separate run limit for the salaried managers, who are also exempt from the rest gap. 14 = the whole fortnight, i.e. no restriction.", "days", "legal", 1, 14, "Weekly limits"),
            new("longRunWarnDays", "Long-run warning threshold", "Raise a roster warning when anyone works more than this many days in a row. Advisory only - it never blocks a roster. Set 0 to switch the warning off.", "days", "policy", 0, 14, "Weekly limits"),
            new("maxWorkedPerWeek", "Max worked days per week", "Days worked in a Mon-Sun week - 5 guarantees at least two days off every week.", "days", "policy", 1, 7, "Weekly limits"),
            new("minPeopleOnFloor", "Minimum people on the floor", "Lone-worker safety floor - never fewer than this many people at once.", "people", "legal", 1, 5, "Floor safety"),
            new("minAdultsWithJsa", "Adults required with a junior", "Adults (SA or managers) present in every block a junior works.", "people", "legal", 1, 5, "Floor safety"),
            new("jsaEarliestMin", "Junior earliest start", "Absolute earliest a junior may start, school day or not.", "time", "legal", 0, 24 * 60, "Juniors (JSA)"),
            new("jsaLatestMin", "Junior latest finish", "Absolute latest a junior may work to.", "time", "legal", 0, 24 * 60, "Juniors (JSA)"),
            new("jsaSchoolDayStartMin", "Junior school-day start", "On a school day a junior may only work a close shift starting from this time.", "time", "legal", 0, 24 * 60, "Juniors (JSA)"),
            new("jsaTermMaxShiftH", "Junior max shift (school day)", "Longest single junior shift on a school day. A weekend in a term week allows the holiday maximum.", "hours", "legal", 1, 12, "Juniors (JSA)"),
            new("jsaHolidayMaxShiftH", "Junior max shift (holidays)", "Longest single junior shift in a school-holiday week.", "hours", "legal", 1, 12, "Juniors (JSA)"),
            new("jsaTermWeekCapH", "Junior weekly cap (term)", "Total junior hours allowed in a school-term week.", "hours", "legal", 1, 60, "Juniors (JSA)"),
            new("jsaHolidayWeekCapH", "Junior weekly cap (holidays)", "Total junior hours allowed in a school-holiday week.", "hours", "legal", 1, 60, "Juniors (JSA)"),
            new("overlapMinMin", "Manager handover minimum", "Minimum overlap between the opener and closer manager shifts - a rule: trimming never cuts it.", "minutes", "policy", 0, 4 * 60, "Manager pair"),
        };
        (string Key, string Label, string Description, string Unit, double Min, double Max)[] plan =
        [
            ("closeDownMin", "Close-down after close", "How long the store is staffed after it closes, every day: End of Day, UKG End of Day and the last of the cleaning.", "minutes", 0, 120),
            ("lrEarliestStartMin", "Load earliest start", "Where a large load starts (the Load Running start rule).", "time", 4 * 60, 9 * 60),
            ("lrLatestStartMin", "Load latest start", "Where a small load starts.", "time", 4 * 60, 10 * 60),
            ("lrFinishQuietMin", "Load finish, quiet first hour", "How late load may run when the first hour of trade is quiet. A store opening after 08:30 runs it later by the same amount.", "time", 7 * 60, 13 * 60),
            ("lrFinishSteadyMin", "Load finish, steady first hour", "...when the first hour is steady.", "time", 7 * 60, 13 * 60),
            ("lrFinishBusyMin", "Load finish, busy first hour", "...when the first hour is busy.", "time", 7 * 60, 13 * 60),
            ("borrowMorningEndMin", "Checkout borrow: morning band ends", "Till rounding before this is funded from Load Running.", "time", 6 * 60, 14 * 60),
            ("borrowEveningFromMin", "Checkout borrow: evening band starts", "Till rounding from this is funded from Cleaning; between the bands, from Reset and Admin.", "time", 12 * 60, 22 * 60),
            ("adminHeadcount", "Managers on Admin at once", "How many managers Stage 1 plans on Admin at one time.", "people", 1, 3),
            ("adminHeadcountHandover", "Managers on Admin in the handover", "How many, inside the managers' handover, when a job has no room otherwise.", "people", 1, 3),
        ];
        foreach (var p in plan) list.Add(new(p.Key, p.Label, p.Description, p.Unit, "policy", p.Min, p.Max, "Plan settings"));
        list.Add(new("loadBreakStretchMin", "Load runs on for breaks", "Load runners take their breaks one at a time. Above 0, the load may run this far past its window to make up what the breaks displace, before an extra shift is added. 0 (the default) adds the shift.", "minutes", "policy", 0, 60, "Shift limits"));
        (string Key, string Label, string Description)[] shares =
        [
            ("subCleaningFrontEndPct", "Cleaning: front end", "Share of the day's Cleaning tiles labelled Front end (FE). The owner's split is 25 / 50 / 25. The three Cleaning shares are read as parts of a whole: if they don't add up to 100 they are scaled to it. Labels only - they never change the hours planned."),
            ("subCleaningShopFloorPct", "Cleaning: shop floor", "Share labelled Shop floor (SF)."),
            ("subCleaningBohPct", "Cleaning: BOH", "Share labelled BOH (BH), the back of house."),
            ("subSpecialsBuildingPct", "Specials: building", "Share of the day's Specials tiles labelled Building (BU), on build days only (Tuesday and Friday). On other days nothing is labelled Building and Specials is split between Tidy and Running in the ratio of their two shares. The owner's split is 50 / 20 / 30."),
            ("subSpecialsTidyPct", "Specials: tidy", "Share labelled Tidy (TD)."),
            ("subSpecialsRunningPct", "Specials: running", "Share labelled Running (RN)."),
            ("subLoadProducePct", "Load Running: produce", "Used only when the load board has no pallet counts for that weekday in the eight weeks before the fortnight: share of Load Running labelled Produce (PR). Otherwise the split follows what the load board says arrives on an average day (pallets times minutes per pallet)."),
            ("subLoadLongLifePct", "Load Running: long life", "...labelled Long Life (LL), when there is no load history."),
            ("subLoadFreezerPct", "Load Running: freezer", "...labelled Freezer (FZ), when there is no load history."),
            ("subLoadAmbientPct", "Load Running: ambient", "...labelled Ambient (AM), when there is no load history."),
            ("subLoadBreadPct", "Load Running: bread", "...labelled Bread (BD), when there is no load history."),
            ("subLoadShortLifePct", "Load Running: short life", "...labelled Short Life (SL), when there is no load history."),
            ("subLoadFreezeToThawPct", "Load Running: freeze to thaw", "...labelled Freeze to Thaw (FT), when there is no load history."),
        ];
        foreach (var s in shares) list.Add(new(s.Key, s.Label, s.Description, "percent", "policy", 0, 100, "Sub-task shares"));
        list.Add(new("breakTimeAddMaxMin", "Time added so breaks don't cost work", "When someone's break would leave work on nobody's shift, the planner may keep a shift on a little longer (or start it a little earlier) so the work is still done inside its window. This is the most it may add in a day; the day shows how much it added, as a warning. 0 turns it off.", "minutes", "policy", 0, 120, "Shift limits"));
        list.Add(new("managerMinShiftMin", "Manager shift minimum", "The shortest shift a manager is planned. The handover has no upper limit.", "minutes", "policy", 3 * 60, 12 * 60, "Manager pair"));
        return list;
    }
}
