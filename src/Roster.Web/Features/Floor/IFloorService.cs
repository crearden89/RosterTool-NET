// Port of the data side of src/app/floor/[token]/page.tsx and walk-actions.ts:
// what the Floor Display reads and the four things it can change, all through
// the store's secret kiosk token.

using Roster.Web.Kiosk;

namespace Roster.Web.Features.Floor;

/// <summary>
/// The Floor Display's reads and actions. The actions authenticate on the store's
/// kiosk token rather than a session (the kiosk is a no-login surface), exactly
/// as the Load Planner's do, and everything they touch is scoped to the store
/// that token resolves to; a caller can never name a store id directly.
/// </summary>
public interface IFloorService
{
    /// <summary>
    /// The page for a kiosk link the page has already checked (IKioskLinkCheck, Open).
    /// <paramref name="date"/> is the link's ?date= (another day: no now, no
    /// ticking; anything but yyyy-mm-dd is ignored); <paramref name="view"/> is its
    /// ?view= ("notes" is the walk notes form).
    /// </summary>
    Task<FloorPageData> LoadAsync(string token, KioskLink link, string? date, string? view, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raise a note. The assignee is resolved against the store's OWN roster for
    /// today, so a crafted employee id from another store cannot be attached; the
    /// name is stored alongside the id because the note outlives today's roster.
    /// </summary>
    Task<KioskResult> AddWalkNoteAsync(string token, string rawNote, string? assignedEmployeeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tick a note off. Scoped by store id as well as note id, so a note id lifted
    /// from one kiosk cannot be completed through another store's token.
    /// </summary>
    Task<KioskResult> CompleteWalkNoteAsync(string token, string noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undo a completion. The kiosk is a shared tablet with no login, so a mis-tap
    /// has to be recoverable by whoever is standing there.
    /// </summary>
    Task<KioskResult> ReopenWalkNoteAsync(string token, string noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tick (or untick) an admin task for the store's today. The task NAME is the
    /// handle; it is length-capped only to stop junk writes, since it is matched,
    /// not executed. Toggle rather than set-once: a mis-tap on a shared tablet must
    /// be reversible by whoever is standing there.
    /// </summary>
    Task<KioskResult> SetAdminTaskCheckAsync(string token, string taskName, bool isChecked, CancellationToken cancellationToken = default);
}
