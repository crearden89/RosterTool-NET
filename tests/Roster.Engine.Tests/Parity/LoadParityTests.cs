// New: load allocation parity - allocateLoad (src/lib/load/allocate.ts) against
// Allocate.AllocateLoad on every recorded call (the Load Planner's crew, truck,
// pins and rotation history for each rostered day of the replay kit).

using Roster.Engine.Load;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class LoadParityTests(ITestOutputHelper log)
{
    public const string Entry = "load";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["allocateLoad"] = c => new(ParityJson.ToNode(Allocate.AllocateLoad(
                c.Arg<List<LoadCrewMember>>("crewIn"),
                c.Arg<List<LoadItem>>("loads"),
                c.Input["opts"] is null ? null : c.Arg<AllocateOptions>("opts")))),
        };

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
