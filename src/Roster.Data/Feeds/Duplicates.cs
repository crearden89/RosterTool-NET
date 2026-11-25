// Port of src/lib/csv/duplicates.ts.
// ─── Two rows for one thing ──────────────────────────────────────────────────
// Every upload writes on a natural key: one store's day, one employee, one
// quarter-hour reading. Two rows in the same file for the same key used to pass
// validation, and then one of two things happened:
//   - Postgres refused the whole statement ("ON CONFLICT DO UPDATE command
//     cannot affect row a second time"), after the upload had been logged as
//     accepted, so nothing loaded and the history said it had;
//   - or, where the rows are summed (the POS readings), the figure was
//     silently counted twice.
//
// Identical copies are one fact written twice - a common export glitch - so
// they are kept once. Copies that DISAGREE are a contradiction only the file
// can settle, so every copy is rejected with a row error naming the others,
// rather than guessing which one is right.

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Roster.Engine.Core;

namespace Roster.Data.Feeds;

/// <param name="Rows">One row per key: the unique rows plus the first of each identical set.</param>
/// <param name="Errors">One error per row whose key has conflicting copies.</param>
/// <param name="Collapsed">Identical copies dropped, so the summary can say why counts differ.</param>
public sealed record DuplicateCheck<T>(IReadOnlyList<T> Rows, IReadOnlyList<RowError> Errors, int Collapsed);

public static class Duplicates
{
    /// <summary>
    /// Split <paramref name="rows"/> into what can load and what conflicts. <paramref name="label"/>
    /// names the key in the error message, e.g. "G001 on 2026-07-21".
    /// </summary>
    public static DuplicateCheck<T> CheckDuplicates<T>(
        IReadOnlyList<T> rows, Func<T, string> keyOf, Func<T, string> label, string? field = null)
        where T : class, IFeedRow
    {
        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var k = keyOf(r);
            if (groups.TryGetValue(k, out var g)) g.Add(r);
            else groups[k] = [r];
        }

        var contents = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        string Content(T row)
        {
            if (!contents.TryGetValue(row, out var c)) contents[row] = c = RowContent.Of(row);
            return c;
        }

        var kept = new List<T>();
        var errors = new List<RowError>();
        var collapsed = 0;
        foreach (var r in rows)
        {
            var group = groups[keyOf(r)];
            if (group.Count == 1)
            {
                kept.Add(r);
                continue;
            }
            var first = Content(group[0]);
            if (group.All(g => Content(g) == first))
            {
                if (ReferenceEquals(group[0], r)) kept.Add(r);
                else collapsed++;
                continue;
            }
            var others = group.Where(g => !ReferenceEquals(g, r)).Select(g => g.Row.ToString(CultureInfo.InvariantCulture)).ToList();
            errors.Add(new RowError(
                r.Row,
                r.StoreCode,
                field,
                $"{label(r)} appears more than once with different values " +
                $"(also row{(others.Count > 1 ? "s" : "")} {string.Join(", ", others)}). " +
                "Keep the right one and upload again."));
        }
        return new DuplicateCheck<T>(kept, errors, collapsed);
    }

    /// <summary>Merge error lists into file order (a stable sort by row).</summary>
    public static List<RowError> ByRow(IEnumerable<RowError> errors) => errors.OrderBy(e => e.Row).ToList();
}

/// <summary>
/// A row's content without its line number, for "identical copy" checks: the TypeScript's
/// key-order-independent JSON. Properties (and dictionary keys) are written in ordinal
/// order, numbers as JavaScript prints them (so -0 reads as 0, and NaN as null).
/// </summary>
internal static class RowContent
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertiesOf = new();

    public static string Of(object row)
    {
        var sb = new StringBuilder();
        Write(sb, row, top: true);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v, bool top = false)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case double d:
                sb.Append(double.IsFinite(d) ? Js.NumberToString(d) : "null");
                return;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            case DateOnly date:
                WriteString(sb, date.ToIso());
                return;
            case IDictionary dict:
            {
                var keys = dict.Keys.Cast<object>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)!).ToList();
                var entries = keys.Zip(dict.Values.Cast<object?>()).OrderBy(e => e.First, StringComparer.Ordinal);
                sb.Append('{');
                var first = true;
                foreach (var (k, value) in entries)
                {
                    if (!first) sb.Append(',');
                    WriteString(sb, k);
                    sb.Append(':');
                    Write(sb, value);
                    first = false;
                }
                sb.Append('}');
                return;
            }
            case IEnumerable list:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    Write(sb, item);
                    first = false;
                }
                sb.Append(']');
                return;
            }
            default:
            {
                var props = PropertiesOf.GetOrAdd(v.GetType(), t => t
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray());
                sb.Append('{');
                var first = true;
                foreach (var p in props)
                {
                    // The line number is not content (delete rest.__row).
                    if (top && p.Name == nameof(IFeedRow.Row)) continue;
                    if (!first) sb.Append(',');
                    WriteString(sb, p.Name);
                    sb.Append(':');
                    Write(sb, p.GetValue(v));
                    first = false;
                }
                sb.Append('}');
                return;
            }
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
    }
}
