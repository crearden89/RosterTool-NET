// New: sub-task label parity - labelShifts (src/lib/subtasks/label.ts) against
// Label.LabelBlocks on every recorded call. Only a shift's tiles are read or
// written, so the corpus carries each shift as { blocks }.

using System.Text.Json.Nodes;
using Roster.Engine.Core;
using Roster.Engine.SubTasks;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class SubTasksParityTests(ITestOutputHelper log)
{
    public const string Entry = "subtasks";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["labelShifts"] = c =>
            {
                var shifts = c.Input["shifts"]!.AsArray()
                    .Select(s => (IReadOnlyList<DisplayBlock>)ParityJson.Read<List<DisplayBlock>>(s!["blocks"]))
                    .ToList();
                var labelled = Label.LabelBlocks(shifts, c.Arg<LabelContext>("ctx"));
                return new(new JsonArray(labelled.Select(b => (JsonNode)new JsonObject { ["blocks"] = ParityJson.ToNode(b) }).ToArray()));
            },
        };

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
