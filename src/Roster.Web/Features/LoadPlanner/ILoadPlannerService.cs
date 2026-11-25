// Port of the data side of src/app/load/[token]/page.tsx and actions.ts.

using Roster.Web.Features.Floor;
using Roster.Web.Kiosk;

namespace Roster.Web.Features.LoadPlanner;

/// <summary>Everything the server reads for the page, ready to plan from (LoadPlanner.tsx's LoadPageData).</summary>
public sealed record LoadPageData(
    string Token,
    string Store, // "Example Store · G101", as the Floor Display heads it
    string? State, // the store's state, for its clock
    DateOnly Day, // the store's today
    int Now, // the store's time when the page was read, in minutes
    bool RosterApproved,
    IReadOnlyList<CrewOption> Crew, // everyone with Load Running tiles on the approved roster
    IReadOnlyList<LoadTypeOption> Types, // every load type, in sort order, with the day's count
    bool HasEntry, // a load has been saved for the day
    int? EnteredAt, // when it was last saved, store minutes
    IReadOnlyDictionary<string, string> Pins, // today's manual rows: employee -> load type
    IReadOnlyDictionary<string, double> TypeCounts, // `${key}:${type}` -> days owned this fortnight
    bool FromFloor) // opened from the Floor Display's strip, in a tab of its own
{
    public PlanInputs Inputs => new(Crew, Types, TypeCounts);

    /// <summary>The header's "No login needed" pill: gone when Kiosk:RequireDeviceSignIn makes the device sign in.</summary>
    public bool NoLoginNeeded { get; init; } = true;
}

/// <summary>markLoadComplete's answer: when it was marked, on success.</summary>
public sealed record LoadCompleteResult(bool Ok, string? Error = null, DateTimeOffset? At = null);

/// <summary>
/// The Load Planner's reads and actions. Deliberately PUBLIC, like the Floor
/// Display: anyone in the store may enter or correct the truck's contents. The
/// actions authenticate on the store's kiosk token, and their input is tightly
/// bounded: only known load types, only the store's today, quantities 0-99.
/// </summary>
public interface ILoadPlannerService
{
    /// <summary>
    /// The page for a kiosk link the page has already checked (IKioskLinkCheck, Open).
    /// Records today's AUTOMATIC load ownership as it reads, as the TypeScript page does.
    /// </summary>
    Task<LoadPageData> LoadAsync(string token, KioskLink link, bool fromFloor, CancellationToken cancellationToken = default);

    /// <summary>Save (or re-save) today's load for the store.</summary>
    Task<KioskResult> SaveDailyLoadAsync(string token, IReadOnlyList<(string LoadTypeId, double Qty)> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// SM edit mode: pin (or unpin, <paramref name="loadType"/> null) a crew member's
    /// owned category for today. The employee is verified against THIS store's roster
    /// for today and the name derived server-side; <paramref name="employeeName"/> is
    /// never trusted.
    /// </summary>
    Task<KioskResult> SetLoadAssignmentAsync(string token, string employeeId, string employeeName, string? loadType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark today's load complete. Not persisted yet, as in the TypeScript app (the
    /// load completion history lands with the reporting schema): it validates the
    /// store and says when.
    /// </summary>
    Task<LoadCompleteResult> MarkLoadCompleteAsync(string token, CancellationToken cancellationToken = default);
}
