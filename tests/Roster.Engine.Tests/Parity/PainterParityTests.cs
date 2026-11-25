// New: stage 2 painter parity - paintTasks (src/lib/stage2/engine.ts) against
// TaskPainter.PaintTasks on every recorded call. The painter recolours the
// shifts it is given (and may move a shift's edges), so the shifts afterwards
// are compared as well as its result; a blank names its shift by index.

using System.Text.Json.Nodes;
using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class PainterParityTests(ITestOutputHelper log)
{
    public const string Entry = "painter";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>> { ["paintTasks"] = Paint };

    private static ParityResult Paint(ParityCase c)
    {
        var shifts = c.Arg<List<PaintableShift>>("shifts");
        var result = TaskPainter.PaintTasks(
            shifts,
            c.Arg<List<DemandBlock>>("demandByBlock"),
            c.Arg<List<string>>("priority"),
            TimeTable.From(c.Input["allowedAt"]),
            c.Input["opts"] is null ? null : c.Arg<PaintOptions>("opts"));
        var output = new JsonObject
        {
            ["blanks"] = new JsonArray(result.Blanks
                .Select(b => (JsonNode)new JsonObject
                {
                    ["shift"] = shifts.FindIndex(s => ReferenceEquals(s, b.Shift)),
                    ["times"] = ParityJson.ToNode(b.Times),
                })
                .ToArray()),
            ["lost"] = ParityJson.ToNode(result.Lost),
            ["searchCapped"] = result.SearchCapped,
        };
        return new(output, new JsonObject { ["shifts"] = ParityJson.ToNode(shifts) });
    }

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
