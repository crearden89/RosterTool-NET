// New: a function of a minute, rebuilt from the corpus's table of it. The
// painter's allowedAt and the shaper's allowedAt/placeAt are functions in the
// TypeScript; the dumper writes each as its value at every quarter-hour of the
// day and at every other minute the TypeScript engine asked about, listed only
// where it differs from its value outside the day:
//   { "default": [...], "at": [[minute, [...]], ...] }

using System.Text.Json.Nodes;
using Roster.Engine.Core;

namespace Roster.Engine.Tests.Parity;

public static class TimeTable
{
    /// <summary>The function the table describes; null when the call passed none.</summary>
    public static Func<double, IReadOnlyList<string>?>? From(JsonNode? table)
    {
        if (table is not JsonObject o) return null;
        var dflt = ParityJson.Read<List<string>?>(o["default"]);
        var at = new JsMap<double, IReadOnlyList<string>?>();
        foreach (var entry in o["at"]!.AsArray())
        {
            var pair = entry!.AsArray();
            at.Set(pair[0]!.GetValue<double>(), ParityJson.Read<List<string>?>(pair[1]));
        }
        return t => at.TryGet(t, out var v) ? v : dflt;
    }
}
