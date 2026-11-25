// Port of the loading halves of src/app/(app)/analysis/page.tsx, src/lib/analysis/compute.ts
// and src/app/(app)/analysis/notes/page.tsx. Reads go through the signed-in context: the
// store scope keeps each login to its own stores (Analysis never offers peer stores).

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Held;
using Roster.Data.Services.Pay;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Engine.Analysis;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Walk;
using HeldPlanDay = Roster.Engine.Planning.Plan.HeldPlanDay;

namespace Roster.Web.Features.Analysis;

/// <summary>
/// computeRosterFortnight (lib/stage2/compute) for the Analysis page: the fortnight's roster
/// by date and where each person landed against contract. A seam until the data layer's
/// loader is on main (PROGRESS: P4 NEED D2), then that loader is called directly.
/// </summary>
public interface IAnalysisRosters
{
    Task<FortnightRoster> ComputeRosterFortnightAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);
}

/// <summary>Until the data layer's fortnight loader lands: the Analysis page says it couldn't be read.</summary>
public sealed class PendingAnalysisRosters : IAnalysisRosters
{
    public Task<FortnightRoster> ComputeRosterFortnightAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) =>
        throw new NotImplementedException("computeRosterFortnight needs the data layer's fortnight loader (lib/stage2/compute).");
}

public sealed class AnalysisService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IRememberedStore remembered,
    IRefData refData,
    IPayData payData,
    IPayCosts payCosts,
    IStage1Loader stage1,
    IHeldPlans heldPlans,
    IAnalysisRosters rosters,
    TimeProvider clock) : IAnalysisService
{
    private async Task<(ScopedUser User, List<AnalysisStore> Stores, Guid? StoreId)?> StoreAsync(string? store, CancellationToken ct)
    {
        if (scope.User is not { } user) return null;
        await using var db = contexts.Create();
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new AnalysisStore(s.Id, s.Code, s.Name)).ToListAsync(ct);
        Guid? requested = Guid.TryParse(store, out var r) ? r : null;
        var storeId = user.Tier == Tier.Store
            ? user.StoreId
            : StoreScope.ResolveStoreId(requested, stores.Select(s => s.Id).ToList(), remembered.StoreId);
        return (user, stores, storeId);
    }

    // ─── Analysis ────────────────────────────────────────────────────────────────

    public async Task<AnalysisPage?> LoadAnalysisAsync(string? store, string? start, string? end, bool fullyLoaded, CancellationToken ct = default)
    {
        using var memo = RequestMemo.Begin();
        if (await StoreAsync(store, ct) is not var (user, stores, storeId)) return null;
        var today = Dates.TodayAu(clock.GetUtcNow());

        var startDate = start ?? "";
        if (storeId is { } id && startDate.Length == 0)
        {
            await using var db = contexts.Create();
            var first = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == id)
                .OrderBy(b => b.Date).Select(b => (DateOnly?)b.Date).FirstOrDefaultAsync(ct);
            startDate = (first ?? today).ToIso();
        }
        var endDate = end ?? startDate;
        var result = storeId is { } sid ? await ComputeAnalysisAsync(sid, startDate, endDate, ct) : null;
        return new AnalysisPage(stores, storeId, user.Tier != Tier.Store, startDate, endDate, fullyLoaded, result);
    }

    /// <summary>
    /// computeAnalysis's reads, then <see cref="AnalysisCompute.Compute"/>. A range that
    /// isn't a pair of dates has no days (the TypeScript's loop never ran on one).
    /// </summary>
    public async Task<AnalysisResult> ComputeAnalysisAsync(Guid storeId, string startDate, string endDate, CancellationToken ct = default)
    {
        var s = ParseIso(startDate);
        var e = ParseIso(endDate);
        var days = s is { } from && e is { } to ? AnalysisCompute.RangeDays(from, to) : [];

        // Employees first - the leave table has no store column, so the range query must
        // go through the store's employee ids.
        var emps = await refData.GetEmployeesAsync(storeId, ct);
        var empIds = emps.Select(x => x.Id).ToList();
        var payRates = await payData.GetPayRatesAsync(ct);
        var storeState = await refData.GetStoreStateAsync(storeId, ct);
        var paySettings = await payData.GetPaySettingsAsync(ct);
        var taxRates = await payData.GetPayrollTaxRatesAsync(ct);
        List<AnalysisLeave> leave = [];
        if (days.Count > 0)
        {
            var (first, last) = (days[0], days[^1]);
            await using var db = contexts.Create();
            leave = (await db.EmployeeLeave.AsNoTracking()
                    .Where(l => empIds.Contains(l.EmployeeId) && l.StartDate <= last && l.EndDate >= first)
                    .OrderBy(l => l.EmployeeId).ThenBy(l => l.StartDate).ThenBy(l => l.Id)
                    .Select(l => new { l.EmployeeId, l.StartDate, l.EndDate, l.LeaveType, l.DayHours })
                    .ToListAsync(ct))
                .Select(l => new AnalysisLeave(l.EmployeeId.ToString(), l.StartDate, l.EndDate, l.LeaveType, l.DayHours))
                .ToList();
        }

        // The roster is a fortnight-wide solve: each fortnight is read once.
        var fortnights = new Dictionary<DateOnly, FortnightRoster>();
        async Task<FortnightRoster> FortnightAsync(DateOnly periodStart)
        {
            if (!fortnights.TryGetValue(periodStart, out var f))
                fortnights[periodStart] = f = await rosters.ComputeRosterFortnightAsync(storeId, periodStart, ct);
            return f;
        }

        var perDay = new List<AnalysisDay>();
        foreach (var date in days)
        {
            var period = Period.PeriodStartFor(date);
            var dt = await payData.DayTypeAsync(storeId, date, ct);
            var roster = (await FortnightAsync(period)).ByDate.Get(date) ?? new RosterComputation();
            var s1 = await stage1.ComputeStage1Async(storeId, date, ct);
            // Overtime and double time are pay-PERIOD rules; the containing fortnight is
            // tiered once and read per day.
            var tiers = await payCosts.GetPeriodTiersAsync(storeId, period, ct);
            perDay.Add(new AnalysisDay(date, dt, roster, s1, tiers));
        }

        var periods = days.Select(Period.PeriodStartFor).Distinct().ToList();
        var outcomes = new List<IReadOnlyList<ContractOutcome>>();
        foreach (var p in periods) outcomes.Add((await FortnightAsync(p)).Outcomes);

        List<AnalysisActuals> actuals;
        List<AnalysisBench> bench;
        await using (var db = contexts.Create())
        {
            actuals = await db.ActualsDays.AsNoTracking()
                .Where(a => a.StoreId == storeId && days.Contains(a.Date))
                .Select(a => new AnalysisActuals(a.Date, a.ActualHours100, a.ActualItems, a.ActualSales))
                .ToListAsync(ct);
            bench = await db.BenchmarkDays.AsNoTracking()
                .Where(b => b.StoreId == storeId && days.Contains(b.Date))
                .Select(b => new AnalysisBench(b.Date, b.SalesForecast, b.Items))
                .ToListAsync(ct);
        }

        // The plan the Plan page shows and the Review approved, read the way the Plan page
        // reads it, so its planned hours and benchmark are the Plan page's. Never solved here.
        var held = new Dictionary<DateOnly, HeldPlanDay>();
        foreach (var p in periods)
        {
            var plan = await heldPlans.ReadPlanAsync(storeId, p, ct);
            foreach (var d in plan?.Days ?? []) held[d.Date] = new HeldPlanDay(BenchmarkParts.Of(d.Result), d.Plan);
        }

        return AnalysisCompute.Compute(new AnalysisInputs
        {
            Days = days,
            PerDay = perDay,
            Employees = emps.Select(x => new AnalysisEmployee(x.Id.ToString(), x.Role, x.PositionCode, x.SmYearBand, x.NotionalHours, x.ContractHours)).ToList(),
            PayRates = payRates,
            PaySettings = paySettings,
            StoreState = storeState,
            PayrollTaxRates = taxRates,
            Leave = leave,
            PeriodOutcomes = outcomes,
            Actuals = actuals,
            Bench = bench,
            Held = held,
        });
    }

    private static DateOnly? ParseIso(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // ─── Notes History ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads through the signed-in context: walk notes are scoped to the stores the login
    /// may see, so the hierarchy is enforced by the data layer, not re-derived here.
    /// </summary>
    public async Task<NotesPage?> LoadNotesAsync(string? store, string? start, string? end, string? status, CancellationToken ct = default)
    {
        if (await StoreAsync(store, ct) is not var (user, stores, storeId)) return null;
        var today = Dates.TodayAu(clock.GetUtcNow());
        var filter = status == "open" ? NoteStatus.Open : status == "all" ? NoteStatus.All : NoteStatus.Completed;

        await using var db = contexts.Create();
        // Default the range to this store's whole history, so the first load always has
        // something in it - notes are sparse, and a fixed "last 30 days" window would open
        // empty for most stores.
        var startDate = start ?? "";
        if (storeId is { } id && startDate.Length == 0)
        {
            var first = await db.StoreWalkNotes.AsNoTracking().Where(n => n.StoreId == id)
                .OrderBy(n => n.RaisedDate).Select(n => (DateOnly?)n.RaisedDate).FirstOrDefaultAsync(ct);
            startDate = (first ?? today).ToIso();
        }
        var endDate = end ?? today.ToIso();

        // A range the database can't read (no store, or not a date) finds nothing, as the
        // TypeScript's failed query did.
        var notes = new List<WalkNote>();
        if (storeId is { } sid && ParseIso(startDate) is { } from && ParseIso(endDate) is { } to)
        {
            var q = db.StoreWalkNotes.AsNoTracking().Where(n => n.StoreId == sid && n.RaisedDate >= from && n.RaisedDate <= to);
            if (filter == NoteStatus.Completed) q = q.Where(n => n.CompletedAt != null);
            if (filter == NoteStatus.Open) q = q.Where(n => n.CompletedAt == null);
            notes = (await q.OrderByDescending(n => n.RaisedDate).ThenByDescending(n => n.CreatedAt)
                    .Select(n => new { n.Id, n.Note, n.AssignedEmployeeId, n.AssignedName, n.RaisedDate, n.CompletedAt })
                    .ToListAsync(ct))
                .Select(n => new WalkNote(n.Id.ToString(), n.Note, n.AssignedEmployeeId?.ToString(), n.AssignedName, n.RaisedDate, n.CompletedAt))
                .ToList();
        }

        return new NotesPage(
            user.Tier == Tier.Store ? [] : stores,
            storeId,
            user.Tier != Tier.Store,
            startDate,
            endDate,
            filter,
            today,
            notes,
            Notes.SummariseNotes(notes, today));
    }
}
