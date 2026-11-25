// New: the Planning port replayed against answers the TypeScript app gave
// (planning-vectors.mjs writes planning-vectors.json): random Stage 1 days,
// the day checker over random shifts on them, and the benchmark split's
// hundredths, compared field by field.

using System.Text.Json;
using System.Text.Json.Serialization;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Engine.Tests.Planning;

public class PlanningVectorTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Planning", "planning-vectors.json"))).RootElement;

    // JSON.stringify writes NaN and Infinity as null; the C# side writes them by name.
    private static readonly JsonSerializerOptions Json = new(RosterJson.Options) { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private sealed record ShiftDto(string PlanRole, string? SlotId, double Start, double End, List<CheckedBlock> Blocks)
    {
        public CheckedShift ToChecked() => new() { PlanRole = PlanRole, SlotId = SlotId, Start = Start, End = End, Blocks = Blocks };
    }

    [Fact]
    public void RunStage1_gives_the_TypeScript_engines_answer_on_every_vector()
    {
        var failures = new List<string>();
        var i = 0;
        foreach (var c in Vectors.GetProperty("stage1").EnumerateArray())
        {
            var input = c.GetProperty("input").Deserialize<Stage1Input>(Json)!;
            var got = JsonSerializer.SerializeToElement(Stage1Engine.RunStage1(input), Json);
            var diff = JsonDiff.First(c.GetProperty("out"), got, "$");
            if (diff is not null) failures.Add($"stage1[{i}] {diff}");
            i++;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void CheckPlanDay_gives_the_TypeScript_checkers_answer_on_every_vector()
    {
        var failures = new List<string>();
        var i = 0;
        foreach (var c in Vectors.GetProperty("dayCheck").EnumerateArray())
        {
            var s1 = c.GetProperty("s1").Deserialize<Stage1Computation>(Json)!;
            var shifts = c.GetProperty("shifts").Deserialize<List<ShiftDto>>(Json)!.Select(s => s.ToChecked()).ToList();
            var opts = c.GetProperty("opts").Deserialize<DayCheckOptions>(Json)!;
            var got = JsonSerializer.SerializeToElement(DayChecker.CheckPlanDay(s1, shifts, opts), Json);
            var diff = JsonDiff.First(c.GetProperty("out"), got, "$");
            if (diff is not null) failures.Add($"dayCheck[{i}] {diff}");
            i++;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void ToHundredths_gives_the_TypeScript_answer_in_its_key_order()
    {
        var failures = new List<string>();
        var i = 0;
        foreach (var c in Vectors.GetProperty("hundredths").EnumerateArray())
        {
            var parts = c.GetProperty("parts").Deserialize<JsMap<string, double>>(Json)!;
            var keys = c.GetProperty("keys").Deserialize<List<string>?>(Json);
            var got = BenchmarkSplit.ToHundredths(parts, c.GetProperty("total").GetDouble(), keys);
            var want = c.GetProperty("out").EnumerateObject().Select(p => (p.Name, p.Value.GetDouble())).ToList();
            if (!want.SequenceEqual(got.Select(e => (e.Key, e.Value))))
                failures.Add($"hundredths[{i}]: want {string.Join(",", want)} got {string.Join(",", got.Select(e => (e.Key, e.Value)))}");
            i++;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }
}

/// <summary>
/// The first difference between a JSON.stringify answer and the C# port's,
/// or null. A field the TypeScript left out (undefined) and one the C# writes
/// as null are the same; so are JSON.stringify's null for NaN or Infinity and
/// the C# "NaN". Numbers compare exactly.
/// </summary>
internal static class JsonDiff
{
    private static bool IsAbsent(JsonElement? e) => e is null || e.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public static string? First(JsonElement? want, JsonElement? got, string path)
    {
        if (IsAbsent(want) && IsAbsent(got)) return null;
        if (IsAbsent(want) && got!.Value.ValueKind == JsonValueKind.String && got.Value.GetString() is "NaN" or "Infinity" or "-Infinity") return null;
        if (IsAbsent(want) || IsAbsent(got)) return $"{path}: want {Show(want)}, got {Show(got)}";
        var w = want!.Value;
        var g = got!.Value;
        if (w.ValueKind != g.ValueKind) return $"{path}: want {Show(w)}, got {Show(g)}";
        switch (w.ValueKind)
        {
            case JsonValueKind.Object:
                var names = w.EnumerateObject().Select(p => p.Name).Concat(g.EnumerateObject().Select(p => p.Name)).Distinct();
                foreach (var n in names)
                {
                    JsonElement? wv = w.TryGetProperty(n, out var a) ? a : null;
                    JsonElement? gv = g.TryGetProperty(n, out var b) ? b : null;
                    var d = First(wv, gv, $"{path}.{n}");
                    if (d is not null) return d;
                }
                return null;
            case JsonValueKind.Array:
                var wl = w.GetArrayLength();
                var gl = g.GetArrayLength();
                for (var i = 0; i < Math.Min(wl, gl); i++)
                {
                    var d = First(w[i], g[i], $"{path}[{i}]");
                    if (d is not null) return d;
                }
                return wl == gl ? null : $"{path}: want {wl} items, got {gl}";
            case JsonValueKind.Number:
                return w.GetDouble() == g.GetDouble() ? null : $"{path}: want {w.GetRawText()}, got {g.GetRawText()}";
            case JsonValueKind.String:
                return w.GetString() == g.GetString() ? null : $"{path}: want {Show(w)}, got {Show(g)}";
            default:
                return w.GetRawText() == g.GetRawText() ? null : $"{path}: want {Show(w)}, got {Show(g)}";
        }
    }

    private static string Show(JsonElement? e)
    {
        if (e is null) return "(absent)";
        var t = e.Value.GetRawText();
        return t.Length > 160 ? t[..160] + "…" : t;
    }
}
