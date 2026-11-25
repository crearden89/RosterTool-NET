// Port of src/app/(app)/plan/non-op/nonOpShared.ts.
// What the Non-Op page and its add sheet share: the rows the page reads, and
// how a category, a day and a sum of minutes are shown.

using Roster.Engine.Core;

namespace Roster.Web.Features.NonOp;

/// <param name="DaysAllowed">Weekdays the item may be booked on, Monday = 0.</param>
public sealed record NonOpSub(
    Guid Id,
    Guid CategoryId,
    string Name,
    int? MinMinutes,
    int? MaxMinutes,
    int? DefaultMinutes,
    string? Note,
    IReadOnlyList<int> DaysAllowed);

public sealed record NonOpEntryRow(
    Guid Id,
    DateOnly Date,
    Guid? SubcategoryId,
    Guid? EmployeeId,
    int Minutes,
    string Description);

public sealed record NonOpCategoryRow(Guid Id, string Name);

public sealed record NonOpEmployee(Guid Id, string Name);

public static class NonOpShared
{
    // One colour per category, in the categories' own order (from the avatar
    // set; white text passes on each).
    private static readonly string[] CategoryColours = ["#3c3ea6", "#0f766e", "#b45309", "#be185d", "#0369a1"];

    public static string CategoryColour(IReadOnlyList<NonOpCategoryRow> categories, Guid? id)
    {
        var at = -1;
        for (var i = 0; i < categories.Count; i++)
        {
            if (categories[i].Id == id)
            {
                at = i;
                break;
            }
        }
        return CategoryColours[Math.Max(0, at) % CategoryColours.Length];
    }

    public static int MinutesOf(IEnumerable<NonOpEntryRow> entries)
    {
        var n = 0;
        foreach (var e in entries) n += e.Minutes;
        return n;
    }

    /// <summary>Minutes as hours to two decimals, as every figure on the page.</summary>
    public static string HoursText(double minutes) => Js.ToFixed(minutes / 60, 2);

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>"Mon", "Tue"... for a date.</summary>
    public static string WeekdayOf(DateOnly d) => Period.DayLabels[Period.DowMon0(d)];

    public static int DayNumber(DateOnly d) => d.Day;

    /// <summary>"Nov" for a date.</summary>
    public static string MonthOf(DateOnly d) => Dates.AuDayMonth(d).Split(' ')[1];

    public static bool IsWeekend(DateOnly d) => Period.DowMon0(d) >= 5;

    /// <summary>The items a store may book in a category on a date's weekday.</summary>
    public static List<NonOpSub> ItemsAllowed(IEnumerable<NonOpSub> subs, Guid? categoryId, DateOnly d)
    {
        var dow = Period.DowMon0(d);
        return subs.Where(s => s.CategoryId == categoryId && s.DaysAllowed.Contains(dow)).ToList();
    }
}
