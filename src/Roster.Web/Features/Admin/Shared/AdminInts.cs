// New: integer columns (Postgres INT) fed from number inputs. Postgres refused a
// fraction with its own message; the same message is returned here.

using Roster.Engine.Core;

namespace Roster.Web.Features.Admin.Shared;

public static class AdminInts
{
    /// <summary>The value as an int when it is a whole number Postgres would store, else null.</summary>
    public static int? AsInt(double v) =>
        double.IsFinite(v) && Math.Floor(v) == v && v >= int.MinValue && v <= int.MaxValue ? (int)v : null;

    /// <summary>Postgres's refusal of a fraction for an integer column.</summary>
    public static string BadInt(double v) =>
        $"invalid input syntax for type integer: \"{Js.NumberToString(v)}\"";
}
