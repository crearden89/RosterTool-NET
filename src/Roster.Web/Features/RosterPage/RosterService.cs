// Port of the reads of src/app/(app)/plan/roster/page.tsx and the query of
// src/app/api/roster/held/route.ts. What the page decides from them is RosterPageBuilder.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Held;
using Roster.Data.Services.Pay;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Data.Services.Rostering;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.RosterOps;
using Roster.Web.Components.Ui;
using RosterDrift = Roster.Data.Services.Approval.RosterDrift;

namespace Roster.Web.Features.RosterPage;

public sealed class RosterService(
    IStoreScope scope,
    IRememberedStore remembered,
    IRosterDbContextFactory contexts,
    IPlanPeriods periods,
    IHeldRosters held,
    IRosterRows rosterRows,
    IPayCosts payCosts,
    IRosterDrift drift,
    IRefData refData,
    TimeProvider time) : IRosterService
{
    public async Task<RosterPageModel> LoadPageAsync(RosterPageQuery query, CancellationToken ct = default)
    {
        using var memo = RequestMemo.Begin();
        var user = scope.User;
        await using var db = contexts.Create();

        var stores = await db.Stores.OrderBy(s => s.Code).Select(s => new StoreOption(s.Id, s.Code, s.Name)).ToListAsync(ct);
        var selectedStore = user?.StoreId
            ?? StoreScope.ResolveStoreId(Guid.TryParse(query.Store, out var asked) ? asked : null, stores.Select(s => s.Id).ToList(), remembered.StoreId);

        // An explicit ?period= (fortnight nav) or a ?date= pins the fortnight, so approved
        // history stays reachable after time moves on. With neither, open on the earliest
        // un-approved fortnight (§5.2) so a behind store sees the gap, and surface any earlier
        // still-unapproved periods as overdue.
        var date = ParseDate(query.Date);
        var requestedPeriod = ParseDate(query.Period) is { } p ? Period.PeriodStartFor(p) : date is { } d ? Period.PeriodStartFor(d) : (DateOnly?)null;
        var planning = selectedStore is { } st && requestedPeriod is null
            ? await periods.ResolvePlanningPeriodAsync(st, ct: ct)
            : null;
        var planDates = selectedStore is not null ? periods.GetPlanPeriodDates(planning?.PeriodStart ?? requestedPeriod) : [];
        var today = Dates.TodayAu(time.GetUtcNow());
        var periodStart = planDates.Count > 0 ? planDates[0] : today;
        var selectedDate = date ?? periodStart;

        if (selectedStore is not { } store)
            return RosterPageBuilder.Build(new RosterPageInputs
            {
                Stores = stores,
                IsReadOnly = user is null || user.Tier.IsReadOnly(),
                IsStoreTier = user?.Tier == Tier.Store,
                PeriodStart = periodStart,
                SelectedDate = selectedDate,
                CurrentPeriodStart = Period.CurrentPeriodStart(today),
            });

        // NO solving on render: the page reads the HELD snapshots written by the explicit
        // Generate/Regenerate action (one solve produces both the plan and the roster).
        var snapshots = await held.ReadSnapshotsAsync(store, periodStart, ct);
        var statusRows = await db.DayPlans
            .Where(r => r.StoreId == store && planDates.Contains(r.Date))
            .Select(r => new RosterDayStatusRow(r.Date, r.RosterStatus, r.RosterGeneratedAt))
            .ToListAsync(ct);
        var employees = await refData.GetEmployeesAsync(store, ct);
        var roster = snapshots.Roster;

        // The held plan's Stage 1 record per date - what the roster was built from. The cost
        // figures price the ideal floor from it rather than solving Stage 1 again.
        var stage1ByDate = new Dictionary<DateOnly, Stage1Computation>();
        foreach (var day in snapshots.Plan?.Days ?? []) stage1ByDate[day.Date] = day.Result;
        // Overtime and double time are pay-period rules, so the costs need the whole
        // fortnight tiered: the HELD roster's - the shifts shown here, edits and locked
        // shifts included.
        JsMap<string, ShiftTiers>? tiers = roster is null
            ? null
            : await payCosts.TierRosterPeriodAsync(roster.ByDate.Select(kv => (kv.Key, (IEnumerable<Shift>)kv.Value.Shifts)), ct);
        // Row building is display-only work over the HELD day - no snapshot, no solve.
        var heldDay = roster?.ByDate.Get(selectedDate);
        var rows = heldDay is null
            ? null
            : await rosterRows.ComputeRosterRowsAsync(store, selectedDate, heldDay, stage1ByDate.GetValueOrDefault(selectedDate), tiers, ct);
        // Fortnight cost delta (ideal floor vs actual) reuses the solved roster (§6.5).
        var cost = roster is null ? null : await rosterRows.ComputeFortnightCostDeltaAsync(store, roster.ByDate, stage1ByDate, tiers, ct);
        // Has this day been re-solved since it was published to the floor? The kiosk shows
        // the approved roster; this page shows the held plan.
        var driftNow = await drift.DetectRosterDriftAsync(store, selectedDate, ct);
        // Plan -> Review -> Roster: the roster exists only once the fortnight's PLAN is
        // approved on the Review page, and is built from that plan.
        var review = await db.RosterReviews
            .Where(r => r.StoreId == store && r.PeriodStart == periodStart)
            .Select(r => new RosterReviewRow(r.Status, r.SnapshotKey, r.DecidedAt))
            .FirstOrDefaultAsync(ct);

        return RosterPageBuilder.Build(new RosterPageInputs
        {
            Stores = stores,
            SelectedStore = store,
            IsReadOnly = user is null || user.Tier.IsReadOnly(),
            IsStoreTier = user?.Tier == Tier.Store,
            PeriodStart = periodStart,
            PlanDates = planDates,
            SelectedDate = selectedDate,
            OverduePeriods = planning?.OverduePeriods ?? [],
            CurrentPeriodStart = Period.CurrentPeriodStart(today),
            PlanVersion = snapshots.Plan?.Version,
            RosterByDate = roster?.ByDate,
            FortnightFlags = roster?.FortnightFlags ?? [],
            Outcomes = roster?.Outcomes,
            RosterGeneratedAt = roster?.GeneratedAt,
            StatusRows = statusRows,
            Employees = employees.Select(e => new RosterEmployeeRow(e.Id, e.Name, e.Role, e.Active)).ToList(),
            Review = review,
            DayRows = rows,
            FortnightCost = cost,
            Drift = driftNow,
        });
    }

    public async Task<DateTimeOffset?> HeldRosterGeneratedAtAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        return await db.PlanSnapshots
            .Where(s => s.StoreId == storeId && s.PeriodStart == periodStart && s.Kind == SnapshotKinds.Roster)
            .Select(s => (DateTimeOffset?)s.GeneratedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
