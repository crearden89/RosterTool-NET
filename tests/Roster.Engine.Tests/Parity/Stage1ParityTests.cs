// New: stage 1 day plan parity - runStage1 (src/lib/stage1/engine.ts) against
// Stage1Engine.RunStage1 on every recorded call.

using Roster.Engine.Planning.Stage1;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class Stage1ParityTests(ITestOutputHelper log)
{
    public const string Entry = "stage1";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["runStage1"] = c => new(ParityJson.ToNode(Stage1Engine.RunStage1(c.Arg<Stage1Input>("input")))),
        };

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
