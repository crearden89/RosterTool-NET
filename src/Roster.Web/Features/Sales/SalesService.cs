// Port of src/app/(app)/plan/sales/page.tsx (the reads, fortnightSummary) and
// actions.ts (saveOverride, resetOverride, clearAdjustment).

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Snapshots;
using Roster.Engine.Core;

namespace Roster.Web.Features.Sales;

public sealed class SalesService(
    IRosterDbContextFactory contexts,
    IStoreScope scope,
    IRememberedStore remembered,
    IPlanSnapshotStore snapshots,
    TimeProvider clock) : ISalesService
{
    private const int LookbackWeeks = 12;

    public async Task<SalesPageData> LoadAsync(Guid? store, DateOnly? start, CancellationToken ct = default)
    {
        var user = scope.User;
        await using var db = contexts.Create();
        var today = Dates.TodayAu(clock.GetUtcNow());

        // The stores this login can see (the private pages never offer peer stores).
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new SalesStore(s.Id, s.Code, s.Name)).ToListAsync(ct);
        var storeId = user?.Tier == Tier.Store
            ? user.StoreId
            : StoreScope.ResolveStoreId(store, stores.Select(s => s.Id).ToList(), remembered.StoreId);

        // Default fortnight start: earliest benchmark date for the store. (No store at
        // all: today - the TypeScript page cannot render that case.)
        var startDate = start ?? today;
        if (storeId is { } sid && start is null)
        {
            startDate = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == sid)
                .OrderBy(b => b.Date).Select(b => (DateOnly?)b.Date).FirstOrDefaultAsync(ct) ?? today;
        }

        var dates = Enumerable.Range(0, 14).Select(startDate.AddDays).ToList();

        // For each target date, the same-weekday date in each of the preceding
        // LOOKBACK_WEEKS weeks (used for the grey "avg" figure + growth %).
        var lookbackByDate = dates.ToDictionary(d => d, d => Enumerable.Range(1, LookbackWeeks).Select(w => d.AddDays(-w * 7)).ToList());
        var allLookbackDates = lookbackByDate.Values.SelectMany(x => x).Distinct().ToList();

        List<SalesForecastDay> bench = [];
        List<SalesOverrideRow> overrides = [];
        Dictionary<DateOnly, double> actualByDate = [];
        if (storeId is { } id)
        {
            bench = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == id && dates.Contains(b.Date))
                .Select(b => new SalesForecastDay(b.Date, b.SalesForecast)).ToListAsync(ct);
            overrides = await db.SalesOverrides.AsNoTracking().Where(o => o.StoreId == id && dates.Contains(o.Date))
                .Select(o => new SalesOverrideRow(o.Date, o.Closed, o.Method, o.Pct, o.Value, o.Reason, o.Notes)).ToListAsync(ct);
            actualByDate = await db.ActualsDays.AsNoTracking().Where(a => a.StoreId == id && allLookbackDates.Contains(a.Date))
                .ToDictionaryAsync(a => a.Date, a => a.ActualSales, ct);
        }

        var avgSales = dates.Select(d =>
        {
            var known = lookbackByDate[d].Where(actualByDate.ContainsKey).Select(ld => actualByDate[ld]).ToList();
            double sum = 0;
            foreach (var k in known) sum += k;
            return new AvgSalesDay(d, known.Count > 0 ? sum / known.Count : null);
        }).ToList();

        var summary = storeId is { } s2 ? await FortnightSummaryAsync(db, s2, dates, ct) : null;

        return new SalesPageData(
            StoreId: storeId,
            Stores: user?.Tier == Tier.Store ? [] : stores,
            StartDate: startDate,
            Dates: dates,
            Benchmark: bench,
            Overrides: overrides,
            AvgSales: avgSales,
            Summary: summary,
            ReadOnly: user is null || AccessMatrix.IsReadOnlyTier(user.Tier));
    }

    // ─── The fortnight's other planning inputs, in brief ─────────────────────────
    // Read the way their own pages read them, so the cards and those pages agree:
    //   - Non-Op: the store's entries on these dates, as the Non-Op page lists them.
    //   - Leave: the team's leave overlapping the fortnight - the bookings made on
    //     Team Members, which are the ones the roster reads (lib/stage2/compute).
    //     (The Leave Liability page's bookings come from the UKG extract and are
    //     estimates for the balances; they don't feed the plan.)
    //   - Weekends: the weekend-off switches for the Saturdays in these dates, as
    //     the Weekends page reads them.
    //   - Benchmark: the held plan's hours against the benchmark, summed over the
    //     fortnight - the Plan page's and Review's per-day figure (owner decision
    //     R2). Read from the held snapshot, never solved here; none until the plan
    //     has been generated.
    private async Task<SalesSummary> FortnightSummaryAsync(RosterDbContext db, Guid storeId, List<DateOnly> dates, CancellationToken ct)
    {
        var saturdays = dates.Where(d => Period.DowMon0(d) == 5).ToList();
        var team = await db.Employees.AsNoTracking().Where(e => e.StoreId == storeId && e.Active)
            .Select(e => new { e.Id, e.Name }).ToListAsync(ct);
        var ids = team.Select(e => e.Id).ToList();
        var nameOf = team.ToDictionary(e => e.Id, e => e.Name);

        // No ORDER BY in the TypeScript (the database's own order, which is the order
        // the entries were made); here that order is asked for.
        var entries = await db.NonOpEntries.AsNoTracking().Where(e => e.StoreId == storeId && dates.Contains(e.Date))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new { e.SubcategoryId, e.Minutes }).ToListAsync(ct);
        var catOfSub = await db.NonOpSubcategories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CategoryId, ct);
        var catName = (await db.NonOpCategories.AsNoTracking().OrderBy(c => c.Sort).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Name);
        var first = dates[0];
        var last = dates[13];
        var leave = await db.EmployeeLeave.AsNoTracking()
            .Where(l => ids.Contains(l.EmployeeId) && l.StartDate <= last && l.EndDate >= first)
            .OrderBy(l => l.StartDate).ThenBy(l => l.CreatedAt)
            .Select(l => new { l.EmployeeId, l.StartDate, l.EndDate }).ToListAsync(ct);
        var markers = saturdays.Count > 0
            ? await db.WeekendMarkers.AsNoTracking().Where(m => ids.Contains(m.EmployeeId) && saturdays.Contains(m.WeekendStart))
                .Select(m => new { m.EmployeeId, m.SatOff, m.SunOff }).ToListAsync(ct)
            : [];
        // The plan is held per grid fortnight; dates that aren't one have none.
        // Only the store's own scope reaches here (the switcher lists the
        // viewer's stores), and only the summed hours leave the server.
        var plan = Period.IsPeriodStart(dates[0]) ? await snapshots.ReadPeerPlanAsync(storeId, dates[0], ct) : null;

        // Non-Op hours by category, largest first.
        var minutesByCat = new JsMap<string, double>();
        double nonOpMinutes = 0;
        foreach (var e in entries)
        {
            nonOpMinutes += e.Minutes;
            var name = catOfSub.TryGetValue(e.SubcategoryId, out var cat) && catName.TryGetValue(cat, out var n) ? n : "Other";
            minutesByCat.Set(name, minutesByCat.GetOr(name, 0) + e.Minutes);
        }

        double? benchmarkHours = plan is null ? null : BenchmarkHours(plan.Payload, dates);

        return new SalesSummary(
            NonOp: new NonOpBrief(
                nonOpMinutes / 60,
                Js.Sorted(minutesByCat.Entries(), (a, b) => b.Value - a.Value).Select(kv => kv.Key).ToList()),
            Leave: leave.Select(l => new LeaveBrief(nameOf.TryGetValue(l.EmployeeId, out var nm) ? nm : "-", l.StartDate, l.EndDate)).ToList(),
            Weekends: new WeekendsBrief(
                saturdays,
                markers.Sum(m => (m.SatOff ? 1 : 0) + (m.SunOff ? 1 : 0)),
                markers.Where(m => m.SatOff || m.SunOff).Select(m => m.EmployeeId).Distinct().Count()),
            Benchmark: new BenchmarkBrief(benchmarkHours, plan?.GeneratedAt));
    }

    /// <summary>
    /// The held plan's benchmark hours over these dates: each open day with data counts
    /// its plan's benchmark hours, else its Stage 1 total. Read straight from the held
    /// payload (camelCase, as the TypeScript app holds it).
    /// </summary>
    public static double BenchmarkHours(string payload, IReadOnlyList<DateOnly> dates)
    {
        double hours = 0;
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array) return hours;
        var want = dates.Select(d => d.ToIso()).ToHashSet();
        foreach (var d in days.EnumerateArray())
        {
            if (!d.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String || !want.Contains(date.GetString()!)) continue;
            if (!d.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;
            if (!Truthy(result, "hasData") || Truthy(result, "closed")) continue;
            var planBenchmark = d.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.Object
                && h.TryGetProperty("benchmark", out var b) && b.ValueKind == JsonValueKind.Number
                    ? b.GetDouble()
                    : (double?)null;
            var total = result.TryGetProperty("totalBenchmark", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : (double?)null;
            hours += planBenchmark ?? total ?? 0;
        }
        return hours;
    }

    private static bool Truthy(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => Js.Truthy(v.GetDouble()),
            JsonValueKind.String => v.GetString()!.Length > 0,
            JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };

    // ─── actions.ts ────────────────────────────────────────────────────────────

    public async Task<SalesResult> SaveOverrideAsync(SaveOverrideInput input, CancellationToken ct = default)
    {
        var denied = await scope.DenyStoreWriteAsync(input.StoreId, ct);
        if (denied is not null) return new SalesResult(false, denied);
        var userId = scope.User?.Id;
        // Back at the forecast with no reason is no adjustment: clear the day's
        // rather than keep an empty one that still shows on the chart (F10).
        var asRow = new AdjustmentRow { Closed = input.Closed, Method = input.Method, Pct = input.Pct, Value = input.Value };
        if (!Adjustment.ChangesSales(asRow) && Js.Trim(input.Note) == "")
            return await ClearAdjustmentAsync(userId, input.StoreId, input.Date, ct);

        await using var db = contexts.CreateSystem("sales adjustment, after the store-write check");
        // A single compulsory note, shown in the table's Notes column. Also mirrored
        // into the legacy `reason` column so nothing reading it separately breaks.
        var row = await db.SalesOverrides.FirstOrDefaultAsync(o => o.StoreId == input.StoreId && o.Date == input.Date, ct);
        if (row is null)
        {
            row = new SalesOverride { StoreId = input.StoreId, Date = input.Date };
            db.SalesOverrides.Add(row);
        }
        row.Closed = input.Closed;
        row.Method = input.Method;
        row.Pct = input.Pct;
        row.Value = input.Value;
        row.Reason = input.Note;
        row.Notes = input.Note;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            return new SalesResult(false, Message(ex));
        }

        // Override invalidates that day's approved plan/roster (spec 11.6). If the
        // invalidation fails the store would keep running an approved roster built
        // on figures that no longer apply - that is a failure, not a success.
        var invalidateErr = await UnapproveDayAsync(db, input.StoreId, input.Date, ct);
        if (invalidateErr is not null)
        {
            return new SalesResult(false,
                $"Override saved but the day could not be un-approved ({invalidateErr}) - withdraw and re-approve it manually.");
        }
        await AuditAsync(db, input.StoreId, input.Date, userId,
            new { closed = input.Closed, method = input.Method, pct = input.Pct, value = input.Value }, ct);
        return SalesResult.Success;
    }

    public async Task<SalesResult> ResetOverrideAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var denied = await scope.DenyStoreWriteAsync(storeId, ct);
        if (denied is not null) return new SalesResult(false, denied);
        return await ClearAdjustmentAsync(scope.User?.Id, storeId, date, ct);
    }

    // Remove a day's adjustment, back to the forecast. The day's approval goes
    // only when the adjustment had changed its sales: the plan was then built on a
    // figure that no longer applies (spec 11.6, as for a save). An empty one - 100%
    // and no reason - changed nothing the plan used, so clearing it leaves the
    // day's approval alone.
    private async Task<SalesResult> ClearAdjustmentAsync(Guid? userId, Guid storeId, DateOnly date, CancellationToken ct)
    {
        await using var db = contexts.CreateSystem("sales adjustment reset, after the store-write check");
        SalesOverride? existing;
        try
        {
            existing = await db.SalesOverrides.FirstOrDefaultAsync(o => o.StoreId == storeId && o.Date == date, ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            return new SalesResult(false, ex.Message);
        }
        if (existing is not null)
        {
            var was = new AdjustmentRow { Closed = existing.Closed, Method = existing.Method, Pct = existing.Pct, Value = existing.Value };
            db.SalesOverrides.Remove(existing);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                return new SalesResult(false, Message(ex));
            }
            if (Adjustment.ChangesSales(was))
            {
                var invalidateErr = await UnapproveDayAsync(db, storeId, date, ct);
                if (invalidateErr is not null)
                {
                    return new SalesResult(false,
                        $"Adjustment removed but the day could not be un-approved ({invalidateErr}) - withdraw and re-approve it manually.");
                }
            }
            await AuditAsync(db, storeId, date, userId,
                new { reset = true, closed = was.Closed, method = was.Method, pct = was.Pct, value = was.Value }, ct);
        }
        return SalesResult.Success;
    }

    /// <summary>The day_plans upsert: plan_status and roster_status back to "none". Null, or the error.</summary>
    private static async Task<string?> UnapproveDayAsync(RosterDbContext db, Guid storeId, DateOnly date, CancellationToken ct)
    {
        try
        {
            var day = await db.DayPlans.FirstOrDefaultAsync(d => d.StoreId == storeId && d.Date == date, ct);
            if (day is null)
            {
                day = new DayPlan { StoreId = storeId, Date = date };
                db.DayPlans.Add(day);
            }
            day.PlanStatus = DayStatuses.None;
            day.RosterStatus = DayStatuses.None;
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex)
        {
            return Message(ex);
        }
    }

    /// <summary>The roster_audit row. Like the TypeScript, a failed audit write does not fail the action.</summary>
    private static async Task AuditAsync(RosterDbContext db, Guid storeId, DateOnly date, Guid? userId, object detail, CancellationToken ct)
    {
        db.RosterAudit.Add(new RosterAudit
        {
            StoreId = storeId,
            Date = date,
            UserId = userId,
            Action = "sales_override",
            Detail = JsonSerializer.Serialize(detail),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private static string Message(DbUpdateException ex) => ex.InnerException?.Message ?? ex.Message;
}
