// Port of src/lib/plan/dayCheck.ts.
// ─── The day checker ─────────────────────────────────────────────────────────
// One check of a planned day, read from the shifts as they stand - when the
// plan is generated, after every edit, on the Plan page and in the Review
// (owner decisions 8, R2 and R4; build phase 4). The planner used to write its
// flags as it went, and some went stale by the end: "0.75h planned above the
// curve" for time it later trimmed, "lost to breaks" for work no break touched
// (review RC-29). An edit changed nothing at all (RC-15). Everything here is
// worked out from the finished shifts, so a flag can't outlive its cause.
//
// Pure: shared by the server (snapshots, edits, Review) and the Plan page.
// Wording decides severity - see ErrorPatterns in ReviewChecks. A rule broken
// is an error; an allowed overage or a rule a later build phase enforces is a
// warning.

using System.Text.Json.Serialization;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Planning.Plan;

public sealed record DayCheckOptions
{
    /// <summary>The floor-cover rules (Rostering rules page).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoverRules? Cover { get; init; }

    /// <summary>Longest legal shift by the clock, minutes, per slot type ("MGR", "SA", "JSA").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double>? MaxClock { get; init; }

    /// <summary>Shortest legal shift, minutes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinShiftMin { get; init; }

    /// <summary>Shortest manager shift, minutes (owner decision R7).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ManagerMinShiftMin { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSchoolDay { get; init; }

    /// <summary>
    /// Hours the planner added so breaks don't cost work (master plan decision
    /// 4.2), from its note on the day: counted as explained in the hours check.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BreakTimeAdded { get; init; }
}

/// <param name="LostAt">The quarter-hours of work on nobody's shift, tills included, one per unit.</param>
public sealed record DayCheck(List<string> Flags, HoursCheck Hours, List<double> LostAt);

/// <summary>dayCheck.ts's functions (the result type keeps the TypeScript name DayCheck).</summary>
public static class DayChecker
{
    public static readonly IReadOnlyDictionary<string, string> GroupLabel = new Dictionary<string, string>
    {
        ["checkout"] = "Tills",
        ["load_running"] = "Load running",
        ["reset"] = "Reset",
        ["admin"] = "Admin",
        ["specials"] = "Specials",
        ["cleaning"] = "Cleaning",
        ["non_op"] = "Non-operational hours",
    };

    /// <summary>Every finding on the day's shifts as they stand, and its hours check.</summary>
    public static DayCheck CheckPlanDay(Stage1Computation s1, IReadOnlyList<CheckedShift> shifts, DayCheckOptions? opts = null) =>
        throw new NotImplementedException("CheckPlanDay: the body lands with E5's Coverage.CoverFlags.");

    /// <summary>OK (decision 8): no error from any planning step, the hours check included.</summary>
    public static bool DayIsOk(IEnumerable<string> flags) => !flags.Any(f => ReviewChecks.SeverityOf(f) == Severity.Error);
}
