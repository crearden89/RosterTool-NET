// Port of app/(app)/setup/weekends/page.tsx (LoadAsync) and of the server actions in
// setup/weekends/actions.ts, with the same store-scope checks in the same places.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Planning;
using Roster.Engine.Core;
using Roster.Web.Components.Ui;
using Roster.Web.Features.Team;

namespace Roster.Web.Features.Weekends;

public sealed class WeekendsService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IRememberedStore remembered,
    IPlanPeriods planPeriods) : IWeekendsService
{
    public async Task<WeekendsPageData> LoadAsync(Guid? requestedStore, DateOnly? start, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var user = scope.User;
        await using var db = contexts.Create();
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new StoreOption(s.Id, s.Code, s.Name)).ToListAsync(ct);
        var storeId = user is { Tier: Tier.Store }
            ? user.StoreId
            : StoreScope.ResolveStoreId(requestedStore, stores.Select(s => s.Id).ToList(), remembered.StoreId);

        // Default to the same fortnight the Plan screen opens on, so the two pages always
        // agree about which period is being worked.
        var periodStart = start is { } s && Period.IsPeriodStart(s)
            ? s
            : storeId is { } id
                ? (await planPeriods.ResolvePlanningPeriodAsync(id, ct: ct)).PeriodStart
                : Period.PlanningPeriodStart();

        var sid = storeId ?? Guid.Empty;
        var employees = WeekendsRules.SortTeam(await db.Employees.AsNoTracking()
            .Where(e => e.StoreId == sid && e.Active)
            .Select(e => new WeekendsMember(e.Id, e.Name, e.Role, e.ContractHours))
            .ToListAsync(ct));

        var weekends = WeekendWindow.WindowSaturdays(periodStart);
        // Scoped to THIS store's team, on the window's four weekends.
        var markers = employees.Count == 0
            ? []
            : await db.WeekendMarkers.AsNoTracking()
                .Where(m => weekends.Contains(m.WeekendStart) && m.Employee!.StoreId == sid && m.Employee.Active)
                .Select(m => new WeekendMarkerRow(m.EmployeeId, m.WeekendStart, m.SatOff, m.SunOff))
                .ToListAsync(ct);

        return new WeekendsPageData
        {
            StoreId = storeId,
            Stores = user is { Tier: Tier.Store } ? [] : stores,
            Employees = employees,
            Weekends = weekends,
            Markers = markers,
            PeriodStart = periodStart,
            ReadOnly = user is null || AccessMatrix.IsReadOnlyTier(user.Tier),
        };
    }

    public async Task<SaveResult> SetWeekendMarkerAsync(Guid employeeId, DateOnly weekendStart, bool satOff, bool sunOff, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var denied = await scope.DenyEmployeeWriteAsync(employeeId, ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        await UpsertAsync(db, employeeId, weekendStart, satOff, sunOff, ct);
        return await TeamService.SaveAsync(db, ct);
    }

    // Batch upsert of weekend markers - the page collects all edits across staff and
    // persists them in one Save rather than a round-trip per toggle.
    public async Task<SaveResult> SaveWeekendMarkersAsync(IReadOnlyList<WeekendMarkerInput> markers, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        if (scope.User is null) return SaveResult.Fail(ScopeMessages.DeniedAuth);
        if (markers.Count == 0) return SaveResult.Success;
        var denied = await scope.DenyEmployeesWriteAsync(markers.Select(m => m.EmployeeId), ct);
        if (denied is not null) return SaveResult.Fail(denied);
        await using var db = contexts.Create();
        foreach (var m in markers) await UpsertAsync(db, m.EmployeeId, m.WeekendStart, m.SatOff, m.SunOff, ct);
        return await TeamService.SaveAsync(db, ct);
    }

    private static async Task UpsertAsync(RosterDbContext db, Guid employeeId, DateOnly weekendStart, bool satOff, bool sunOff, CancellationToken ct)
    {
        var marker = db.WeekendMarkers.Local.FirstOrDefault(m => m.EmployeeId == employeeId && m.WeekendStart == weekendStart)
            ?? await db.WeekendMarkers.FirstOrDefaultAsync(m => m.EmployeeId == employeeId && m.WeekendStart == weekendStart, ct);
        if (marker is null)
        {
            marker = new WeekendMarker { EmployeeId = employeeId, WeekendStart = weekendStart };
            db.WeekendMarkers.Add(marker);
        }
        marker.SatOff = satOff;
        marker.SunOff = sunOff;
    }
}
