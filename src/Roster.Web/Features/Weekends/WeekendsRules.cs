// Port of the pure parts of app/(app)/setup/weekends/page.tsx (the team's
// order; the window itself is Roster.Engine.Core.WeekendWindow) and
// WeekendsClient.tsx (the edit keys and the X/4 count).

using Roster.Engine.Core;

namespace Roster.Web.Features.Weekends;

public static class WeekendsRules
{
    private static readonly string[] RoleOrder = ["SM", "ASM", "DSM", "RSM", "SA", "JSA"];

    /// <summary>
    /// The team by role, managers first, then by name. (A role outside the list sorts
    /// before SM, as indexOf's -1 does.)
    /// </summary>
    public static List<WeekendsMember> SortTeam(IEnumerable<WeekendsMember> employees) =>
        Js.Sorted(employees, (a, b) =>
            ((double)(Array.IndexOf(RoleOrder, a.Role) - Array.IndexOf(RoleOrder, b.Role))).Or(Js.LocaleCompare(a.Name, b.Name)));

    /// <summary>The page's key for one person's weekend: "{employee}:{saturday}".</summary>
    public static string Key(Guid employeeId, DateOnly weekendStart) => $"{employeeId}:{weekendStart.ToIso()}";

    /// <summary>How many of the window's weekends are full weekends off (both days).</summary>
    public static int FullOffCount(IEnumerable<DateOnly> weekends, Func<DateOnly, (bool Sat, bool Sun)> get) =>
        weekends.Count(wk =>
        {
            var g = get(wk);
            return g.Sat && g.Sun;
        });

    /// <summary>The first editable weekend: the two most recent weekends are editable.</summary>
    public static int EditableFrom(int weekendCount) => weekendCount - 2;
}
