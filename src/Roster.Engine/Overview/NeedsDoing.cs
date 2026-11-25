// Port of src/lib/overview/needsDoing.ts.
// ─── The Dashboard's "Needs doing" list ──────────────────────────────────────
// What a store has to act on, each item linking to the page where it is done:
//
//   day-off requests waiting   the team's pending requests        -> Requests
//   plan days with an error    errors not yet explained or fixed  -> Review
//   warnings not checked yet   warnings not yet ticked            -> Review
//   a fixed roster above its contract  one item per person        -> Team Members
//
// Errors and warnings are the store's review work, so they are listed only
// while the review is with the store (not yet submitted, or returned): once
// the plan is submitted every error carries its note, and once it is approved
// the plan is locked. Pure - the page reads the rows and hands them in.

using Roster.Engine.Core;
using Roster.Engine.Pay;

namespace Roster.Engine.Overview;

/// <summary>A review check as the list reads it: "error" or "warning", ticked or not, and a hard stop.</summary>
public sealed record NeedsCheck(string Severity, bool Done, bool HardStop = false);

/// <param name="Label">"Sun 29 Nov"</param>
public sealed record ReviewDayInput(DateOnly Date, string Label, IReadOnlyList<NeedsCheck> Checks);

/// <summary>A pending day-off request (only its age matters).</summary>
public sealed record PendingRequest(string SubmittedAt);

/// <summary>Someone whose fixed roster pays more than their contract.</summary>
public sealed record OverContract(string Name, double PaidHours, double ContractHours);

public sealed record NeedsLinks(string Requests, string Review, string Team);

/// <param name="HasPlan">A plan is held for the fortnight.</param>
/// <param name="Requests">The team's pending day-off requests, oldest first.</param>
/// <param name="OverContract">People whose fixed roster pays more than their contract.</param>
public sealed record NeedsDoingInput(
    bool HasPlan,
    ReviewStatus ReviewStatus,
    IReadOnlyList<ReviewDayInput> Days,
    IReadOnlyList<PendingRequest> Requests,
    IReadOnlyList<OverContract> OverContract,
    NeedsLinks Links);

public enum NeedsKind { Requests, Errors, Warnings, Contract }

/// <param name="Tone">Red: an error to explain or fix; soft otherwise.</param>
public sealed record NeedsItem(string Key, NeedsKind Kind, string Title, string Detail, string Href, string LinkLabel, ChipTone Tone);

/// <summary>A fixed shift, as fixed_rosters holds it ("09:00:00").</summary>
public sealed record FixedShiftRow(string EmployeeId, string StartTime, string EndTime);

/// <summary>A multi-week fixed roster's cycle length (fixed_roster_cycles).</summary>
public sealed record FixedCycleRow(string EmployeeId, double CycleWeeks);

/// <summary>A team member as the contract check reads them.</summary>
public sealed record ContractMember(string Id, string Name, string Role, double ContractHours);

public static class NeedsDoing
{
    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>The review is the store's to work on: checks still count as to-dos.</summary>
    public static bool ReviewIsWithStore(bool hasPlan, ReviewStatus status) =>
        hasPlan && (status == ReviewStatus.InProgress || status == ReviewStatus.Returned);

    private static string ListDays(IReadOnlyList<string> labels, int max = 3) =>
        labels.Count <= max ? string.Join(", ", labels) : $"{string.Join(", ", labels.Take(max))} and {labels.Count - max} more";

    public static List<NeedsItem> Items(NeedsDoingInput input)
    {
        var items = new List<NeedsItem>();

        if (input.Requests.Count > 0)
        {
            var n = input.Requests.Count;
            items.Add(new(
                "requests",
                NeedsKind.Requests,
                $"{Plural(n, "day-off request", "day-off requests")} waiting",
                "First in, first served",
                input.Links.Requests,
                "Requests",
                ChipTone.Soft));
        }

        if (ReviewIsWithStore(input.HasPlan, input.ReviewStatus))
        {
            // A hard stop can't be cleared with a note (owner decision R3), so it
            // stays on the list until the plan itself is fixed.
            var errorDays = input.Days
                .Where(d => d.Checks.Any(c => c.Severity == "error" && (!c.Done || c.HardStop)))
                .ToList();
            if (errorDays.Count > 0)
            {
                items.Add(new(
                    "errors",
                    NeedsKind.Errors,
                    $"{Plural(errorDays.Count, "plan day", "plan days")} with an error",
                    ListDays(errorDays.Select(d => d.Label).ToList()),
                    input.Links.Review,
                    "Review",
                    ChipTone.Red));
            }
            var warnings = input.Days.Sum(d => d.Checks.Count(c => c.Severity == "warning" && !c.Done));
            if (warnings > 0)
            {
                items.Add(new(
                    "warnings",
                    NeedsKind.Warnings,
                    $"{Plural(warnings, "warning", "warnings")} not checked yet",
                    "The store checks each one before submitting",
                    input.Links.Review,
                    "Review",
                    ChipTone.Soft));
            }
        }

        foreach (var p in input.OverContract)
        {
            items.Add(new(
                $"contract:{p.Name}",
                NeedsKind.Contract,
                $"{p.Name}'s fixed roster pays above contract",
                $"{Js.ToFixed(p.PaidHours, 1)} h a fortnight against a {Js.NumberToString(p.ContractHours)} h contract",
                input.Links.Team,
                "Team Members",
                ChipTone.Soft));
        }

        return items;
    }

    // ─── Fixed rosters above contract ────────────────────────────────────────────
    // The Team Members page's rule, unchanged: each fixed shift is paid its length
    // less the role's unpaid break; a multi-week cycle is averaged over its weeks
    // and scaled to the fortnight (the unit contracts are stored in). A junior
    // (JSA) is casual and has no contract to exceed.

    public static JsMap<string, double> FixedPaidHours(
        IEnumerable<FixedShiftRow> fixedShifts,
        IEnumerable<FixedCycleRow> cycles,
        Func<string, string?> roleOf)
    {
        var weeks = new JsMap<string, double>();
        foreach (var c in cycles) weeks.Set(c.EmployeeId, c.CycleWeeks);
        var paid = new JsMap<string, double>();
        foreach (var f in fixedShifts)
        {
            var span = Grid.TimeToMin(f.EndTime) - Grid.TimeToMin(f.StartTime);
            var unpaid = Breaks.BreakForRole(roleOf(f.EmployeeId) ?? Roles.SA, span / 60).UnpaidMinutes;
            paid.Set(f.EmployeeId, paid.GetOr(f.EmployeeId, 0) + Math.Max(0, span - unpaid) / 60);
        }
        foreach (var (id, h) in paid.Entries()) paid.Set(id, h / weeks.GetOr(id, 1) * 2);
        return paid;
    }

    public static List<OverContract> FixedRostersAboveContract(
        IReadOnlyList<ContractMember> employees,
        IEnumerable<FixedShiftRow> fixedShifts,
        IEnumerable<FixedCycleRow> cycles)
    {
        var byId = new JsMap<string, ContractMember>();
        foreach (var e in employees) byId.Set(e.Id, e);
        var paid = FixedPaidHours(fixedShifts, cycles, id => byId.Get(id)?.Role);
        return employees
            .Where(e => e.Role != Roles.JSA && paid.Has(e.Id) && paid[e.Id] > e.ContractHours + 0.01)
            .Select(e => new OverContract(e.Name, paid[e.Id], e.ContractHours))
            .ToList();
    }
}
