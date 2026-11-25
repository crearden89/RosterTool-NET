// New: the Team Members page's service - what page.tsx loads, and the server
// actions of app/(app)/setup/staff/actions.ts and setup/requests/actions.ts
// (day-off requests and fixed rosters, managed on Team Members since the
// Requests page became a redirect there), with the same checks in the same places.

namespace Roster.Web.Features.Team;

public interface ITeamService
{
    /// <summary>
    /// page.tsx: the store the page shows (a Store-tier user's own; otherwise the one
    /// asked for when in scope, else the one last chosen, else the first), its active
    /// team in rank order, and everything the cards and the editor show.
    /// </summary>
    Task<TeamPageData> LoadAsync(Guid? requestedStore, CancellationToken cancellationToken = default);

    // ─── setup/staff/actions.ts ──────────────────────────────────────────────

    /// <summary>
    /// addEmployee: a team member is added by POSITION, which sets the base role and the
    /// tenure band. A JSA is casual: no contract size. Starts available every day.
    /// </summary>
    Task<SaveResult> AddEmployeeAsync(Guid? storeId, string name, string positionCode, double contractHours, CancellationToken cancellationToken = default);

    /// <summary>
    /// setAvailability: the weekly availability, Monday to Sunday. Windows are cleared
    /// when a day is marked unavailable.
    /// </summary>
    Task<SaveResult> SetAvailabilityAsync(Guid employeeId, IReadOnlyList<AvailabilityDay> days, CancellationToken cancellationToken = default);

    /// <summary>deleteEmployee.</summary>
    Task<SaveResult> DeleteEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>setRestrictedTasks: replaces the person's restricted labour groups.</summary>
    Task<SaveResult> SetRestrictedTasksAsync(Guid employeeId, IReadOnlyList<string> groups, CancellationToken cancellationToken = default);

    /// <summary>
    /// addLeave: dayHours = hours credited per leave weekday; null = a full contract
    /// day, which resolves to the person's notional hours at roster time.
    /// </summary>
    Task<SaveResult> AddLeaveAsync(Guid employeeId, DateOnly start, DateOnly end, string leaveType = "ANN", double? dayHours = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// setContractAndNotional: the contract size, the notional day value (null clears the
    /// override back to fortnight contract / 10) and the start date (null = experienced).
    /// </summary>
    Task<SaveResult> SetContractAndNotionalAsync(Guid employeeId, double contractHours, double? notionalHours, DateOnly? startDate = null, CancellationToken cancellationToken = default);

    /// <summary>clearLeave: removes one booking, scope-checked through its owner.</summary>
    Task<SaveResult> ClearLeaveAsync(Guid leaveId, CancellationToken cancellationToken = default);

    /// <summary>
    /// setPosition: a promotion or a pay-year step. The base role and tenure band follow
    /// from the position; a JSA's contract goes to 0, and <paramref name="contractHours"/>,
    /// when given, sets the contract size the new position implies.
    /// </summary>
    Task<SaveResult> SetPositionAsync(Guid employeeId, string positionCode, double? contractHours = null, CancellationToken cancellationToken = default);

    // ─── setup/requests/actions.ts ───────────────────────────────────────────

    /// <summary>addDayOff: a pending request; submittedAt null = now.</summary>
    Task<SaveResult> AddDayOffAsync(Guid employeeId, DateOnly date, DateTimeOffset? submittedAt, string notes, CancellationToken cancellationToken = default);

    /// <summary>
    /// grantAllPending: every pending request of the store's team, in one go. A granted
    /// weekend day still switches on its weekend-off marker.
    /// </summary>
    Task<SaveResult> GrantAllPendingAsync(Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// setRequestStatus: pending, granted or declined. A weekend day keeps its
    /// weekend-off switch in step (granted switches it on, anything else off).
    /// </summary>
    Task<SaveResult> SetRequestStatusAsync(Guid requestId, string status, CancellationToken cancellationToken = default);

    /// <summary>deleteRequest.</summary>
    Task<SaveResult> DeleteRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// setFixedRosterCycle: the whole cycle in one action - 1, 2 or 4 weekly templates
    /// and the date Week 1 starts (anchored on its Monday). Days not given are cleared;
    /// no days removes the fixed roster and its cycle. A shift that breaks a shift rule
    /// is refused with the reason (owner decision R6).
    /// </summary>
    Task<SaveResult> SetFixedRosterCycleAsync(Guid employeeId, int cycleWeeks, DateOnly? startDate, IReadOnlyList<FixedDay> days, CancellationToken cancellationToken = default);

    /// <summary>clearFixedRoster: every week's locked shifts and the cycle.</summary>
    Task<SaveResult> ClearFixedRosterAsync(Guid employeeId, CancellationToken cancellationToken = default);
}
