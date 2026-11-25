// Port of src/components/roster/RosterRowEdit.tsx (RowEditApi, hhmm, toMin).
// The editable cells of a roster row (NameCell.razor, TimeCells.razor) are kept
// apart from the grid, so the grid itself stays a plain display for the read-only
// surfaces, which never pass an editor.

using Microsoft.AspNetCore.Components;
using Roster.Engine.Core;
using Roster.Web.Components.Ui;

namespace Roster.Web.Features.RosterPage.Grid;

/// <summary>
/// Inline row editing (Roster view): the Name cell becomes a type-ahead person
/// picker and Start/Finish become time inputs, so a shift is changed where it is
/// read instead of in a separate panel below the grid.
/// </summary>
public sealed record RowEditApi
{
    public IReadOnlyList<ComboOption> Employees { get; init; } = [];
    public bool Busy { get; init; }

    /// <summary>Rows that may be edited - open (unfilled) shifts have no person to swap.</summary>
    public IReadOnlySet<string> EditableKeys { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> LockedKeys { get; init; } = new HashSet<string>();

    /// <summary>The row's person changed: (row key, the new employee's id).</summary>
    public EventCallback<(string RowKey, string EmployeeId)> OnPerson { get; init; }

    /// <summary>The row's times changed: (row key, start, end) in minutes from midnight.</summary>
    public EventCallback<(string RowKey, double StartMin, double EndMin)> OnTimes { get; init; }

    public EventCallback<string> OnRemove { get; init; }
    public EventCallback<string> OnToggleLock { get; init; }
}

/// <summary>The time inputs' text: "HH:MM" for minutes from midnight, and back.</summary>
public static class RowEditFormat
{
    /// <summary><c>hhmm</c>: minutes from midnight as "HH:MM" (no rounding).</summary>
    public static string Hhmm(double min) =>
        $"{Js.PadStart(Js.NumberToString(Math.Floor(min / 60)), 2, '0')}:{Js.PadStart(Js.NumberToString(min % 60), 2, '0')}";

    /// <summary><c>toMin</c>: "HH:MM" as minutes from midnight; a part that is not a number counts as 0.</summary>
    public static double ToMin(string s)
    {
        var parts = s.Split(':');
        var h = Js.Number(parts[0]);
        var m = Js.Number(parts.Length > 1 ? parts[1] : null);
        return h.Or(0) * 60 + m.Or(0);
    }
}
