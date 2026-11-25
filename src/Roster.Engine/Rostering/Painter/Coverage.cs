// Port of src/lib/stage2/coverage.ts.
// ─── Floor cover, quarter-hour by quarter-hour ──────────────────────────────
// Pure module (plan view, roster, tests). Three rules hold for every 15-minute
// block in which anyone is rostered:
//
//   - a manager is on (interview: ">=1 manager on floor every block anyone is
//     present");
//   - at least `minPeopleOnFloor` people are on - nobody works alone;
//   - a junior never works with fewer than `minAdultsWithJsa` adults.
//
// The plan is cut to satisfy them, but nothing used to check the finished
// shifts: the roster flagged an open slot as "Understaffed", never as "no
// manager on" or "one person alone", and the plan's own later steps could trim
// a manager's shift and leave a gap no screen mentioned. This reads the final
// shifts and names every block that breaks a rule, so a gap is always visible
// even before its cause is fixed.
//
// A person on a break still counts as present. Breaks are taken in the building,
// and whether a break should count as leaving a colleague alone is the owner's
// decision (Roster Engine Review, decision 1), not this check's.

using Roster.Engine.Core;

namespace Roster.Engine.Rostering.Painter;

/// <summary>A shift as the cover check reads it.</summary>
public sealed record CoverShift(string Role, double Start, double End, bool Unfilled = false);

/// <summary>The three cover rules (CoverIssueKind).</summary>
public static class CoverIssueKinds
{
    public const string NoManager = "no_manager";
    public const string LoneWorker = "lone_worker";
    public const string JuniorSupervision = "junior_supervision";
}

/// <param name="Kind">A <see cref="CoverIssueKinds"/> value.</param>
/// <param name="Start">First block, minutes from midnight.</param>
/// <param name="End">Exclusive.</param>
public sealed record CoverIssue(string Kind, double Start, double End);

/// <summary>
/// The two cover minimums (Pick&lt;RosteringRules, "minPeopleOnFloor" | "minAdultsWithJsa"&gt;).
/// A RosteringRules converts to it implicitly.
/// </summary>
public sealed record CoverRules(double MinPeopleOnFloor, double MinAdultsWithJsa)
{
    /// <summary>DEFAULT_RULES' two minimums.</summary>
    public static readonly CoverRules Default = From(RosteringRules.Default);

    public static CoverRules From(RosteringRules rules) => new(rules.MinPeopleOnFloor, rules.MinAdultsWithJsa);

    public static implicit operator CoverRules(RosteringRules rules) => From(rules);
}

public static class Coverage
{
    /// <summary>Every run of blocks that breaks a cover rule, merged into ranges.</summary>
    public static List<CoverIssue> CoverIssues(IEnumerable<PaintableShift> shifts, CoverRules? rules = null) =>
        CoverIssues(shifts.Select(s => new CoverShift(s.Role, s.Start, s.End, s.Unfilled)), rules);

    /// <inheritdoc cref="CoverIssues(IEnumerable{PaintableShift}, CoverRules?)"/>
    public static List<CoverIssue> CoverIssues(IEnumerable<CoverShift> shifts, CoverRules? rules = null)
    {
        var r = rules ?? CoverRules.Default;
        var real = shifts.Where(s => !s.Unfilled && s.End > s.Start).ToList();
        if (real.Count == 0) return [];
        var from = Js.Min(real.Select(s => s.Start));
        var to = Js.Max(real.Select(s => s.End));

        var out_ = new List<CoverIssue>();
        var open = new JsMap<string, double>(); // kind -> start of the current run
        void Close(string kind, double at)
        {
            if (!open.TryGet(kind, out var start)) return;
            out_.Add(new CoverIssue(kind, start, at));
            open.Delete(kind);
        }

        for (var m = from; m < to; m += Grid.BlockMin)
        {
            var people = 0;
            var managers = 0;
            var adults = 0;
            var juniors = 0;
            foreach (var s in real)
            {
                if (m < s.Start || m >= s.End) continue;
                people++;
                if (Roles.IsManager(s.Role)) managers++;
                if (s.Role == Roles.JSA) juniors++;
                else adults++;
            }
            (string Kind, bool Broken)[] broken =
            [
                (CoverIssueKinds.NoManager, people > 0 && managers == 0),
                (CoverIssueKinds.LoneWorker, people > 0 && people < r.MinPeopleOnFloor),
                (CoverIssueKinds.JuniorSupervision, juniors > 0 && adults < r.MinAdultsWithJsa),
            ];
            foreach (var (kind, isBroken) in broken)
            {
                if (isBroken)
                {
                    if (!open.Has(kind)) open.Set(kind, m);
                }
                else Close(kind, m);
            }
        }
        foreach (var kind in open.Keys().ToList()) Close(kind, to);
        return Js.Sort(out_, (a, b) => (a.Start - b.Start).Or(Js.LocaleCompare(a.Kind, b.Kind)));
    }

    /// <summary>The same issues as plain-English flags for the plan and roster screens.</summary>
    public static List<string> CoverFlags(IEnumerable<PaintableShift> shifts, CoverRules? rules = null) =>
        CoverFlags(shifts.Select(s => new CoverShift(s.Role, s.Start, s.End, s.Unfilled)), rules);

    /// <inheritdoc cref="CoverFlags(IEnumerable{PaintableShift}, CoverRules?)"/>
    public static List<string> CoverFlags(IEnumerable<CoverShift> shifts, CoverRules? rules = null)
    {
        var r = rules ?? CoverRules.Default;
        return CoverIssues(shifts, r).Select(i =>
        {
            var span = $"{Grid.MinToTime(i.Start)}–{Grid.MinToTime(i.End)}";
            return i.Kind switch
            {
                CoverIssueKinds.NoManager => $"No manager rostered {span}",
                CoverIssueKinds.LoneWorker =>
                    $"Fewer than {Js.NumberToString(r.MinPeopleOnFloor)} people rostered {span} - someone works alone",
                _ => $"Junior rostered with fewer than {Js.NumberToString(r.MinAdultsWithJsa)} adults {span}",
            };
        }).ToList();
    }
}
