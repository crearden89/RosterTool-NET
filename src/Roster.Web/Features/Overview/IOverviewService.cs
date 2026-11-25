// Port of the reads behind the Dashboard: src/app/(app)/overview/page.tsx (which stores,
// which month), src/lib/overview/compute.ts (computeOverview's reads) and
// src/lib/overview/dashboard.ts (loadStoreFortnight, loadAreaStores, and YourStores.tsx's
// remit). The figures themselves are worked out in Roster.Engine.Overview.

using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Overview;

namespace Roster.Web.Features.Overview;

/// <summary>A store in the Dashboard's pickers (every store: the Dashboard is peer-visible).</summary>
public sealed record DashboardStore(Guid Id, string Code, string Name, Guid? RegionId, Guid? AreaId);

public sealed record DashboardRegion(Guid Id, string Name);

/// <summary>What page.tsx hands the Dashboard.</summary>
/// <param name="Data">The month's figures; null when no store is in scope.</param>
/// <param name="SelectedStore">The store picked (null = a roll-up, or none).</param>
/// <param name="IsRollup">PA, DSO and EMSO logins land on an aggregate by default.</param>
public sealed record DashboardPage(
    ScopedUser User,
    OverviewData? Data,
    DateOnly Today,
    Guid? SelectedStore,
    bool IsRollup,
    IReadOnlyList<DashboardStore> Stores,
    IReadOnlyList<DashboardRegion> Regions,
    DateOnly MonthStart,
    string MonthLabel,
    DateOnly PrevMonth,
    DateOnly NextMonth);

/// <summary>One store's fortnight on the Dashboard (dashboard.ts StoreFortnight).</summary>
/// <param name="OverduePeriods">Earlier fortnights whose roster still isn't on the floor.</param>
public sealed record StoreFortnight(
    FortnightState State,
    IReadOnlyList<DateOnly> OverduePeriods,
    FortnightFigures Figures,
    IReadOnlyList<NeedsItem> Needs);

/// <summary>One row of "Your stores" (dashboard.ts AreaStoreRow).</summary>
public sealed record AreaStoreRow(Guid Id, string Code, string Name, StoreStatus Status, int Requests, double? OeMtd);

/// <param name="Period">The fortnight being planned.</param>
/// <param name="NoRemit">The login has no area or region set (an empty remit).</param>
public sealed record YourStoresView(DateOnly Period, IReadOnlyList<AreaStoreRow> Rows, bool NoRemit);

public interface IOverviewService
{
    /// <summary>
    /// page.tsx: the stores and regions, which store (or roll-up) and month to show, and the
    /// month's figures. <paramref name="store"/> and <paramref name="month"/> are the query
    /// values as given. Null when nobody is signed in.
    /// </summary>
    Task<DashboardPage?> LoadDashboardAsync(string? store, string? month, CancellationToken ct = default);

    /// <summary>
    /// loadStoreFortnight: the fortnight the Plan page opens on for the store, read from its
    /// review and published days, and what the store has to do. Null when the review can't
    /// be read (the store is not visible). Throws when a read fails.
    /// </summary>
    Task<StoreFortnight?> LoadStoreFortnightAsync(Guid storeId, DateOnly today, CancellationToken ct = default);

    /// <summary>
    /// YourStores + loadAreaStores: every store the signed-in area login looks after, with the
    /// fortnight being planned. Throws when a read fails.
    /// </summary>
    Task<YourStoresView> LoadYourStoresAsync(DateOnly today, CancellationToken ct = default);
}

internal static class TierChecks
{
    /// <summary>The roll-up tiers: PA, DSO ("director") and EMSO.</summary>
    public static bool IsAreaLogin(Tier tier) => tier is Tier.Pa or Tier.Director or Tier.Emso;
}
