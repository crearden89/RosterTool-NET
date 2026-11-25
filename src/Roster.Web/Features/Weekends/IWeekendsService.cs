// New: the Weekends page's service - what app/(app)/setup/weekends/page.tsx
// loads, and the server actions of setup/weekends/actions.ts, with the same
// checks in the same places.

using Roster.Web.Features.Team;

namespace Roster.Web.Features.Weekends;

public interface IWeekendsService
{
    /// <summary>
    /// page.tsx: the store the page shows, the fortnight (<paramref name="start"/> when it
    /// is a period start, else the one the Plan screen opens on, so the two pages always
    /// agree), the store's active team and their markers on the window's four weekends.
    /// </summary>
    Task<WeekendsPageData> LoadAsync(Guid? requestedStore, DateOnly? start, CancellationToken cancellationToken = default);

    /// <summary>setWeekendMarker: one person's weekend.</summary>
    Task<SaveResult> SetWeekendMarkerAsync(Guid employeeId, DateOnly weekendStart, bool satOff, bool sunOff, CancellationToken cancellationToken = default);

    /// <summary>
    /// saveWeekendMarkers: every edited weekend in one save, refused whole if any of
    /// the people is out of the caller's scope.
    /// </summary>
    Task<SaveResult> SaveWeekendMarkersAsync(IReadOnlyList<WeekendMarkerInput> markers, CancellationToken cancellationToken = default);
}
