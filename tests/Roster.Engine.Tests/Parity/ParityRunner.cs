// New: runs one entry point's corpus through the C# engine and compares every
// result with the TypeScript one. Each recorded function has an adapter that
// reads the case's input, calls the C# port and returns its result in the
// corpus's form (and, where the TypeScript changed its arguments in place,
// those arguments afterwards). A case passes only when both are identical
// (JsonDiff); a failure names the case, where it came from and the first
// difference. Rerun one case with PARITY_CASE=<id>.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

/// <summary>What the C# engine returned for a case, in the corpus's form.</summary>
public sealed record ParityResult(JsonNode? Output, JsonObject? After = null);

public static class ParityRunner
{
    private const int MaxReported = 8;

    public static void Run(
        string? dir,
        string entry,
        IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> adapters,
        ITestOutputHelper log)
    {
        // A Slow full-corpus run with no corpus must not read as a pass.
        if (dir is null)
            Assert.Fail($"{entry}: full corpus not generated: run tools/parity/dump.ts (or set PARITY_FULL_CORPUS to its --full folder)");
        var file = ParityCorpus.FileFor(dir, entry);
        Assert.True(File.Exists(file), $"{file} is missing: run tools/parity/dump.ts");

        var clock = Stopwatch.StartNew();
        var failures = new List<string>();
        var counts = new SortedDictionary<string, (int Cases, int Failed)>();
        foreach (var c in ParityCorpus.Read(dir, entry))
        {
            var diff = Compare(c, adapters);
            var (n, f) = counts.GetValueOrDefault(c.Fn);
            counts[c.Fn] = (n + 1, f + (diff is null ? 0 : 1));
            if (diff != null && failures.Count < MaxReported) failures.Add($"{c}\n    {diff}");
        }
        var total = counts.Values.Sum(x => x.Cases);
        var failed = counts.Values.Sum(x => x.Failed);
        var summary = string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value.Cases - kv.Value.Failed}/{kv.Value.Cases}"));
        log.WriteLine($"{entry}: {total - failed} of {total} cases identical ({summary}) in {clock.Elapsed.TotalSeconds:0.0}s");
        Assert.True(total > 0, $"{file} has no cases");
        if (failed > 0)
            Assert.Fail(
                $"{entry}: {failed} of {total} cases differ from the TypeScript ({summary}). First {failures.Count}:\n  " +
                string.Join("\n  ", failures) +
                $"\nRerun one with PARITY_CASE=<id> (corpus {file}).");
    }

    /// <summary>The first difference for one case, or null when the C# result is identical.</summary>
    public static string? Compare(ParityCase c, IReadOnlyDictionary<string, Func<ParityCase, ParityResult>> adapters)
    {
        if (!adapters.TryGetValue(c.Fn, out var run)) return $"no C# adapter for {c.Fn}";
        ParityResult r;
        try
        {
            r = run(c);
        }
        catch (Exception e)
        {
            var at = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Roster.Engine."))?.Trim();
            return $"threw {e.GetType().Name}: {e.Message} {at}";
        }
        if (c.After != null)
        {
            if (r.After is null) return "$.after: the TypeScript changed its arguments in place, the adapter reports none";
            // Only the arguments the TypeScript changed are compared.
            var after = new JsonObject();
            foreach (var (k, _) in c.After) after[k] = r.After[k]?.DeepClone();
            var d = JsonDiff.FirstDifference(c.After, after, "$.after");
            if (d != null) return d;
        }
        return JsonDiff.FirstDifference(c.Output, r.Output, "$.output");
    }

    /// <summary>The case's argument <paramref name="name"/>, read as <typeparamref name="T"/>.</summary>
    public static T Arg<T>(this ParityCase c, string name) => ParityJson.Read<T>(c.Input[name]);
}
