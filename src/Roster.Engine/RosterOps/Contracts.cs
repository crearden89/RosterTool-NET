// Port of src/lib/roster/contracts.ts.
// ─── Where the team landed against contract this fortnight ───────────────────
// The Roster page's contract figures as numbers. They come from the engine's
// own contract outcomes (contractOutcomes in Rostering/Fortnight), read
// from its ledgers once the roster is finished and held with the roster since
// it is built (Data snapshots). No number here comes from flag text.
//
// The engine writes its contract flags from the same final ledger, so the two
// agree. The figures still replace those flags rather than sit beside them:
// one place for the numbers, and a roster held before 15/11/2026 can carry
// lines from before the engine read them again at the end, when a break
// rescue's swap had moved hours between two people. Pure.

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;

namespace Roster.Engine.RosterOps;

public sealed record ContractGap(
    string EmployeeId,
    string Name,
    string Role,
    string Kind, // "under" | "over"
    double Hours); // hours under or above the fortnight contract, always positive

public sealed record ContractFigures
{
    /// <summary>Everyone more than half an hour off their contract, the largest gap first.</summary>
    public List<ContractGap> People { get; init; } = new();
    public double Under { get; init; } // people under contract
    public double Over { get; init; } // people above contract
    // The totals of the hours as shown (two decimals each), so they agree with
    // the rows they total.
    public double UnderHours { get; init; }
    public double OverHours { get; init; }
    /// <summary>The team's line, as the engine words it: which way its contracts are out.</summary>
    public string? Team { get; init; }
    /// <summary>The contract flags these figures would write, word for word.</summary>
    public JsSet<string> Lines { get; init; } = new();
}

public static partial class Contracts
{
    // The roles the engine holds to contract (UNDER_CONTRACT_ROLES in the
    // fortnight builder); juniors flex and are never flagged.
    private static readonly IReadOnlyList<string> HeldToContract = [Roles.SM, Roles.ASM, Roles.DSM, Roles.RSM, Roles.SA];
    // Within half an hour either way is on contract, as the engine's flags read it.
    private const double MarginH = 0.5;

    // The engine's contract lines (underContractFlags and overContractFlags in
    // the fortnight builder): a person under or above contract, perhaps with
    // why their contract can't be met, and the team's line.
    [GeneratedRegex(@"^(.+) is [0-9]+(?:\.[0-9]+)?h (?:under|above) contract this fortnight(?: - unreachable: (.+))?\z")]
    private static partial Regex PersonLine();

    [GeneratedRegex(@"^(?:The team's contracts hold [0-9]+(?:\.[0-9]+)?h more than the plans need this fortnight|The plans need more hours than the team's contracts hold): ")]
    private static partial Regex TeamLine();

    /// <summary>Where the team landed against contract, from the engine's own contract outcomes.</summary>
    public static ContractFigures ContractFigures(IEnumerable<ContractOutcome> outcomes)
    {
        var people = new List<ContractGap>();
        var lines = new JsSet<string>();
        double contract = 0;
        double given = 0;
        double overSum = 0;
        double overPeople = 0;
        double shortPeople = 0;
        foreach (var o in outcomes)
        {
            if (!HeldToContract.Contains(o.Role)) continue;
            // Paid hours plus paid leave and public holidays not worked, as the
            // engine's ledger counts them.
            var got = o.PaidHours + o.CreditedHours;
            contract += o.ContractPeriodHours;
            given += got;
            var diff = got - o.ContractPeriodHours;
            if (o.ShortfallHours > MarginH)
            {
                people.Add(new ContractGap(o.EmployeeId, o.Name, o.Role, "under", o.ShortfallHours));
                lines.Add($"{o.Name} is {Js.ToFixed(o.ShortfallHours, 2)}h under contract this fortnight");
            }
            if (diff > MarginH)
            {
                people.Add(new ContractGap(o.EmployeeId, o.Name, o.Role, "over", diff));
                lines.Add($"{o.Name} is {Js.ToFixed(diff, 2)}h above contract this fortnight");
                overSum += diff;
                overPeople++;
            }
            else if (diff < -MarginH) shortPeople++;
        }
        static string Are(double n) => $"{Js.NumberToString(n)} {(n == 1 ? "person is" : "people are")}";
        var team =
            overPeople > 0 && given > contract + MarginH
                ? $"The plans need more hours than the team's contracts hold: {Are(overPeople)} rostered {Js.ToFixed(overSum, 2)}h above contract this fortnight"
                : shortPeople > 0 && contract > given + MarginH
                    ? $"The team's contracts hold {Js.ToFixed(contract - given, 2)}h more than the plans need this fortnight: {Are(shortPeople)} short of contract and paid the difference"
                    : null;
        if (team is not null) lines.Add(team);
        Js.Sort(people, (a, b) => (b.Hours - a.Hours).Or(Js.LocaleCompare(a.Name, b.Name)));
        double Sum(string kind) => people.Where(p => p.Kind == kind).Aggregate(0.0, (n, p) => n + Js.Round(p.Hours * 100)) / 100;
        return new ContractFigures
        {
            People = people,
            Under = people.Count(p => p.Kind == "under"),
            Over = overPeople,
            UnderHours = Sum("under"),
            OverHours = Sum("over"),
            Team = team,
            Lines = lines,
        };
    }

    /// <summary>
    /// The fortnight flags the figures do not show: run lengths, anything new the
    /// engine adds, and why a contract can't be met (its hours dropped - the
    /// figures give them).
    /// </summary>
    public static List<string> OtherFortnightFlags(IEnumerable<string> flags)
    {
        var out_ = new List<string>();
        foreach (var f in flags)
        {
            if (TeamLine().IsMatch(f)) continue;
            var person = PersonLine().Match(f);
            if (!person.Success) out_.Add(f);
            else if (person.Groups[2].Success) out_.Add($"{person.Groups[1].Value}'s contract can't be met: {person.Groups[2].Value}");
        }
        return out_;
    }
}
