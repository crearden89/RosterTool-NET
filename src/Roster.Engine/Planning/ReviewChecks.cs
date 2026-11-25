// Port of src/lib/review/checks.ts.
// The fortnight review's checklist: every flag the plan and roster raise for a
// day (or for the fortnight), each one an ERROR the store must explain or a
// WARNING it only has to look at. Pure - shared by the review page, its server
// actions and the tests.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Roster.Engine.Core;

namespace Roster.Engine.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("warning")] Warning,
}

/// <param name="Key">
/// Stable across a regenerate: the day plus the flag's own wording, so the
/// same flag keeps its check and a changed one (new hours, new times) needs
/// checking again.
/// </param>
/// <param name="Date">null = a fortnight-level flag (e.g. under contract).</param>
/// <param name="HardStop">
/// A legal breach a note can't clear (owner decision R3): fixed by an edit
/// or a re-plan, or the plan can't be submitted.
/// </param>
/// <param name="Detail">The engine's "Why:" explanation, when it gave one.</param>
public sealed record ReviewCheck(string Key, DateOnly? Date, Severity Severity, bool HardStop, string Message, string? Detail);

public static class ReviewChecks
{
    // Plan and roster changes inside this many hours of the benchmark are fine -
    // the same ±1.00 h the Plan page's day variance uses.
    public const double HoursTolerance = 1;

    // The TypeScript patterns, case-insensitive (/i). In .NET [0-9] stands for
    // JavaScript's \d (ASCII digits only) and \z for $ (which in .NET would also
    // match before a final newline).
    private static Regex P(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Wording the engines use when work or cover is MISSING: someone has to act,
    // or say why it's acceptable. Everything else is advisory.
    private static readonly Regex[] ErrorPatterns =
    [
        P("external cover may be required"),
        P("could not be (placed|planned|moved)"),
        P("cannot be planned"),
        P("min not planned"),
        P("on nobody's shift"),
        P("has nobody on it"),
        P("^Manager gap"),
        P("^Only 1 person planned"),
        P("^No tills configured"),
        P("outside the store's load running"),
        // The day checker (lib/plan/dayCheck.ts): a rule broken on the finished
        // shifts - cover (F1-F3), who may do each task, windows, shift lengths,
        // junior hours, Monday's date checks, and an hours gap no decision allows
        // (owner decisions 8, R2, R4).
        P("^No manager rostered"),
        P("^Fewer than [0-9]+ people rostered"),
        P("^Junior rostered with fewer than"),
        P("only managers may do Admin"),
        P("juniors may not work a till"),
        P("is outside its window"),
        P("is under the [0-9.]+-hour minimum"),
        P("is longer than the [0-9.]+-hour maximum"),
        P("works outside a junior's"),
        P("starts before 16:00 on a school day"),
        // "Monday date checks (with Register Assignment): ..." names the tasks it
        // shares its window with - an error too.
        P(@"^Monday date checks( \([^)]*\))?:"),
        P("^Planned hours are [0-9.]+ h (under|over) the benchmark"),
        // Rules the shift cutter and painter now keep (phases 6 and 7): the
        // handover and the morning Cleaning hour.
        P("^Managers do not overlap"),
        P("^Manager handover is [0-9]+ minutes"),
        P(@"is worked in its hour\z"),
        P("^No till manned while the store is trading"),
    ];

    // The hard stops (owner decision R3; review RC-16): someone alone (F1), a
    // junior with fewer than three adults (F3), a trading quarter-hour with no
    // till. No manager on (F2) is an error that still clears with a note.
    private static readonly Regex[] HardStopPatterns =
    [
        P("^Fewer than [0-9]+ people rostered"),
        P("^Only 1 person planned"),
        P("^Junior rostered with fewer than"),
        P("^No till manned while the store is trading"),
    ];

    private static readonly Regex ExternalCover = P("external cover may be required");

    public static bool IsHardStop(string flag) => HardStopPatterns.Any(p => p.IsMatch(flag));

    public static Severity SeverityOf(string flag) => ErrorPatterns.Any(p => p.IsMatch(flag)) ? Severity.Error : Severity.Warning;

    // FNV-1a: short, stable and dependency-free. Collisions only matter within
    // one store-fortnight's few dozen flags, where 32 bits is ample.
    private static string Fnv1a(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var c in s)
        {
            h ^= c;
            h = unchecked(h * 0x01000193u);
        }
        return h.ToString("x8");
    }

    public static string CheckKey(DateOnly? date, string message) =>
        $"{date?.ToIso() ?? "fortnight"}:{Fnv1a(Js.Trim(message))}";

    // /^Why:\s*/
    private static bool IsWhy(string f) => f.StartsWith("Why:", StringComparison.Ordinal);

    /// <summary>
    /// A day's raw flags as checks. The roster adds one "Why: ..." line under its
    /// open shifts; it becomes the explanation on each of them rather than a check
    /// of its own. Repeats (the plan and roster can raise the same flag) collapse.
    /// </summary>
    public static List<ReviewCheck> FlagsToChecks(DateOnly? date, IEnumerable<string> flags)
    {
        var list = flags.ToList();
        var whyFlag = list.FirstOrDefault(IsWhy);
        var why = whyFlag is null ? null : Js.Trim(whyFlag[4..]);
        if (why == "") why = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var out_ = new List<ReviewCheck>();
        foreach (var raw in list)
        {
            var message = Js.Trim(raw);
            if (message.Length == 0 || IsWhy(message) || !seen.Add(message)) continue;
            out_.Add(new ReviewCheck(
                CheckKey(date, message),
                date,
                SeverityOf(message),
                IsHardStop(message),
                message,
                ExternalCover.IsMatch(message) ? why : null));
        }
        return out_;
    }

    // The day's hours and its work outside a window are checked by the day
    // checker (lib/plan/dayCheck.ts) and arrive here among the plan's flags - one
    // calculation for the status, the Plan page and the Review (decision R2).

    /// <summary>Errors first, then warnings, keeping each group's own order.</summary>
    public static List<ReviewCheck> SortChecks(IEnumerable<ReviewCheck> checks)
    {
        var all = checks.ToList();
        return [.. all.Where(c => c.Severity == Severity.Error), .. all.Where(c => c.Severity == Severity.Warning)];
    }
}
