// New: where the parity comparer may treat false as a missing field, or an
// empty object as an empty array - an explicit list, each entry shown safe by
// how the TypeScript app reads that field (see JsonDiff). Anywhere else a
// difference between them is a difference.

using System.Text;
using Xunit.Abstractions;

namespace Roster.Engine.Tests.Parity;

public static class ParityTolerances
{
    /// <summary>
    /// Per entry point: "kind|normalised path" (JsonDiff.Normalise). The audit
    /// of 25/11/2026 ran the full corpus with every tolerance allowed and found
    /// them needed at these five paths only, always as "TypeScript leaves the
    /// field out, C# writes false". Each field is read in the TypeScript app
    /// only by truthiness tests (if (x), !x, x ? a : b, x &amp;&amp; y, x || y,
    /// filter(s =&gt; !s.unfilled)) - never by ??, === false, !== undefined, in or
    /// a JSON comparison - so false and undefined read the same. An empty object
    /// was never needed to equal an empty array: that tolerance is allowed nowhere.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed = new Dictionary<string, IReadOnlySet<string>>
    {
        ["stage1"] = new HashSet<string>
        {
            // WindowRule.makeUpOnly: read only as !w.makeUpOnly (stage1/engine.ts,
            // stage1/compute.ts) and !(placementOnly && w.makeUpOnly) (allowedGroupsFromStage1).
            $"{Tolerance.FalseAsAbsent}|computeStage1:$.output.windows[].makeUpOnly",
        },
        ["painter"] = new HashSet<string>
        {
            // PaintableShift.unfilled: if (s.unfilled), !s.unfilled, o.unfilled || ... only (engine.ts,
            // coverage.ts, breakGap.ts, fortnight.ts, roster/*, payroll/periodTiers.ts, analysis/compute.ts).
            $"{Tolerance.FalseAsAbsent}|paintTasks:$.after.shifts[].unfilled",
            // PaintResult.searchCapped: if (searchCapped && ...), searchCapped ? ... : ... only
            // (fortnight.ts, stage1/planShifts.ts).
            $"{Tolerance.FalseAsAbsent}|paintTasks:$.output.searchCapped",
        },
        ["fortnight"] = new HashSet<string>
        {
            // FShift has no fixed in the TypeScript; C#'s FShift inherits PaintableShift.Fixed.
            // Shifts' fixed is read only as s.fixed / !o.fixed / b.fixed (the painter, engine.ts).
            $"{Tolerance.FalseAsAbsent}|runFortnightStage2:$.output.byDate.<date>.shifts[].fixed",
            // As the painter's unfilled above.
            $"{Tolerance.FalseAsAbsent}|runFortnightStage2:$.output.byDate.<date>.shifts[].unfilled",
        },
    };

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public static Tolerance For(string entry) =>
        new(entry, Allowed.GetValueOrDefault(entry) ?? None,
            permissive: Environment.GetEnvironmentVariable("PARITY_TOLERANCE") == "permissive");

    /// <summary>Where the tolerances were used; to PARITY_TOLERANCE_LOG/&lt;entry&gt;.tsv when set.</summary>
    public static void Report(Tolerance t, ITestOutputHelper log)
    {
        var lines = t.Used.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key.Replace('|', '\t')}\t{kv.Value.Count}\t{kv.Value.Example}")
            .ToList();
        foreach (var l in lines) log.WriteLine($"tolerance used: {l}");
        var dir = Environment.GetEnvironmentVariable("PARITY_TOLERANCE_LOG");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        foreach (var (k, n) in ParityJson.EmptyMapReads.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.AppendLine($"input-empty-map\t{k}\t{n}");
        File.WriteAllText(Path.Combine(dir, $"{t.Entry}.tsv"), sb.ToString());
    }
}
