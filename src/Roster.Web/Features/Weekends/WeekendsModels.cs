// New: the Weekends page's view models and action inputs - the rows
// app/(app)/setup/weekends/page.tsx selects and hands to WeekendsClient, and
// the argument of saveWeekendMarkers (setup/weekends/actions.ts).

using Roster.Web.Components.Ui;

namespace Roster.Web.Features.Weekends;

/// <summary>A team member as the Weekends page lists them.</summary>
public sealed record WeekendsMember(Guid Id, string Name, string Role, double ContractHours);

/// <summary>A weekend-off marker (weekend_markers), keyed on the weekend's Saturday.</summary>
public sealed record WeekendMarkerRow(Guid EmployeeId, DateOnly WeekendStart, bool SatOff, bool SunOff);

/// <summary>One edited weekend of <see cref="IWeekendsService.SaveWeekendMarkersAsync"/>.</summary>
public sealed record WeekendMarkerInput(Guid EmployeeId, DateOnly WeekendStart, bool SatOff, bool SunOff);

/// <summary>Everything the Weekends page shows: what page.tsx loads for WeekendsClient.</summary>
public sealed record WeekendsPageData
{
    /// <summary>The store the page is on; null when the person has no store in scope.</summary>
    public required Guid? StoreId { get; init; }

    /// <summary>The store picker's stores; empty for a Store-tier user.</summary>
    public required IReadOnlyList<StoreOption> Stores { get; init; }

    /// <summary>The store's active team, by role (SM first) then name.</summary>
    public required IReadOnlyList<WeekendsMember> Employees { get; init; }

    /// <summary>
    /// The four Saturdays of the window, oldest first: the previous fortnight's two
    /// (context) then the selected fortnight's two (editable).
    /// </summary>
    public required IReadOnlyList<DateOnly> Weekends { get; init; }

    /// <summary>The team's markers on those four weekends.</summary>
    public required IReadOnlyList<WeekendMarkerRow> Markers { get; init; }

    /// <summary>The selected fortnight (a period start on the national grid).</summary>
    public required DateOnly PeriodStart { get; init; }

    /// <summary>PA and DSO: the page shows, and changes nothing.</summary>
    public required bool ReadOnly { get; init; }
}
