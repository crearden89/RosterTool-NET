// Port of the number formatting in src/components/roster/RosterGrid.tsx (money, paidLen)
// and src/app/(app)/plan/roster/page.tsx (money, dollars).

using Roster.Engine.Core;

namespace Roster.Web.Features.RosterPage.Grid;

/// <summary>The grid's and the Roster page's numbers, written exactly as the TypeScript app writes them.</summary>
public static class GridFormat
{
    /// <summary><c>"$" + n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })</c>: "$1,234.56".</summary>
    public static string Money(double n) => "$" + Js.ToLocaleString(n, 2, 2);

    /// <summary><c>"$" + Math.round(n).toLocaleString("en-AU")</c>: whole dollars, "$1,235".</summary>
    public static string Dollars(double n) => "$" + Js.ToLocaleString(Js.Round(n));

    /// <summary>
    /// Paid length = clock span minus only the UNPAID break minutes; paid breaks
    /// still count toward paid hours. (The Break column separately shows the total
    /// time off the floor.)
    /// </summary>
    public static string PaidLen(double startMin, double endMin, double unpaidMin) =>
        Js.ToFixed((endMin - startMin - unpaidMin) / 60, 2);
}
