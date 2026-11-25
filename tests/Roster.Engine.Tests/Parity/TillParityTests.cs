// New: till parity - computeBlock, liftToOneTill, dailyCheckoutHours
// (src/lib/till/engine.ts) and computeBorrow (src/lib/till/borrow.ts) against
// Roster.Engine.Till on every recorded call.

using Roster.Engine.Core;
using Roster.Engine.Till;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class TillParityTests(ITestOutputHelper log)
{
    public const string Entry = "till";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["computeBlock"] = c => Out(TillEngine.ComputeBlock(c.Arg<double>("segment"), c.Arg<CheckoutAttrs>("a"))),
            ["liftToOneTill"] = c => Out(TillEngine.LiftToOneTill(c.Arg<BlockResult>("r"), c.Arg<CheckoutAttrs>("a"))),
            ["dailyCheckoutHours"] = c => Out(TillEngine.DailyCheckoutHours(c.Arg<List<BlockResult>>("blocks"))),
            ["computeBorrow"] = c => Out(Borrow.ComputeBorrow(
                c.Arg<List<BorrowBlock>>("blocks"),
                c.Arg<int>("dowMon0"),
                c.Arg<JsMap<string, double>>("available"),
                c.Input["bands"] is null ? null : c.Arg<List<BorrowBand>>("bands"),
                c.Input["minRetained"] is null ? null : c.Arg<double>("minRetained"),
                c.Input["committed"] is null ? null : c.Arg<JsMap<string, double>>("committed"))),
        };

    private static ParityResult Out(object? value) => new(ParityJson.ToNode(value));

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
