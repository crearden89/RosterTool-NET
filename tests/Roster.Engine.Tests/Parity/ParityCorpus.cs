// New: reads the parity corpus - the TypeScript engine's recorded calls
// (tools/parity/dump.ts writes them). One gzipped JSON-lines file per entry
// point; each line is one call:
//   {"id", "fn", "source", "input": {<parameter>: <value>}, "output", "after"?}
// "after" holds the arguments the call changed in place (the painter's shifts).
//
// The committed, representative corpus is tests/parity-corpus. The full corpus
// (every distinct call, too big to commit) is generated on demand by the dumper
// into $TMPDIR/roster-parity-full, or wherever PARITY_FULL_CORPUS points.

using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Roster.Engine.Tests.Parity;

public sealed record ParityCase(string Id, string Fn, string Source, JsonObject Input, JsonNode? Output, JsonObject? After)
{
    public override string ToString() => $"{Id} {Fn} ({Source})";
}

public static class ParityCorpus
{
    /// <summary>The committed corpus: tests/parity-corpus in this repository.</summary>
    public static string CommittedDir => Path.Combine(RepoRoot(), "tests", "parity-corpus");

    /// <summary>The full corpus, when it has been generated; null otherwise.</summary>
    public static string? FullDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("PARITY_FULL_CORPUS");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "roster-parity-full");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    public static string FileFor(string dir, string entry) => Path.Combine(dir, $"{entry}.jsonl.gz");

    /// <summary>
    /// The cases of one entry point, in recorded order. PARITY_CASE (ids, comma
    /// separated) narrows the run to those cases.
    /// </summary>
    public static IEnumerable<ParityCase> Read(string dir, string entry)
    {
        var only = Environment.GetEnvironmentVariable("PARITY_CASE")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var file = File.OpenRead(FileFor(dir, entry));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var o = JsonNode.Parse(line)!.AsObject();
            var id = (string)o["id"]!;
            if (only is { Length: > 0 } && !only.Contains(id)) continue;
            yield return new ParityCase(
                id,
                (string)o["fn"]!,
                (string?)o["source"] ?? "",
                o["input"]!.AsObject(),
                o["output"],
                o["after"] as JsonObject);
        }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Roster.sln"))) return d.FullName;
        throw new DirectoryNotFoundException($"Roster.sln not found above {AppContext.BaseDirectory}");
    }
}
