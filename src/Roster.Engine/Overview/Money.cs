// New: the money and count formats the Dashboard and Analysis share (money0, count0 in
// the TypeScript pages), in one place.

using Roster.Engine.Core;

namespace Roster.Engine.Overview;

public static class Money
{
    /// <summary><c>"$" + n.toLocaleString(undefined, { maximumFractionDigits: 0 })</c>: "$1,546,977".</summary>
    public static string Money0(double n) => "$" + Js.ToLocaleString(n, 0);

    /// <summary><c>Math.round(n).toLocaleString()</c>: whole numbers with separators.</summary>
    public static string Count0(double n) => Js.ToLocaleString(Js.Round(n));
}
