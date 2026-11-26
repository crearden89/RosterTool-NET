// New: the parity comparer. Finds the first place two JSON values differ and
// names it by its JSON path, e.g. $.output.blanks[0].times[2].
//
// Exact, with one equivalence everywhere and two only where allowed:
//   - numbers compare as doubles, exactly (JSON.stringify writes the shortest
//     form that reads back to the same double, and System.Text.Json reads it
//     back exactly); "NaN" equals "NaN";
//   - everywhere: a missing field equals null (C# has no undefined; a TypeScript
//     field left undefined is a C# null);
//   - only at the field paths of ParityTolerances.Allowed: false equals a
//     missing or null field (an optional TypeScript boolean left undefined that
//     the C# port holds as a plain bool), and an empty object equals an empty
//     array (an empty Map, which the dumper writes as [] whatever its key type);
//   - object keys compare as a set: a record's property order is not data.
// Every use of those two is recorded (Tolerance.Used), so the allow-list can be
// audited against the corpus: PARITY_TOLERANCE=permissive allows them anywhere
// and PARITY_TOLERANCE_LOG=<dir> writes where they were needed.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Roster.Engine.Tests.Parity;

/// <summary>Which tolerances a comparison may use, and where it used them.</summary>
public sealed class Tolerance(string entry, IReadOnlySet<string> allowed, bool permissive)
{
    public const string FalseAsAbsent = "false~absent";
    public const string EmptyObjectAsArray = "{}~[]";

    public string Entry { get; } = entry;

    /// <summary>"kind|normalised path" -> (uses, an example: case id and raw path).</summary>
    public ConcurrentDictionary<string, (int Count, string Example)> Used { get; } = new();

    /// <summary>May <paramref name="kind"/> make the values at <paramref name="path"/> equal? Records the use.</summary>
    public bool Allows(string kind, string path, string detail, string caseId)
    {
        var key = $"{kind}|{JsonDiff.Normalise(path)}";
        if (!permissive && !allowed.Contains(key)) return false;
        Used.AddOrUpdate(key, _ => (1, $"{caseId} {path} {detail}"), (_, v) => (v.Count + 1, v.Example));
        return true;
    }
}

public static class JsonDiff
{
    /// <summary>The first difference between <paramref name="expected"/> and <paramref name="actual"/>, or null when equal.</summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path = "$", Tolerance? tolerance = null, string caseId = "")
    {
        if (IsAbsent(expected) && IsAbsent(actual)) return null;
        if (expected is JsonObject eo && actual is JsonObject ao) return Objects(eo, ao, path, tolerance, caseId);
        if (expected is JsonArray ea && actual is JsonArray aa) return Arrays(ea, aa, path, tolerance, caseId);
        if (tolerance != null)
        {
            if ((IsFalse(expected) && IsAbsent(actual)) || (IsAbsent(expected) && IsFalse(actual)))
                if (tolerance.Allows(Tolerance.FalseAsAbsent, path, $"expected {Show(expected)}, got {Show(actual)}", caseId)) return null;
            if (IsEmptyContainer(expected) && IsEmptyContainer(actual))
                if (tolerance.Allows(Tolerance.EmptyObjectAsArray, path, $"expected {Show(expected)}, got {Show(actual)}", caseId)) return null;
        }
        if (expected is JsonValue ev && actual is JsonValue av && SameValue(ev, av)) return null;
        return $"{path}: expected {Show(expected)}, got {Show(actual)}";
    }

    private static string? Objects(JsonObject e, JsonObject a, string path, Tolerance? t, string caseId)
    {
        foreach (var (k, v) in e)
        {
            var d = FirstDifference(v, a[k], $"{path}.{k}", t, caseId);
            if (d != null) return d;
        }
        foreach (var (k, v) in a)
        {
            if (e.ContainsKey(k)) continue;
            var d = FirstDifference(null, v, $"{path}.{k}", t, caseId);
            if (d != null) return d;
        }
        return null;
    }

    private static string? Arrays(JsonArray e, JsonArray a, string path, Tolerance? t, string caseId)
    {
        var n = Math.Min(e.Count, a.Count);
        for (var i = 0; i < n; i++)
        {
            var d = FirstDifference(e[i], a[i], $"{path}[{i}]", t, caseId);
            if (d != null) return d;
        }
        if (e.Count != a.Count)
            return $"{path}: expected {e.Count} items, got {a.Count}" +
                   (e.Count > n ? $"; first missing {Show(e[n])}" : $"; first extra {Show(a[n])}");
        return null;
    }

    // Absent: missing or null.
    private static bool IsAbsent(JsonNode? n) => n is null || (n is JsonValue v && v.GetValueKind() == JsonValueKind.Null);

    private static bool IsFalse(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.False;

    private static bool IsEmptyContainer(JsonNode? n) => n is JsonArray { Count: 0 } or JsonObject { Count: 0 };

    private static bool SameValue(JsonValue e, JsonValue a)
    {
        var ek = e.GetValueKind();
        var ak = a.GetValueKind();
        if (ek == JsonValueKind.Number && ak == JsonValueKind.Number)
        {
            var x = Number(e);
            var y = Number(a);
            return x == y || (double.IsNaN(x) && double.IsNaN(y));
        }
        if (ek != ak) return false;
        return ek switch
        {
            JsonValueKind.String => e.GetValue<string>() == a.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => e.ToJsonString() == a.ToJsonString(),
        };
    }

    // The number's JSON text read as a double (a node built in C# may hold an int).
    private static double Number(JsonValue v) => double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);

    public static string Show(JsonNode? n, int max = 240)
    {
        var s = n is null ? "(missing)" : n.ToJsonString();
        return s.Length <= max ? s : s[..max] + $"... ({s.Length} chars)";
    }

    // A field path with the data taken out: array indices become [], and map
    // keys that are data (dates, employee ids, a pay tier's shift key, numbers)
    // become <date>, <employee>, <shift>, <n>.
    public static string Normalise(string path) =>
        string.Join('.', Regex.Replace(path, @"\[\d+\]", "[]").Split('.').Select(seg => seg switch
        {
            _ when Regex.IsMatch(seg, @"^\d{4}-\d{2}-\d{2}(\[\])*$") => "<date>" + seg[10..],
            _ when Regex.IsMatch(seg, @"^e\d+(\[\])*$") => "<employee>" + Regex.Replace(seg, @"^e\d+", ""),
            _ when Regex.IsMatch(seg, @"^[^:]+:\d{4}-\d{2}-\d{2}:-?[\d.]+(\[\])*$") => "<shift>" + Regex.Replace(seg, @"^[^\[]+", ""),
            _ when Regex.IsMatch(seg, @"^-?[\d.]+(\[\])*$") => "<n>" + Regex.Replace(seg, @"^[^\[]+", ""),
            _ => seg,
        }));
}
