// New: the parity comparer. Finds the first place two JSON values differ and
// names it by its JSON path, e.g. $.output.blanks[0].times[2].
//
// Exact, with the few equivalences that come from C# not having undefined:
//   - numbers compare as doubles, exactly (JSON.stringify writes the shortest
//     form that reads back to the same double, and System.Text.Json reads it
//     back exactly); 0 equals -0; "NaN" equals "NaN";
//   - a missing field, null and false are the same (an optional TypeScript
//     field left undefined is a C# null or a false bool);
//   - an empty object and an empty array are the same (an empty Map);
//   - object keys compare as a set: a record's property order is not data.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Roster.Engine.Tests.Parity;

public static class JsonDiff
{
    /// <summary>The first difference between <paramref name="expected"/> and <paramref name="actual"/>, or null when equal.</summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (IsNothing(expected) && IsNothing(actual)) return null;
        if (expected is JsonObject eo && actual is JsonObject ao) return Objects(eo, ao, path);
        if (expected is JsonArray ea && actual is JsonArray aa) return Arrays(ea, aa, path);
        if (IsEmptyContainer(expected) && IsEmptyContainer(actual)) return null;
        if (expected is JsonValue ev && actual is JsonValue av && SameValue(ev, av)) return null;
        return $"{path}: expected {Show(expected)}, got {Show(actual)}";
    }

    private static string? Objects(JsonObject e, JsonObject a, string path)
    {
        foreach (var (k, v) in e)
        {
            var d = FirstDifference(v, a[k], $"{path}.{k}");
            if (d != null) return d;
        }
        foreach (var (k, v) in a)
        {
            if (e.ContainsKey(k)) continue;
            var d = FirstDifference(null, v, $"{path}.{k}");
            if (d != null) return d;
        }
        return null;
    }

    private static string? Arrays(JsonArray e, JsonArray a, string path)
    {
        var n = Math.Min(e.Count, a.Count);
        for (var i = 0; i < n; i++)
        {
            var d = FirstDifference(e[i], a[i], $"{path}[{i}]");
            if (d != null) return d;
        }
        if (e.Count != a.Count)
            return $"{path}: expected {e.Count} items, got {a.Count}" +
                   (e.Count > n ? $"; first missing {Show(e[n])}" : $"; first extra {Show(a[n])}");
        return null;
    }

    // Nothing: missing, null or false.
    private static bool IsNothing(JsonNode? n) =>
        n is null || (n is JsonValue v && v.GetValueKind() is JsonValueKind.Null or JsonValueKind.False);

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
    private static double Number(JsonValue v) => double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public static string Show(JsonNode? n, int max = 240)
    {
        var s = n is null ? "(missing)" : n.ToJsonString();
        return s.Length <= max ? s : s[..max] + $"... ({s.Length} chars)";
    }
}
