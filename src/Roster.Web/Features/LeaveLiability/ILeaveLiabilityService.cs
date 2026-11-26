// Port of src/app/(app)/admin/leave-liability/page.tsx (the reads, with the loading
// half of src/lib/leave/compute.ts) and actions.ts (the four saves).

using Roster.Data.Scope;
using Roster.Engine.Leave;

namespace Roster.Web.Features.LeaveLiability;

/// <summary>The page's query: ?store=, ?period=, ?tab=, ?scope=.</summary>
public sealed record LeavePageQuery(Guid? Store, DateOnly? Period, string? Tab, string? Scope);

/// <summary>A store in the page's store picker (the viewer's own remit).</summary>
public sealed record LeaveStoreOption(Guid Id, string Code, string Name);

/// <summary>Everything the page shows (page.tsx's props for LeaveClient).</summary>
/// <param name="View">The selected store's figures; null when it has no leave data loaded.</param>
/// <param name="Network">The in-scope stores' league table; its Periods drive the pay-period picker.</param>
/// <param name="ShowNetwork">Store logins get no cross-store league table at all.</param>
/// <param name="CanSeeNational">PA/DSO only - the switch from their own remit out to every store.</param>
/// <param name="CanEditStore">May write to the selected store - gates the cover cap.</param>
/// <param name="IsSuperUser">Gates the NATIONAL config: target, FTE divisor, accrual bands.</param>
/// <param name="InitialTab">"overview", "team", "calendar", "network" or "settings".</param>
/// <param name="Today">Today's date in Australia (the Calendar opens on it).</param>
public sealed record LeavePageData(
    Guid? StoreId,
    IReadOnlyList<LeaveStoreOption> Stores,
    LeaveStoreView? View,
    LeaveNetworkView Network,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<AccrualBand> Bands,
    double Target,
    double FteDivisor,
    int MaxStaffOnLeave,
    bool ShowNetwork,
    bool CanSeeNational,
    NetworkScope NetworkScope,
    bool CanEditStore,
    bool IsSuperUser,
    string InitialTab,
    DateOnly Today);

/// <summary>A save's answer: ok, the reason it was refused, or where a refused tier is sent.</summary>
/// <param name="RedirectTo">Set when the caller lacks the tier (requireTier's redirect to /overview).</param>
public sealed record LeaveResult(bool Ok, string? Error = null, string? RedirectTo = null)
{
    public static readonly LeaveResult Success = new(true);
}

/// <summary>The Leave Liability page's reads and saves, as the signed-in user.</summary>
public interface ILeaveLiabilityService
{
    Task<LeavePageData> LoadAsync(LeavePageQuery query, CancellationToken ct = default);

    /// <summary>saveLeaveSettings: the national target and FTE divisor (super user).</summary>
    Task<LeaveResult> SaveLeaveSettingsAsync(double targetHoursPerFte, double fteDivisor, CancellationToken ct = default);

    /// <summary>saveAccrualBand: one (contract, role) band (super user).</summary>
    Task<LeaveResult> SaveAccrualBandAsync(
        double contractHours, string roleClass, double leaveNotionalHours, double accrualWeeks, double annualAccrualHours,
        CancellationToken ct = default);

    /// <summary>deleteAccrualBand (super user).</summary>
    Task<LeaveResult> DeleteAccrualBandAsync(double contractHours, string roleClass, CancellationToken ct = default);

    /// <summary>saveMaxOnLeave: the store's cover cap (whoever may edit the store).</summary>
    Task<LeaveResult> SaveMaxOnLeaveAsync(Guid storeId, double maxStaffOnLeave, CancellationToken ct = default);
}
