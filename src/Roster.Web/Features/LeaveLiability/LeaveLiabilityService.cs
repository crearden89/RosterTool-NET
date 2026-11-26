// Port of src/app/(app)/admin/leave-liability/page.tsx, actions.ts and the loading
// half of src/lib/leave/compute.ts (getLeaveConfig, getPeriodEnds, computeLeaveStore,
// computeLeaveNetwork, computeLeaveTrend). The figures are assembled by
// Roster.Engine.Leave.LeaveCompute from the rows read here.
//
// Reads go through a system context, as the TypeScript's went through the
// service-role client: the page decides its own scope (the store picker is the
// viewer's remit; the network table is networkStoreIds).

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Leave;

namespace Roster.Web.Features.LeaveLiability;

public sealed class LeaveLiabilityService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IRememberedStore remembered,
    IDatabaseFunctions functions,
    IRefData refData,
    TimeProvider clock) : ILeaveLiabilityService
{
    public async Task<LeavePageData> LoadAsync(LeavePageQuery query, CancellationToken ct = default)
    {
        var user = scope.User;
        await using var db = contexts.CreateSystem("leave liability: the page decides its own scope");
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new { s.Id, s.Code, s.Name, s.RegionId, s.AreaId }).ToListAsync(ct);
        var regions = await db.Regions.AsNoTracking().Select(r => new ScopeRegion(r.Id, r.DirectorGroupId)).ToListAsync(ct);
        var scopeStores = stores.Select(s => new ScopeStore(s.Id, s.RegionId, s.AreaId)).ToList();

        // The STORE SWITCHER is limited to the viewer's own remit, and never widened
        // by the national toggle. That toggle is about the Network league table -
        // aggregate hours per store. These tabs show named employees and their
        // balances, which nobody should read outside the stores they look after.
        IReadOnlyList<Guid>? remit = user is null ? [] : ReportingScope.RemitStoreIds(user, scopeStores, regions);
        var myStores = remit is null ? stores : stores.Where(s => remit.Contains(s.Id)).ToList();
        // ResolveStoreId only honours ?store= when it is in this list, so an
        // out-of-scope store id in the URL falls back rather than resolving.
        var storeId = StoreScope.ResolveStoreId(query.Store, myStores.Select(s => s.Id).ToList(), remembered.StoreId);

        // Which stores the Network tab may aggregate. Store logins get no Network tab
        // at all; EMSO gets their area; PA/DSO get their remit with a switch to
        // national. NetworkStoreIds refuses to honour ?scope=national for a tier that
        // is not entitled to it.
        var netScope = query.Scope == "national" ? NetworkScope.National : NetworkScope.Remit;
        IReadOnlyList<Guid>? scopedStoreIds = user is null ? [] : ReportingScope.NetworkStoreIds(user, scopeStores, regions, netScope);
        var showNetwork = user is not null && ReportingScope.CanSeeNetwork(user.Tier);

        // Settings mixes a national block (target, FTE divisor, accrual bands) with
        // this store's own cover cap, so the two halves are gated separately.
        var selected = stores.FirstOrDefault(s => s.Id == storeId);
        var canEditStore = selected is not null && user is not null
            && AccessMatrix.StoreAccessFor(user, selected.Id, selected.AreaId) == StoreAccess.Write;
        var isSuperUser = user?.Tier == Tier.SuperUser;

        var config = await GetLeaveConfigAsync(db, ct);
        var today = Dates.TodayAu(clock.GetUtcNow());
        var view = storeId is { } sid ? await ComputeLeaveStoreAsync(db, config, sid, query.Period, today, ct) : null;
        // Always computed, even when the tab is hidden: its periods list drives the
        // pay-period selector that every tab sits under.
        var network = await ComputeLeaveNetworkAsync(db, config, query.Period, scopedStoreIds, ct);
        var trend = storeId is { } tid ? await ComputeLeaveTrendAsync(db, config, tid, ct) : [];
        // Read on its own rather than off the store view: the cover cap is editable
        // under Settings whether or not this store has leave data loaded yet.
        var maxStaffOnLeave = storeId is { } aid ? await MaxStaffOnLeaveAsync(db, aid, ct) : 3;

        var tab = query.Tab;
        var initialTab = tab is "team" or "calendar"
            ? tab
            : (tab == "network" && showNetwork) || (tab == "settings" && (canEditStore || isSuperUser))
                ? tab
                : "overview";

        return new LeavePageData(
            StoreId: storeId,
            Stores: myStores.Select(s => new LeaveStoreOption(s.Id, s.Code, s.Name)).ToList(),
            View: view,
            Network: network,
            Trend: trend,
            Bands: config.Bands,
            Target: config.TargetHoursPerFte,
            FteDivisor: config.FteDivisor,
            MaxStaffOnLeave: maxStaffOnLeave,
            ShowNetwork: showNetwork,
            CanSeeNational: user is not null && ReportingScope.CanSeeNational(user.Tier),
            NetworkScope: netScope,
            CanEditStore: canEditStore,
            IsSuperUser: isSuperUser,
            InitialTab: initialTab!,
            Today: today);
    }

    // ─── compute.ts: the loading ───────────────────────────────────────────────

    /// <summary>getLeaveConfig: the bands (by role, then contract largest first) and the national settings.</summary>
    internal static async Task<LeaveConfig> GetLeaveConfigAsync(RosterDbContext db, CancellationToken ct)
    {
        var bands = await db.LeaveAccrualBands.AsNoTracking()
            .OrderBy(b => b.RoleClass).ThenByDescending(b => b.ContractHours)
            .Select(b => new AccrualBand(b.ContractHours, b.RoleClass, b.LeaveNotionalHours, b.AccrualWeeks, b.AnnualAccrualHours))
            .ToListAsync(ct);
        var settings = await db.LeaveSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id, ct);
        return new LeaveConfig(bands, settings?.TargetHoursPerFte ?? 76, settings?.FteDivisor ?? 76);
    }

    // Exactly the columns ToRow reads. The TypeScript asked for no order (the
    // database's own, the upload's); here they come by UKG employee id.
    private static IQueryable<LiabilityRow> Rows(IQueryable<LeaveLiabilitySnapshot> q) =>
        q.OrderBy(s => s.UkgEmployeeId).Select(r => new LiabilityRow
        {
            UkgEmployeeId = r.UkgEmployeeId,
            EmployeeName = r.EmployeeName,
            Job = r.Job,
            RoleClass = r.RoleClass,
            ContractHours = r.ContractHours,
            NotionalHours = r.NotionalHours,
            AnnualHours = r.AnnualHours,
            SickHours = r.SickHours,
            LieuHours = r.LieuHours,
            BankedHours = r.BankedHours,
            Banked2Hours = r.Banked2Hours,
        });

    private async Task<LeaveStoreView?> ComputeLeaveStoreAsync(
        RosterDbContext db, LeaveConfig config, Guid storeId, DateOnly? periodEnd, DateOnly today, CancellationToken ct)
    {
        var periods = await functions.LeavePeriodEndsAsync(storeId, ct);
        var (current, previous) = LeaveCompute.PickPeriods(periods, periodEnd);
        if (current is not { } cur) return null;

        var snaps = db.LeaveLiabilitySnapshots.AsNoTracking().Where(s => s.StoreId == storeId);
        var rows = await Rows(snaps.Where(s => s.PeriodEnd == cur)).ToListAsync(ct);
        List<LiabilityRow>? prevRows = previous is { } prev ? await Rows(snaps.Where(s => s.PeriodEnd == prev)).ToListAsync(ct) : null;
        var requests = await db.LeaveRequestsUkg.AsNoTracking().Where(r => r.StoreId == storeId)
            .OrderBy(r => r.UkgEmployeeId).ThenBy(r => r.StartDate)
            .Select(r => new LeaveRequestRow(r.UkgEmployeeId, r.EmployeeName, r.LeaveType, r.StartDate, r.EndDate, r.Status))
            .ToListAsync(ct);
        var maxStaffOnLeave = await MaxStaffOnLeaveAsync(db, storeId, ct);
        // Real 3-tier public holidays (national/state/local) - the workbook
        // hand-maintained its own list, Melbourne Cup note and all.
        var holidays = await refData.GetHolidaySetAsync(storeId, ct);

        return LeaveCompute.BuildStoreView(config, today, cur, previous, rows, prevRows, requests, maxStaffOnLeave, holidays.ToList());
    }

    /// <summary>
    /// Every IN-SCOPE store's liability for one period - the view the workbook can't
    /// give. <paramref name="storeIds"/> is the viewer's reporting scope: null is the
    /// whole network, a list narrows to it, and an EMPTY list yields nothing.
    /// </summary>
    private async Task<LeaveNetworkView> ComputeLeaveNetworkAsync(
        RosterDbContext db, LeaveConfig config, DateOnly? periodEnd, IReadOnlyList<Guid>? storeIds, CancellationToken ct)
    {
        var periods = await functions.LeavePeriodEndsAsync(null, ct);
        var (current, _) = LeaveCompute.PickPeriods(periods, periodEnd);
        if (current is not { } cur) return new LeaveNetworkView(null, [], config.TargetHoursPerFte, periods);

        var storeQ = db.Stores.AsNoTracking().AsQueryable();
        if (storeIds is not null) storeQ = storeQ.Where(s => storeIds.Contains(s.Id));
        var stores = await storeQ.OrderBy(s => s.Code).Select(s => new LeaveStoreRef(s.Id, s.Code, s.Name)).ToListAsync(ct);

        // lieu_hours matters: liability is annual + lieu, and omitting it here
        // made this table disagree with the store Overview above it.
        var snapQ = db.LeaveLiabilitySnapshots.AsNoTracking().Where(s => s.PeriodEnd == cur);
        if (storeIds is not null) snapQ = snapQ.Where(s => storeIds.Contains(s.StoreId));
        var snaps = await snapQ.OrderBy(s => s.StoreId).ThenBy(s => s.UkgEmployeeId)
            .Select(s => new { s.StoreId, s.RoleClass, s.ContractHours, s.AnnualHours, s.LieuHours }).ToListAsync(ct);

        var rows = LeaveCompute.BuildNetworkRows(config, stores, snaps.Select(s => (s.StoreId, new LiabilityRow
        {
            RoleClass = s.RoleClass,
            ContractHours = s.ContractHours,
            AnnualHours = s.AnnualHours,
            LieuHours = s.LieuHours,
        })));
        return new LeaveNetworkView(cur, rows, config.TargetHoursPerFte, periods);
    }

    /// <summary>Liability per FTE by period, oldest first - the trend chart's series.</summary>
    private async Task<List<TrendPoint>> ComputeLeaveTrendAsync(RosterDbContext db, LeaveConfig config, Guid storeId, CancellationToken ct)
    {
        // Newest periods first with a hard bound (the TypeScript's guard against the
        // 1000-row page cap): ~30 staff x 27 periods.
        var periods = (await functions.LeavePeriodEndsAsync(storeId, ct)).Take(27).ToList();
        // Annual + lieu, to match the KPI card the chart sits under.
        var data = await db.LeaveLiabilitySnapshots.AsNoTracking()
            .Where(s => s.StoreId == storeId && periods.Contains(s.PeriodEnd))
            .OrderBy(s => s.PeriodEnd).ThenBy(s => s.UkgEmployeeId)
            .Select(s => new { s.PeriodEnd, s.RoleClass, s.ContractHours, s.AnnualHours, s.LieuHours }).ToListAsync(ct);
        return LeaveCompute.BuildTrend(config, data.Select(s => (s.PeriodEnd, new LiabilityRow
        {
            RoleClass = s.RoleClass,
            ContractHours = s.ContractHours,
            AnnualHours = s.AnnualHours,
            LieuHours = s.LieuHours,
        })));
    }

    private static async Task<int> MaxStaffOnLeaveAsync(RosterDbContext db, Guid storeId, CancellationToken ct) =>
        await db.StoreAttributes.AsNoTracking().Where(a => a.StoreId == storeId)
            .Select(a => (int?)a.MaxStaffOnLeave).FirstOrDefaultAsync(ct) ?? 3;

    // ─── actions.ts ────────────────────────────────────────────────────────────

    /// <summary>requireTier("super_user"): anyone else is sent to the overview.</summary>
    private static readonly LeaveResult NotSuperUser = new(false, null, "/overview");

    public async Task<LeaveResult> SaveLeaveSettingsAsync(double targetHoursPerFte, double fteDivisor, CancellationToken ct = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return NotSuperUser;
        if (!(targetHoursPerFte > 0) || !(fteDivisor > 0))
            return new LeaveResult(false, "Target and FTE divisor must both be greater than zero.");
        await using var db = contexts.CreateSystem("leave settings, super user");
        var row = await db.LeaveSettings.FirstOrDefaultAsync(s => s.Id, ct);
        // An update of the one row: with no row there is nothing to update (as the TypeScript).
        if (row is null) return LeaveResult.Success;
        row.TargetHoursPerFte = targetHoursPerFte;
        row.FteDivisor = fteDivisor;
        row.UpdatedAt = clock.GetUtcNow();
        return await SaveAsync(db, ct);
    }

    public async Task<LeaveResult> SaveAccrualBandAsync(
        double contractHours, string roleClass, double leaveNotionalHours, double accrualWeeks, double annualAccrualHours,
        CancellationToken ct = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return NotSuperUser;
        if (!new[] { RoleClass.SM, RoleClass.ASM, RoleClass.DSM, RoleClass.SA }.Contains(roleClass))
            return new LeaveResult(false, "Unknown role class");
        if (!(contractHours > 0)) return new LeaveResult(false, "Contract hours must be greater than zero.");
        await using var db = contexts.CreateSystem("accrual band, super user");
        var row = await db.LeaveAccrualBands.FirstOrDefaultAsync(b => b.ContractHours == contractHours && b.RoleClass == roleClass, ct);
        if (row is null)
        {
            row = new LeaveAccrualBand { ContractHours = contractHours, RoleClass = roleClass };
            db.LeaveAccrualBands.Add(row);
        }
        row.LeaveNotionalHours = leaveNotionalHours;
        row.AccrualWeeks = accrualWeeks;
        row.AnnualAccrualHours = annualAccrualHours;
        return await SaveAsync(db, ct);
    }

    public async Task<LeaveResult> DeleteAccrualBandAsync(double contractHours, string roleClass, CancellationToken ct = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return NotSuperUser;
        await using var db = contexts.CreateSystem("accrual band removal, super user");
        var row = await db.LeaveAccrualBands.FirstOrDefaultAsync(b => b.ContractHours == contractHours && b.RoleClass == roleClass, ct);
        if (row is null) return LeaveResult.Success;
        db.LeaveAccrualBands.Remove(row);
        return await SaveAsync(db, ct);
    }

    /// <summary>
    /// How many people this store can have on approved annual leave before a date is
    /// called unavailable. Store-SCOPED rather than super-user gated: cover depends
    /// on the size of the team, so it is the Store Manager's call - a small store
    /// might only carry two at once where a larger one carries four.
    ///
    /// Upsert, not update: a store with no attributes row yet would otherwise report
    /// a successful save that changed nothing.
    /// </summary>
    public async Task<LeaveResult> SaveMaxOnLeaveAsync(Guid storeId, double maxStaffOnLeave, CancellationToken ct = default)
    {
        var denied = await scope.DenyStoreWriteAsync(storeId, ct);
        if (denied is not null) return new LeaveResult(false, denied);
        if (!double.IsFinite(maxStaffOnLeave) || maxStaffOnLeave < 0 || maxStaffOnLeave > 99)
            return new LeaveResult(false, "Maximum on leave must be between 0 and 99.");
        await using var db = contexts.CreateSystem("cover cap, after the store-write check");
        var row = await db.StoreAttributes.FirstOrDefaultAsync(a => a.StoreId == storeId, ct);
        if (row is null)
        {
            row = new StoreAttributes { StoreId = storeId };
            db.StoreAttributes.Add(row);
        }
        row.MaxStaffOnLeave = (int)Js.Round(maxStaffOnLeave);
        return await SaveAsync(db, ct);
    }

    private static async Task<LeaveResult> SaveAsync(RosterDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return LeaveResult.Success;
        }
        catch (DbUpdateException ex)
        {
            return new LeaveResult(false, ex.InnerException?.Message ?? ex.Message);
        }
    }
}
