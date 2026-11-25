// New: pay parity - the break rules (src/lib/payroll/breaks.ts) and the pay
// period's tiers and make-up hours (src/lib/payroll/payTiers.ts) against
// Roster.Engine.Pay on every recorded call. tierPayPeriod's rulesFor is a
// function of the role; the corpus carries it as a table { role: settings }.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class PayParityTests(ITestOutputHelper log)
{
    public const string Entry = "pay";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["breakForShift"] = c => Out(Breaks.BreakForShift(c.Arg<string>("breakType"), c.Arg<double>("lengthHours"))),
            ["breakForRole"] = c => Out(Breaks.BreakForRole(c.Arg<string>("role"), c.Arg<double>("lengthHours"))),
            ["maxClockMinutes"] = c => Out(Breaks.MaxClockMinutes(c.Arg<string>("breakType"), c.Arg<double>("maxPaidHours"))),
            ["maxWorkMinutes"] = c => Out(Breaks.MaxWorkMinutes(c.Arg<string>("breakType"), c.Arg<double>("maxPaidHours"))),
            ["breakSegmentsFor"] = c => Out(Breaks.BreakSegmentsFor(c.Arg<double>("totalMinutes"))),
            ["breakTileError"] = c => Out(Breaks.BreakTileError(
                c.Arg<List<double>>("times"), c.Arg<double>("entitledMinutes"), c.Arg<double>("shiftStart"), c.Arg<double>("shiftEnd"))),
            ["tierPayPeriod"] = c =>
            {
                var rules = c.Arg<JsMap<string, PayRuleSettings>>("rulesFor");
                return Out(PayTiers.TierPayPeriod(c.Arg<List<TierShift>>("shifts"), role => rules[role]));
            },
            ["makeUpHours"] = c => Out(PayTiers.MakeUpHours(
                c.Arg<string>("role"), c.Arg<double>("contractPeriodHours"), c.Arg<double>("paidHours"), c.Arg<double>("creditedHours"),
                c.Input["exemptRoles"] is null ? null : c.Arg<List<string>>("exemptRoles"))),
        };

    private static ParityResult Out(object? value) => new(ParityJson.ToNode(value));

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
