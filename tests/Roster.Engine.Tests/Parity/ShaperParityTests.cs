// New: shaper parity - shapePlan and labelJsaSlots (src/lib/stage2/shaper.ts)
// against PlanShaper.ShapePlan and PlanShaper.LabelJsaSlots on every recorded
// call. The input's allowedAt and placeAt are tables in the corpus (TimeTable).

using System.Text.Json.Nodes;
using Roster.Engine.Rostering.Shaper;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public sealed class ShaperParityTests(ITestOutputHelper log)
{
    public const string Entry = "shaper";

    public static readonly IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> Adapters =
        new Dictionary<string, Func<ParityCase, ParityResult>>
        {
            ["shapePlan"] = c =>
            {
                var raw = c.Input["input"]!.AsObject();
                var input = ParityJson.Read<ShaperInput>(raw) with
                {
                    AllowedAt = TimeTable.From(raw["allowedAt"]),
                    PlaceAt = TimeTable.From(raw["placeAt"]),
                };
                return new(ParityJson.ToNode(PlanShaper.ShapePlan(input)));
            },
            ["labelJsaSlots"] = c =>
            {
                var slots = c.Arg<List<PlanSlot>>("slots");
                PlanShaper.LabelJsaSlots(
                    slots,
                    c.Arg<bool>("isSchoolDay"),
                    c.Arg<ShaperRule>("jsaRule"),
                    c.Input["rules"] is null ? null : c.Arg<Dictionary<string, double>>("rules"));
                return new(null, new JsonObject { ["slots"] = ParityJson.ToNode(slots) });
            },
        };

    [Fact]
    public void Committed_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.CommittedDir, Entry, Adapters, log);

    [Fact, Trait("Category", "Slow")]
    public void Full_corpus_matches_the_TypeScript() => ParityRunner.Run(ParityCorpus.FullDir, Entry, Adapters, log);
}
