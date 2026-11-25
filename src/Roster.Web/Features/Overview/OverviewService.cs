// Port of the loading halves of src/app/(app)/overview/page.tsx, src/lib/overview/compute.ts,
// src/lib/overview/dashboard.ts and src/app/(app)/overview/YourStores.tsx. Statuses and held
// snapshots only: nothing here solves or generates a plan or a roster. Reads go through the
// signed-in context, so the store scope keeps each login to the stores it may see, except
// where the TypeScript deliberately read as the service role (the Dashboard is peer-visible).

using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Configurations;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Planning;
using Roster.Data.Services.Review;
using Roster.Engine.Core;
using Roster.Engine.Overview;
using Roster.Engine.Planning;
using OverviewReview = Roster.Engine.Overview.ReviewStatuses;

namespace Roster.Web.Features.Overview;

public sealed class OverviewService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IStoreDirectory directory,
    IRememberedStore remembered,
    IDatabaseFunctions functions,
    IPlanPeriods planPeriods,
    IReviewReader reviews,
    TimeProvider clock) : IOverviewService
{
    private const string PeerReason = "Dashboard: every login may see every store's month (peer-visible by design)";

    public async Task<DashboardPage?> LoadDashboardAsync(string? store, string? month, CancellationToken ct = default)
    {
        if (scope.User is not { } user) return null;
        var today = Dates.TodayAu(clock.GetUtcNow());

        // The Dashboard is PEER-VISIBLE by design: every login can pick any region, then
        // any store in it, and see the same forecast/actual/hours/status view that store's
        // own manager sees. The full network list therefore comes from the system read
        // (the store scope rightly hides other stores from store logins).
        var stores = (await directory.ListAsync(ct))
            .Select(s => new DashboardStore(s.Id, s.Code, s.Name, s.RegionId, s.AreaId))
            .ToList();
        List<DashboardRegion> regions;
        await using (var sys = contexts.CreateSystem(PeerReason))
        {
            regions = await sys.Regions.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(r => r.Name)
                .Select(r => new DashboardRegion(r.Id, r.Name))
                .ToListAsync(ct);
        }

        // Roll-up tiers land on an aggregate by default: PA/DSO across the network, EMSO
        // across their area. Store logins land on their own store.
        var isRollup = TierChecks.IsAreaLogin(user.Tier);
        Guid? requested = Guid.TryParse(store, out var r) ? r : null;
        Guid? selected = requested;
        List<Guid> storeIds;
        if (user.Tier == Tier.Store)
        {
            selected = requested is { } q && stores.Any(s => s.Id == q) ? q : user.StoreId;
            storeIds = selected is { } s1 ? [s1] : [];
        }
        else if (isRollup && requested is null)
        {
            storeIds = (user.Tier == Tier.Emso ? stores.Where(s => s.AreaId == user.AreaId) : stores).Select(s => s.Id).ToList();
        }
        else
        {
            selected = requested ?? StoreScope.ResolveStoreId(null, stores.Select(s => s.Id).ToList(), remembered.StoreId);
            storeIds = selected is { } s2 ? [s2] : [];
        }

        // A single selected store outside the viewer's own scope reads as a peer; everything
        // in scope stays on the signed-in context.
        var selectedRow = stores.FirstOrDefault(s => s.Id == selected);
        var asPeer = storeIds.Count == 1 && selectedRow is not null
            && AccessMatrix.StoreAccessFor(user, selectedRow.Id, selectedRow.AreaId) == StoreAccess.Peer;

        var oeTarget = 90.0;
        if (storeIds.Count == 1)
        {
            await using var db = asPeer ? contexts.CreateSystem(PeerReason) : contexts.Create();
            var target = await db.StoreAttributes.AsNoTracking()
                .Where(a => a.StoreId == storeIds[0])
                .Select(a => (double?)a.OeTargetPct)
                .FirstOrDefaultAsync(ct);
            if (target is { } t) oeTarget = t;
        }

        // ?month= as given (the TypeScript failed on a date it could not read; this opens
        // the current month instead).
        var monthStart = DashboardText.ParseIsoDate(month) ?? new DateOnly(today.Year, today.Month, 1);
        var data = storeIds.Count > 0 ? await ComputeOverviewAsync(storeIds, monthStart, oeTarget, today, asPeer, ct) : null;

        return new DashboardPage(
            user,
            data,
            today,
            selected,
            isRollup,
            stores,
            regions,
            monthStart,
            DashboardText.MonthLabel(monthStart),
            DashboardText.ShiftMonth(monthStart, -1),
            DashboardText.ShiftMonth(monthStart, 1));
    }

    /// <summary>
    /// computeOverview's reads: the data-light threshold, the overview_rollup rows and the
    /// paid-break allowance; the rest is <see cref="OverviewCompute.BuildOverview"/>.
    /// </summary>
    /// <param name="asPeer">Compute for stores OUTSIDE the caller's scope: the Dashboard is
    /// peer-visible by design (every login sees every store's numbers).</param>
    public async Task<OverviewData> ComputeOverviewAsync(
        IReadOnlyList<Guid> storeIds, DateOnly monthStart, double oeTarget, DateOnly today, bool asPeer, CancellationToken ct = default)
    {
        // Query spans Jan-1-of-view-year → max(today, month end) so KPI windows
        // (WTD/MTD/YTD, always relative to today) come from the same fetch.
        var (from, to) = OverviewCompute.RollupRange(monthStart, today);
        double dataLightPct;
        double breakFactorPct;
        await using (var db = asPeer ? contexts.CreateSystem(PeerReason) : contexts.Create())
        {
            dataLightPct = await db.VarianceSettings.AsNoTracking().Where(v => v.Id).Select(v => (double?)v.DataLightPct).FirstOrDefaultAsync(ct) ?? 15;
            // Paid-break allowance, set under Rules → Labour Group Rules → Breaks. Applied at
            // READ time rather than baked into the ingested rows, so changing it re-states
            // history instead of only affecting future uploads.
            breakFactorPct = await BreakFactorAsync(db, ct);
        }
        // Aggregated in the database (overview_rollup): row-level data for every store in
        // scope from 1 January would be hundreds of thousands of rows for a national roll-up.
        var rollup = await functions.OverviewRollupAsync(storeIds, from, to, asPeer, ct);
        return OverviewCompute.BuildOverview(
            rollup.Select(x => new RollupRow(
                x.Date, x.ForecastSales, x.CustomerCount, x.BenchItems, x.Hours90, x.Hours100, x.ActualSales, x.ActualItems,
                x.ActualCustomers, x.ActualHours100, x.KronosHours, x.NonopMinutes, x.AnyApproved, x.AnyGenerated)),
            monthStart,
            oeTarget,
            today,
            breakFactorPct,
            dataLightPct);
    }

    private static async Task<double> BreakFactorAsync(RosterDbContext db, CancellationToken ct)
    {
        var value = await db.RosteringRules.AsNoTracking()
            .Where(x => x.Key == Metrics.PaidBreakFactorKey)
            .Select(x => (double?)x.Value)
            .FirstOrDefaultAsync(ct);
        return value ?? Metrics.DefaultPaidBreakFactorPct;
    }

    // ─── loadStoreFortnight ──────────────────────────────────────────────────────

    public async Task<StoreFortnight?> LoadStoreFortnightAsync(Guid storeId, DateOnly today, CancellationToken ct = default)
    {
        var planning = await planPeriods.ResolvePlanningPeriodAsync(storeId, false, ct);
        var periodStart = planning.PeriodStart;
        var dates = Period.PeriodDatesFrom(periodStart);
        var review = await reviews.LoadReviewAsync(storeId, periodStart, ct);
        if (review is null) return null;

        await using var db = contexts.Create();
        var dayRows = await db.DayPlans.AsNoTracking()
            .Where(d => d.StoreId == storeId && dates.Contains(d.Date))
            .Select(d => new { d.Date, d.RosterStatus })
            .ToListAsync(ct);
        // The held roster's build time only - never its payload.
        var rosterBuiltAt = await db.PlanSnapshots.AsNoTracking()
            .Where(p => p.StoreId == storeId && p.PeriodStart == periodStart && p.Kind == SnapshotKinds.Roster)
            .Select(p => (DateTimeOffset?)p.GeneratedAt)
            .FirstOrDefaultAsync(ct);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.StoreId == storeId && e.Active)
            .Select(e => new { e.Id, e.Name, e.Role, e.ContractHours })
            .ToListAsync(ct);

        // The team's waiting requests (the Requests page's rows) and fixed rosters (the
        // Team Members page's), read for the store's active team.
        var ids = employees.Select(e => e.Id).ToList();
        var requests = await db.DayOffRequests.AsNoTracking()
            .Where(q => ids.Contains(q.EmployeeId) && q.Status == DayOffStatuses.Pending)
            .OrderBy(q => q.SubmittedAt)
            .Select(q => q.SubmittedAt)
            .ToListAsync(ct);
        var fixedRows = await db.FixedRosters.AsNoTracking()
            .Where(f => ids.Contains(f.EmployeeId))
            .Select(f => new { f.EmployeeId, f.StartTime, f.EndTime })
            .ToListAsync(ct);
        var cycles = await db.FixedRosterCycles.AsNoTracking()
            .Where(c => ids.Contains(c.EmployeeId))
            .Select(c => new { c.EmployeeId, c.CycleWeeks })
            .ToListAsync(ct);

        var approvedDates = dayRows.Where(d => d.RosterStatus == DayStatuses.Approved).Select(d => d.Date).ToHashSet();
        var reviewStatus = OverviewReview.Parse(review.Status);
        var state = new FortnightState(
            storeId.ToString(),
            periodStart,
            today,
            review.Days.Any(d => d.Forecast is not null),
            review.HasSnapshot ? new FortnightPlan(review.GeneratedAt) : null,
            new FortnightReview(reviewStatus, review.Stale, review.SubmittedAt, review.DecidedAt, review.ReturnMessage),
            new FortnightRoster(dates.All(approvedDates.Contains), rosterBuiltAt is { } built ? ReviewFormat.Stamp(built) : null));

        var overContract = NeedsDoing.FixedRostersAboveContract(
            employees.Select(e => new ContractMember(e.Id.ToString(), e.Name, e.Role, e.ContractHours)).ToList(),
            fixedRows.Select(f => new FixedShiftRow(f.EmployeeId.ToString(), SqlTime(f.StartTime), SqlTime(f.EndTime))),
            cycles.Select(c => new FixedCycleRow(c.EmployeeId.ToString(), c.CycleWeeks)));
        var needs = NeedsDoing.Items(new NeedsDoingInput(
            review.HasSnapshot,
            reviewStatus,
            review.Days
                .Where(d => d.HasData && !d.Closed)
                .Select(d => new ReviewDayInput(
                    d.Date,
                    $"{d.Dow} {d.DateLabel}",
                    d.Checks.Select(c => new NeedsCheck(c.Severity == Severity.Error ? "error" : "warning", c.Done, c.HardStop)).ToList()))
                .ToList(),
            requests.Select(at => new PendingRequest(at.ToString("O", CultureInfo.InvariantCulture))).ToList(),
            Js.Sorted(overContract, (a, b) => Js.LocaleCompare(a.Name, b.Name)),
            new NeedsLinks($"/setup/requests?store={storeId}", FortnightCard.FortnightLinksFor(storeId.ToString(), periodStart).Review, $"/setup/staff?store={storeId}")));

        return new StoreFortnight(
            state,
            planning.OverduePeriods,
            FortnightCard.FortnightFiguresFor(
                review.Days.Select(d => new FortnightFigureDay(d.Forecast, d.Benchmark, d.Planned, d.PlannedShifts)).ToList(),
                review.HasSnapshot),
            needs);
    }

    private static string SqlTime(TimeOnly t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ─── Your stores ─────────────────────────────────────────────────────────────

    public async Task<YourStoresView> LoadYourStoresAsync(DateOnly today, CancellationToken ct = default)
    {
        var user = scope.User ?? throw new InvalidOperationException("Your stores needs a signed-in user.");
        List<(Guid Id, string Code, string Name, Guid? RegionId, Guid? AreaId)> visible;
        List<ScopeRegion> regionRows;
        await using (var db = contexts.Create())
        {
            visible = (await db.Stores.AsNoTracking().OrderBy(s => s.Code)
                    .Select(s => new { s.Id, s.Code, s.Name, s.RegionId, s.AreaId }).ToListAsync(ct))
                .Select(s => (s.Id, s.Code, s.Name, (Guid?)s.RegionId, s.AreaId))
                .ToList();
            // Which regions belong to a DSO's group (any signed-in user may read them).
            regionRows = await db.Regions.AsNoTracking().Select(x => new ScopeRegion(x.Id, x.DirectorGroupId)).ToListAsync(ct);
        }
        var remit = ReportingScope.RemitStoreIds(user, visible.Select(s => new ScopeStore(s.Id, s.RegionId, s.AreaId)), regionRows);
        var mine = remit is null ? visible : visible.Where(s => remit.Contains(s.Id)).ToList();
        var period = Period.PlanningPeriodStart(today);
        var rows = await LoadAreaStoresAsync(mine.Select(s => (s.Id, s.Code, s.Name)).ToList(), period, today, ct);
        return new YourStoresView(period, rows, remit is { Count: 0 });
    }

    /// <summary>
    /// Each store's plan, review and roster status for the fortnight, its plan days with an
    /// error, its waiting day-off requests and its efficiency month to date. The held plans'
    /// day results are picked out of the snapshots by the database where it can
    /// (payload → days → i → ok), so only fourteen true/false values come back per store.
    /// </summary>
    public async Task<IReadOnlyList<AreaStoreRow>> LoadAreaStoresAsync(
        IReadOnlyList<(Guid Id, string Code, string Name)> stores, DateOnly periodStart, DateOnly today, CancellationToken ct = default)
    {
        if (stores.Count == 0) return [];
        var ids = stores.Select(s => s.Id).ToList();
        var dates = Period.PeriodDatesFrom(periodStart);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        await using var db = contexts.Create();
        var plans = await PlanDayOksAsync(db, ids, periodStart, ct);
        var built = (await db.PlanSnapshots.AsNoTracking()
                .Where(p => ids.Contains(p.StoreId) && p.PeriodStart == periodStart && p.Kind == SnapshotKinds.Roster)
                .Select(p => p.StoreId).ToListAsync(ct))
            .ToHashSet();
        var reviewRows = await db.RosterReviews.AsNoTracking()
            .Where(x => ids.Contains(x.StoreId) && x.PeriodStart == periodStart)
            .Select(x => new { x.StoreId, x.Status, x.SnapshotKey })
            .ToListAsync(ct);
        var published = await db.DayPlans.AsNoTracking()
            .Where(d => ids.Contains(d.StoreId) && dates.Contains(d.Date) && d.RosterStatus == DayStatuses.Approved)
            .Select(d => d.StoreId)
            .ToListAsync(ct);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => ids.Contains(e.StoreId) && e.Active)
            .Select(e => new { e.Id, e.StoreId })
            .ToListAsync(ct);
        var actuals = await db.ActualsDays.AsNoTracking()
            .Where(a => ids.Contains(a.StoreId) && a.Date >= monthStart && a.Date <= today)
            .OrderBy(a => a.StoreId).ThenBy(a => a.Date)
            .Select(a => new { a.StoreId, a.ActualHours100, a.UkgActualHours })
            .ToListAsync(ct);
        var nonOp = await db.NonOpEntries.AsNoTracking()
            .Where(n => ids.Contains(n.StoreId) && n.Date >= monthStart && n.Date <= today)
            .OrderBy(n => n.StoreId).ThenBy(n => n.Date).ThenBy(n => n.Id)
            .Select(n => new { n.StoreId, n.Minutes })
            .ToListAsync(ct);
        var breakFactorPct = await BreakFactorAsync(db, ct);

        // Waiting requests, counted per store through the requester: the requests table is
        // read by employee, as the Requests page reads it, in short slices.
        var storeOfEmployee = employees.ToDictionary(e => e.Id, e => e.StoreId);
        var requestsByStore = new Dictionary<Guid, int>();
        foreach (var slice in employees.Select(e => e.Id).Chunk(150))
        {
            var empIds = slice.ToList();
            var requesters = await db.DayOffRequests.AsNoTracking()
                .Where(q => empIds.Contains(q.EmployeeId) && q.Status == DayOffStatuses.Pending)
                .Select(q => q.EmployeeId)
                .ToListAsync(ct);
            foreach (var e in requesters)
                if (storeOfEmployee.TryGetValue(e, out var s)) requestsByStore[s] = requestsByStore.GetValueOrDefault(s) + 1;
        }

        // Ids as text on this side: the database's own text form of a uniqueidentifier is upper case.
        var oe = StoreRows.OeByStore(
            actuals.Select(a => new StoreActualsRow(a.StoreId.ToString(), a.ActualHours100, a.UkgActualHours)),
            nonOp.Select(n => new StoreNonOpRow(n.StoreId.ToString(), n.Minutes)),
            breakFactorPct);
        var reviewByStore = new Dictionary<Guid, StoreReviewRow>();
        foreach (var x in reviewRows) reviewByStore[x.StoreId] = new StoreReviewRow(x.Status, x.SnapshotKey);
        var publishedByStore = published.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());

        return stores
            .Select(s => new AreaStoreRow(
                s.Id,
                s.Code,
                s.Name,
                StoreRows.StoreStatusOf(new StoreStatusInput(
                    plans.TryGetValue(s.Id, out var p) ? p : null,
                    reviewByStore.GetValueOrDefault(s.Id),
                    publishedByStore.GetValueOrDefault(s.Id),
                    dates.Count,
                    built.Contains(s.Id))),
                requestsByStore.GetValueOrDefault(s.Id),
                oe.Get(s.Id.ToString())))
            .ToList();
    }

    /// <summary>
    /// Each held plan's version and day OKs (true/false, or null where the plan holds no
    /// such day). A fortnight is always fourteen days.
    /// </summary>
    private static async Task<Dictionary<Guid, HeldPlanStatus>> PlanDayOksAsync(
        RosterDbContext db, List<Guid> ids, DateOnly periodStart, CancellationToken ct)
    {
        var result = new Dictionary<Guid, HeldPlanStatus>();
        if (db.Provider == DatabaseProvider.SqlServer)
        {
            var oks = string.Join(", ", Enumerable.Range(0, 14).Select(i => $"json_value(payload, '$.days[{i}].ok') as d{i}"));
            var sql = $"""
                select store_id, version, {oks}
                  from plan_snapshots
                 where store_id in (select cast([value] as uniqueidentifier) from openjson(@ids))
                   and period_start = @period and kind = 'plan'
                """;
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids) });
                cmd.Parameters.Add(new SqlParameter("@period", SqlDbType.Date) { Value = periodStart.ToDateTime(TimeOnly.MinValue) });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var ok = new bool?[14];
                    for (var i = 0; i < 14; i++)
                        ok[i] = r.IsDBNull(2 + i) ? null : r.GetString(2 + i) switch { "true" => true, "false" => false, _ => null };
                    result[r.GetGuid(0)] = new HeldPlanStatus(r.GetInt32(1), ok);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
            return result;
        }

        // Other databases (the tests' SQLite): read the payloads and pick the days out here.
        var rows = await db.PlanSnapshots.AsNoTracking()
            .Where(p => ids.Contains(p.StoreId) && p.PeriodStart == periodStart && p.Kind == SnapshotKinds.Plan)
            .Select(p => new { p.StoreId, p.Version, p.Payload })
            .ToListAsync(ct);
        foreach (var p in rows) result[p.StoreId] = new HeldPlanStatus(p.Version, DayOks(p.Payload));
        return result;
    }

    /// <summary>payload->days->i->ok for i = 0..13: a boolean, or null.</summary>
    internal static bool?[] DayOks(string payload)
    {
        var ok = new bool?[14];
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var day in days.EnumerateArray())
            {
                if (i >= 14) break;
                if (day.ValueKind == JsonValueKind.Object && day.TryGetProperty("ok", out var v)
                    && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ok[i] = v.GetBoolean();
                i++;
            }
        }
        return ok;
    }
}
