// Port of the bodies of the database functions in supabase/migrations:
// approve_roster_day (0049), overview_rollup (0027), leave_period_ends (0037),
// is_public_holiday (0007) and bump_cron_run (0043).

using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Configurations;
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Data.Services.Functions;

public sealed class DatabaseFunctionsService(IRosterDbContextFactory contexts, IStoreScope scope, TimeProvider clock) : IDatabaseFunctions
{
    // ── approve_roster_day ───────────────────────────────────────────────────
    // Atomic per-day roster approval. Approving a fortnight used to be a
    // sequence of independent statements per day; a failure or timeout
    // part-way through left the day half-written - shifts replaced but tiles
    // missing, or a day marked "approved" with no shifts behind it. One
    // transaction per day, with the status flip LAST so any failure leaves the
    // day cleanly un-approved and re-approvable.
    public async Task ApproveRosterDayAsync(Guid storeId, DateOnly date, IReadOnlyList<RosterDayShift>? shifts, CancellationToken ct = default)
    {
        await using var db = contexts.CreateSystem("approve_roster_day: publish a store-day's roster (the caller authorised it)");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // now(): the transaction's start time, the same for every row it writes.
        var now = clock.GetUtcNow();

        // Replace the whole day, locked shifts included: they are all present in the
        // payload, so nothing is lost and nothing can be orphaned. The previous body
        // skipped any locked shift with no pre-existing row, which silently dropped
        // every FIXED-ROSTER shift (the engine marks those locked too, and they have
        // never been persisted). The tiles go with their shifts (ON DELETE CASCADE).
        var dayShifts = db.GeneratedShifts.Where(s => s.StoreId == storeId && s.Date == date);
        await db.ShiftTaskBlocks.Where(b => dayShifts.Select(s => s.Id).Contains(b.ShiftId)).ExecuteDeleteAsync(ct);
        await dayShifts.ExecuteDeleteAsync(ct);

        foreach (var s in shifts ?? [])
        {
            var shift = new GeneratedShift
            {
                Id = Guid.NewGuid(),
                StoreId = storeId,
                Date = date,
                EmployeeId = s.EmployeeId,
                EmployeeName = s.EmployeeName,
                Role = s.Role,
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                BreakMinutes = s.BreakMinutes,
                Locked = s.Locked,
            };
            db.GeneratedShifts.Add(shift);
            foreach (var b in s.Blocks)
            {
                db.ShiftTaskBlocks.Add(new ShiftTaskBlock
                {
                    Id = Guid.NewGuid(),
                    ShiftId = shift.Id,
                    BlockTime = b.BlockTime,
                    LabourGroup = b.LabourGroup,
                    // nullif(b->>'sub_task', ''): a payload without labels stores null.
                    SubTask = string.IsNullOrEmpty(b.SubTask) ? null : b.SubTask,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // Status flip is last: a failure above rolls the whole day back un-approved.
        await UpsertAsync(
            db,
            () => db.DayPlans.Where(d => d.StoreId == storeId && d.Date == date)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.RosterStatus, DayStatuses.Approved).SetProperty(d => d.RosterGeneratedAt, now), ct),
            () => new DayPlan { StoreId = storeId, Date = date, RosterStatus = DayStatuses.Approved, RosterGeneratedAt = now },
            ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// insert ... on conflict do update: update the row; with none, insert it; and
    /// when a concurrent insert wins the race, update the row it wrote.
    /// </summary>
    internal static async Task UpsertAsync<T>(RosterDbContext db, Func<Task<int>> update, Func<T> insert, CancellationToken ct) where T : class
    {
        if (await update() > 0) return;
        var row = insert();
        db.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (await update() == 0) throw;
        }
    }

    // ── overview_rollup ──────────────────────────────────────────────────────
    // Per-date roll-up for the Overview dashboard: at most one row per date
    // (<= ~400 rows regardless of store count), computed where the data lives -
    // pulling row-level benchmark/actuals/day_plans/non-op for every store in
    // scope would be hundreds of thousands of rows per render for a national
    // roll-up. COALESCE mirrors the previous JS (Number(null) = 0): a null
    // column never poisons its row's sum.
    //
    // On SQL Server this is one set-based T-SQL statement, summing as decimal:
    // Postgres summed exact numerics, and a float SUM is off by a few units in
    // the last place - enough to turn a displayed 123.45 into 123.4 instead of
    // 123.5. Elsewhere (the SQLite the unit tests use) the same aggregation runs
    // here, with the same exact sums. SECURITY INVOKER: the caller's visible
    // stores bound the store list, as the tables' row-level security did.
    public async Task<IReadOnlyList<OverviewRollupRow>> OverviewRollupAsync(
        IReadOnlyList<Guid> storeIds, DateOnly from, DateOnly to, bool asPeer = false, CancellationToken ct = default)
    {
        var ids = storeIds.Distinct().ToList();
        if (!asPeer)
        {
            var readable = await scope.ReadableStoreIdsAsync(ct);
            if (readable is not null)
            {
                var visible = readable.ToHashSet();
                ids = ids.Where(visible.Contains).ToList();
            }
        }
        if (ids.Count == 0) return [];

        await using var db = asPeer
            ? contexts.CreateSystem("overview_rollup as a peer: the Dashboard shows every store's numbers by design")
            : contexts.Create();
        return db.Provider == DatabaseProvider.SqlServer
            ? await RollupSqlServerAsync(db, ids, from, to, ct)
            : await RollupInMemoryAsync(db, ids, from, to, ct);
    }

    private const string RollupSql = """
        with ids as (
          select cast([value] as uniqueidentifier) as id from openjson(@ids)
        ),
        b as (
          select bd.[date],
                 sum(cast(coalesce(bd.sales_forecast, 0) as decimal(38, 15))) as fs,
                 sum(cast(coalesce(bd.customer_count, 0) as decimal(38, 15))) as cc,
                 sum(cast(coalesce(bd.items, 0) as decimal(38, 15))) as it,
                 sum(cast(coalesce(bd.lr_hours_90, 0) as decimal(38, 15)) + cast(coalesce(bd.cleaning_hours_90, 0) as decimal(38, 15))
                   + cast(coalesce(bd.specials_hours_90, 0) as decimal(38, 15)) + cast(coalesce(bd.reset_hours_90, 0) as decimal(38, 15))
                   + cast(coalesce(bd.admin_hours_90, 0) as decimal(38, 15)) + cast(coalesce(bd.checkout_hours_90, 0) as decimal(38, 15))) as h90,
                 sum(cast(coalesce(bd.lr_hours_100, 0) as decimal(38, 15)) + cast(coalesce(bd.cleaning_hours_100, 0) as decimal(38, 15))
                   + cast(coalesce(bd.specials_hours_100, 0) as decimal(38, 15)) + cast(coalesce(bd.reset_hours_100, 0) as decimal(38, 15))
                   + cast(coalesce(bd.admin_hours_100, 0) as decimal(38, 15)) + cast(coalesce(bd.checkout_hours_100, 0) as decimal(38, 15))) as h100
            from benchmark_days bd
           where bd.store_id in (select id from ids) and bd.[date] between @from and @to
           group by bd.[date]
        ),
        a as (
          select ad.[date],
                 sum(cast(coalesce(ad.actual_sales, 0) as decimal(38, 15))) as s,
                 sum(cast(coalesce(ad.actual_items, 0) as decimal(38, 15))) as i,
                 sum(cast(coalesce(ad.actual_customers, 0) as decimal(38, 15))) as c,
                 sum(cast(coalesce(ad.actual_hours_100, 0) as decimal(38, 15))) as ah,
                 sum(cast(coalesce(ad.ukg_actual_hours, 0) as decimal(38, 15))) as kh
            from actuals_days ad
           where ad.store_id in (select id from ids) and ad.[date] between @from and @to
           group by ad.[date]
        ),
        n as (
          select ne.[date], sum(cast(coalesce(ne.minutes, 0) as decimal(38, 15))) as m
            from non_op_entries ne
           where ne.store_id in (select id from ids) and ne.[date] between @from and @to
           group by ne.[date]
        ),
        p as (
          select dp.[date],
                 max(case when dp.roster_status = 'approved' then 1 else 0 end) as ap,
                 max(case when dp.roster_status = 'generated' then 1 else 0 end) as gen
            from day_plans dp
           where dp.store_id in (select id from ids) and dp.[date] between @from and @to
           group by dp.[date]
        )
        select coalesce(b.[date], a.[date], n.[date], p.[date]) as [date],
               coalesce(b.fs, 0) as fs, coalesce(b.cc, 0) as cc, coalesce(b.it, 0) as it,
               coalesce(b.h90, 0) as h90, coalesce(b.h100, 0) as h100,
               coalesce(a.s, 0) as s, coalesce(a.i, 0) as i, coalesce(a.c, 0) as c,
               coalesce(a.ah, 0) as ah, coalesce(a.kh, 0) as kh,
               coalesce(n.m, 0) as m,
               coalesce(p.ap, 0) as ap, coalesce(p.gen, 0) as gen
          from b
          full join a on a.[date] = b.[date]
          full join n on n.[date] = coalesce(b.[date], a.[date])
          full join p on p.[date] = coalesce(b.[date], a.[date], n.[date])
         order by 1
        """;

    private static async Task<IReadOnlyList<OverviewRollupRow>> RollupSqlServerAsync(
        RosterDbContext db, List<Guid> ids, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = RollupSql;
            var timeout = db.Database.GetCommandTimeout();
            if (timeout is not null) cmd.CommandTimeout = timeout.Value;
            cmd.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids) });
            cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.Date) { Value = from.ToDateTime(TimeOnly.MinValue) });
            cmd.Parameters.Add(new SqlParameter("@to", SqlDbType.Date) { Value = to.ToDateTime(TimeOnly.MinValue) });
            var rows = new List<OverviewRollupRow>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new OverviewRollupRow
                {
                    Date = DateOnly.FromDateTime(r.GetDateTime(0)),
                    ForecastSales = Exact.ToDouble(r.GetDecimal(1)),
                    CustomerCount = Exact.ToDouble(r.GetDecimal(2)),
                    BenchItems = Exact.ToDouble(r.GetDecimal(3)),
                    Hours90 = Exact.ToDouble(r.GetDecimal(4)),
                    Hours100 = Exact.ToDouble(r.GetDecimal(5)),
                    ActualSales = Exact.ToDouble(r.GetDecimal(6)),
                    ActualItems = Exact.ToDouble(r.GetDecimal(7)),
                    ActualCustomers = Exact.ToDouble(r.GetDecimal(8)),
                    ActualHours100 = Exact.ToDouble(r.GetDecimal(9)),
                    KronosHours = Exact.ToDouble(r.GetDecimal(10)),
                    NonopMinutes = Exact.ToDouble(r.GetDecimal(11)),
                    AnyApproved = Convert.ToInt32(r.GetValue(12), CultureInfo.InvariantCulture) == 1,
                    AnyGenerated = Convert.ToInt32(r.GetValue(13), CultureInfo.InvariantCulture) == 1,
                });
            }
            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<IReadOnlyList<OverviewRollupRow>> RollupInMemoryAsync(
        RosterDbContext db, List<Guid> ids, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var bench = await db.BenchmarkDays.AsNoTracking()
            .Where(x => ids.Contains(x.StoreId) && x.Date >= from && x.Date <= to)
            .Select(x => new
            {
                x.Date, x.SalesForecast, x.CustomerCount, x.Items,
                x.LrHours90, x.CleaningHours90, x.SpecialsHours90, x.ResetHours90, x.AdminHours90, x.CheckoutHours90,
                x.LrHours100, x.CleaningHours100, x.SpecialsHours100, x.ResetHours100, x.AdminHours100, x.CheckoutHours100,
            })
            .ToListAsync(ct);
        var actuals = await db.ActualsDays.AsNoTracking()
            .Where(x => ids.Contains(x.StoreId) && x.Date >= from && x.Date <= to)
            .Select(x => new { x.Date, x.ActualSales, x.ActualItems, x.ActualCustomers, x.ActualHours100, x.UkgActualHours })
            .ToListAsync(ct);
        var nonOp = await db.NonOpEntries.AsNoTracking()
            .Where(x => ids.Contains(x.StoreId) && x.Date >= from && x.Date <= to)
            .Select(x => new { x.Date, x.Minutes })
            .ToListAsync(ct);
        var plans = await db.DayPlans.AsNoTracking()
            .Where(x => ids.Contains(x.StoreId) && x.Date >= from && x.Date <= to)
            .Select(x => new { x.Date, x.RosterStatus })
            .ToListAsync(ct);

        static decimal D(double? x) => Exact.ToDecimal(x ?? 0);
        var rows = new SortedDictionary<DateOnly, OverviewRollupRow>();
        OverviewRollupRow Row(DateOnly date) => rows.TryGetValue(date, out var r) ? r : rows[date] = new OverviewRollupRow { Date = date };

        foreach (var g in bench.GroupBy(x => x.Date))
        {
            rows[g.Key] = Row(g.Key) with
            {
                ForecastSales = Exact.ToDouble(g.Sum(x => D(x.SalesForecast))),
                CustomerCount = Exact.ToDouble(g.Sum(x => D(x.CustomerCount))),
                BenchItems = Exact.ToDouble(g.Sum(x => D(x.Items))),
                Hours90 = Exact.ToDouble(g.Sum(x => D(x.LrHours90) + D(x.CleaningHours90) + D(x.SpecialsHours90)
                    + D(x.ResetHours90) + D(x.AdminHours90) + D(x.CheckoutHours90))),
                Hours100 = Exact.ToDouble(g.Sum(x => D(x.LrHours100) + D(x.CleaningHours100) + D(x.SpecialsHours100)
                    + D(x.ResetHours100) + D(x.AdminHours100) + D(x.CheckoutHours100))),
            };
        }
        foreach (var g in actuals.GroupBy(x => x.Date))
        {
            rows[g.Key] = Row(g.Key) with
            {
                ActualSales = Exact.ToDouble(g.Sum(x => D(x.ActualSales))),
                ActualItems = Exact.ToDouble(g.Sum(x => D(x.ActualItems))),
                ActualCustomers = Exact.ToDouble(g.Sum(x => D(x.ActualCustomers))),
                ActualHours100 = Exact.ToDouble(g.Sum(x => D(x.ActualHours100))),
                KronosHours = Exact.ToDouble(g.Sum(x => D(x.UkgActualHours))),
            };
        }
        foreach (var g in nonOp.GroupBy(x => x.Date))
            rows[g.Key] = Row(g.Key) with { NonopMinutes = Exact.ToDouble(g.Sum(x => D(x.Minutes))) };
        foreach (var g in plans.GroupBy(x => x.Date))
        {
            rows[g.Key] = Row(g.Key) with
            {
                AnyApproved = g.Any(x => x.RosterStatus == DayStatuses.Approved),
                AnyGenerated = g.Any(x => x.RosterStatus == DayStatuses.Generated),
            };
        }
        return rows.Values.ToList();
    }

    // ── leave_period_ends ────────────────────────────────────────────────────
    // Distinct period ends without hauling every snapshot row: the DISTINCT
    // belongs in the database. Called through the service role only.
    public async Task<IReadOnlyList<DateOnly>> LeavePeriodEndsAsync(Guid? storeId = null, CancellationToken ct = default)
    {
        await using var db = contexts.CreateSystem("leave_period_ends: the leave-liability period list");
        return await db.LeaveLiabilitySnapshots.AsNoTracking()
            .Where(s => storeId == null || s.StoreId == storeId)
            .Select(s => s.PeriodEnd)
            .Distinct()
            .OrderByDescending(d => d)
            .ToListAsync(ct);
    }

    // ── is_public_holiday ────────────────────────────────────────────────────
    // Combine all three holiday tiers for a store/date (spec 11.14).
    public async Task<bool> IsPublicHolidayAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        await using var db = contexts.Create();
        if (await db.PublicHolidaysNational.AnyAsync(n => n.Date == date, ct)) return true;
        if (await db.PublicHolidaysState.AnyAsync(st => st.Date == date && db.Stores.Any(s => s.Id == storeId && s.State == st.State), ct)) return true;
        return await db.PublicHolidaysLocal.AnyAsync(l => l.StoreId == storeId && l.Date == date, ct);
    }

    // ── bump_cron_run ────────────────────────────────────────────────────────
    // Upsert the heartbeat. Absence of a row means the job has never completed;
    // a stale last_run_at means it has stopped.
    public async Task BumpCronRunAsync(string job, JsonElement? result = null, CancellationToken ct = default)
    {
        var json = result is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } r ? r.GetRawText() : null;
        await using var db = contexts.CreateSystem("bump_cron_run: a scheduled job's heartbeat");
        var now = clock.GetUtcNow();
        await UpsertAsync(
            db,
            () => db.CronRuns.Where(c => c.Job == job)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastRunAt, now).SetProperty(c => c.RunCount, c => c.RunCount + 1).SetProperty(c => c.LastResult, json), ct),
            () => new CronRun { Job = job, LastRunAt = now, RunCount = 1, LastResult = json },
            ct);
    }
}

/// <summary>
/// Exact sums of the doubles the database holds, as Postgres summed its numerics: each
/// value is taken as the shortest decimal that reads back as it (the figure the file
/// carried), the sum is exact, and the result is the double nearest to it - what
/// JavaScript's Number() made of Postgres's text.
/// </summary>
internal static class Exact
{
    public static decimal ToDecimal(double x) =>
        decimal.Parse(x.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static double ToDouble(decimal x) => double.Parse(x.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
