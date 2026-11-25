// New: fortnight roster parity - runFortnightStage2 (src/lib/stage2/fortnight.ts)
// against FortnightEngine.RunFortnightStage2 on every recorded call: the plan
// slots of 14 days filled with named people, flags and contract outcomes.

using Roster.Engine.Rostering.Fortnight;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class FortnightParityTests(ITestOutputHelper log)
{
    public const string Entry = "fortnight";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["runFortnightStage2"] = c => new(ParityJson.ToNode(FortnightEngine.RunFortnightStage2(c.Arg<FortnightInput>("input")))),
        };

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
