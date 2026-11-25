// Port of the database half of src/lib/csv/ingest.ts (storeCodeMap, holidaysForStores,
// resolvePeriod, the batch row, the chunked upserts and deleteStale). Each method is ONE
// statement of the TypeScript's - one PostgREST request - and is atomic: a write that
// fails changes nothing, so the pipeline in Ingest.cs can say exactly how far it got.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Feeds;

/// <summary>The upload_batches row an upload starts with.</summary>
public sealed record NewUploadBatch(
    string Type, string Filename, Guid? UploadedBy, Guid? RosterPeriodId,
    int TotalRows, int AcceptedRows, int RejectedRows, IReadOnlyList<RowError> ErrorReport);

/// <summary>A team member the contacts import may match (id, store, name, role, position, UKG id).</summary>
public sealed record ExistingEmployee(Guid Id, Guid StoreId, string Name, string Role, string? PositionCode, string? UkgId);

/// <summary>
/// A team member as the contacts import writes them (always active). <paramref name="Id"/> is
/// null for a new one; <paramref name="PositionCode"/> is null when the stored position is kept.
/// </summary>
public sealed record EmployeeImport(
    Guid? Id, Guid StoreId, string Name, string Role, string? PositionCode,
    double ContractHours, double NotionalHours, DateOnly StartDate, string UkgId);

/// <summary>The replace-style tables, whose rows a newer batch supersedes per store.</summary>
public enum StaleTable
{
    PosIntervalStats,
    LeaveRequestsUkg,
}

/// <summary>A write the database refused, in the database's words.</summary>
public sealed class FeedWriteException(string message) : Exception(message);

/// <summary>The reads and writes the upload pipeline makes.</summary>
public interface IFeedStore
{
    /// <summary>Every store code → id (referential validation + row resolution).</summary>
    Task<IReadOnlyDictionary<string, Guid>> StoreCodeMapAsync(CancellationToken ct = default);

    /// <summary>
    /// Public holidays for a SET of stores: the national list, the state lists for the
    /// states involved, and the local rows for those stores (holidaysForStores).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<DateOnly>>> HolidaysForStoresAsync(
        IReadOnlyCollection<Guid> storeIds, CancellationToken ct = default);

    /// <summary>Resolve-or-create the roster period starting on <paramref name="start"/>.</summary>
    Task<Guid> ResolvePeriodAsync(DateOnly start, DateOnly end, CancellationToken ct = default);

    Task<Guid> InsertBatchAsync(NewUploadBatch batch, CancellationToken ct = default);

    /// <summary>Set accepted_rows, and rejected_rows and error_report when given.</summary>
    Task UpdateBatchAsync(Guid batchId, int acceptedRows, int? rejectedRows, IReadOnlyList<RowError>? errorReport,
        CancellationToken ct = default);

    /// <summary>Upsert on (store_id, date), in one statement.</summary>
    Task UpsertBenchmarkDaysAsync(IReadOnlyList<BenchmarkDay> records, CancellationToken ct = default);

    /// <summary>Upsert on (store_id, date), in one statement.</summary>
    Task UpsertActualsDaysAsync(IReadOnlyList<ActualsDay> records, CancellationToken ct = default);

    /// <summary>Upsert one chunk on (store_id, date, block_time).</summary>
    Task UpsertPosIntervalStatsAsync(IReadOnlyList<PosIntervalStat> chunk, CancellationToken ct = default);

    /// <summary>Upsert one chunk on (store_id, period_end, ukg_employee_id).</summary>
    Task UpsertLeaveLiabilityAsync(IReadOnlyList<LeaveLiabilitySnapshot> chunk, CancellationToken ct = default);

    /// <summary>Upsert one chunk on (store_id, ukg_employee_id, leave_type, start_date, end_date).</summary>
    Task UpsertLeaveRequestsAsync(IReadOnlyList<LeaveRequestUkg> chunk, CancellationToken ct = default);

    /// <summary>
    /// For the given stores, remove the rows that <paramref name="batchId"/> did not write
    /// (batch_id is null or another batch).
    /// </summary>
    Task DeleteStaleAsync(StaleTable table, IReadOnlyList<Guid> storeIds, Guid batchId, CancellationToken ct = default);

    /// <summary>Every team member of the stores, in id order (Postgres uuid order).</summary>
    Task<IReadOnlyList<ExistingEmployee>> EmployeesOfStoresAsync(IReadOnlyCollection<Guid> storeIds, CancellationToken ct = default);

    /// <summary>
    /// Upsert one chunk of matched team members on id. With <paramref name="keepPosition"/> the
    /// position column is left as it is.
    /// </summary>
    Task UpsertEmployeesAsync(IReadOnlyList<EmployeeImport> chunk, bool keepPosition, CancellationToken ct = default);

    /// <summary>Insert one chunk of new team members.</summary>
    Task InsertEmployeesAsync(IReadOnlyList<EmployeeImport> chunk, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IFeedStore"/> on the roster database. Uploads write as the server (the
/// TypeScript used the service-role client): the endpoint or page has already checked who
/// may upload.
/// </summary>
public sealed class EfFeedStore(IRosterDbContextFactory contexts) : IFeedStore
{
    private const string Reason = "uploads and data feeds (lib/csv/ingest used the service-role client)";

    private RosterDbContext Db() => contexts.CreateSystem(Reason);

    private const string TouchedTwice = "ON CONFLICT DO UPDATE command cannot affect row a second time";

    private static void OnceEach<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key) where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        foreach (var r in rows)
            if (!seen.Add(key(r))) throw new FeedWriteException(TouchedTwice);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> StoreCodeMapAsync(CancellationToken ct = default)
    {
        await using var db = Db();
        var stores = await db.Stores.AsNoTracking().Select(s => new { s.Id, s.Code }).ToListAsync(ct);
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var s in stores) map[s.Code] = s.Id;
        return map;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<DateOnly>>> HolidaysForStoresAsync(
        IReadOnlyCollection<Guid> storeIds, CancellationToken ct = default)
    {
        await using var db = Db();
        var ids = storeIds.ToList();
        var stores = await db.Stores.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.State }).ToListAsync(ct);
        var states = stores.Select(s => s.State).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var national = await db.PublicHolidaysNational.AsNoTracking().Select(h => h.Date).ToListAsync(ct);
        var stateRows = states.Count > 0
            ? await db.PublicHolidaysState.AsNoTracking().Where(h => states.Contains(h.State)).Select(h => new { h.Date, h.State }).ToListAsync(ct)
            : [];
        var local = await db.PublicHolidaysLocal.AsNoTracking().Where(h => ids.Contains(h.StoreId)).Select(h => new { h.Date, h.StoreId }).ToListAsync(ct);

        var byState = stateRows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Select(r => r.Date).ToList());
        var byStore = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var st in stores)
        {
            var set = new HashSet<DateOnly>(national);
            if (byState.TryGetValue(st.State, out var dates)) set.UnionWith(dates);
            byStore[st.Id] = set;
        }
        foreach (var r in local)
            if (byStore.TryGetValue(r.StoreId, out var set)) set.Add(r.Date);
        return byStore.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<DateOnly>)kv.Value);
    }

    public async Task<Guid> ResolvePeriodAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        await using var db = Db();
        var existing = await db.RosterPeriods.AsNoTracking().Where(p => p.StartDate == start).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;
        var period = new RosterPeriod { StartDate = start, EndDate = end };
        db.RosterPeriods.Add(period);
        await db.SaveChangesAsync(ct);
        return period.Id;
    }

    public async Task<Guid> InsertBatchAsync(NewUploadBatch batch, CancellationToken ct = default)
    {
        await using var db = Db();
        var row = new UploadBatch
        {
            Type = batch.Type,
            Filename = batch.Filename,
            UploadedBy = batch.UploadedBy,
            RosterPeriodId = batch.RosterPeriodId,
            TotalRows = batch.TotalRows,
            AcceptedRows = batch.AcceptedRows,
            RejectedRows = batch.RejectedRows,
            ErrorReport = batch.ErrorReport.Select(ToReport).ToList(),
        };
        db.UploadBatches.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task UpdateBatchAsync(Guid batchId, int acceptedRows, int? rejectedRows, IReadOnlyList<RowError>? errorReport,
        CancellationToken ct = default)
    {
        await using var db = Db();
        var row = await db.UploadBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (row is null) return; // an update matching no row changes nothing, as in SQL
        row.AcceptedRows = acceptedRows;
        if (rejectedRows is { } rejected) row.RejectedRows = rejected;
        if (errorReport is not null) row.ErrorReport = errorReport.Select(ToReport).ToList();
        await db.SaveChangesAsync(ct);
    }

    private static UploadRowError ToReport(RowError e) => new(e.Row, e.StoreCode, e.Field, e.Message);

    public async Task UpsertBenchmarkDaysAsync(IReadOnlyList<BenchmarkDay> records, CancellationToken ct = default)
    {
        OnceEach(records, r => (r.StoreId, r.Date));
        await using var db = Db();
        var stores = records.Select(r => r.StoreId).Distinct().ToList();
        var dates = records.Select(r => r.Date).Distinct().ToList();
        var existing = (await db.BenchmarkDays.Where(b => stores.Contains(b.StoreId) && dates.Contains(b.Date)).ToListAsync(ct))
            .ToDictionary(b => (b.StoreId, b.Date));
        foreach (var r in records)
        {
            if (!existing.TryGetValue((r.StoreId, r.Date), out var row))
            {
                db.BenchmarkDays.Add(r);
                continue;
            }
            // The columns the upload sends; the reference-only registers/SCO split is kept.
            row.IsSco = r.IsSco;
            row.SalesForecast = r.SalesForecast;
            row.Items = r.Items;
            row.CustomerCount = r.CustomerCount;
            row.LrHours100 = r.LrHours100;
            row.CleaningHours100 = r.CleaningHours100;
            row.SpecialsHours100 = r.SpecialsHours100;
            row.ResetHours100 = r.ResetHours100;
            row.AdminHours100 = r.AdminHours100;
            row.CheckoutHours100 = r.CheckoutHours100;
            row.EmployeeCount = r.EmployeeCount;
            row.IsMhs = r.IsMhs;
            row.HasEsl = r.HasEsl;
            row.HasBaler = r.HasBaler;
            row.RosterPeriodId = r.RosterPeriodId;
            row.BatchId = r.BatchId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertActualsDaysAsync(IReadOnlyList<ActualsDay> records, CancellationToken ct = default)
    {
        OnceEach(records, r => (r.StoreId, r.Date));
        await using var db = Db();
        var stores = records.Select(r => r.StoreId).Distinct().ToList();
        var dates = records.Select(r => r.Date).Distinct().ToList();
        var existing = (await db.ActualsDays.Where(a => stores.Contains(a.StoreId) && dates.Contains(a.Date)).ToListAsync(ct))
            .ToDictionary(a => (a.StoreId, a.Date));
        foreach (var r in records)
        {
            if (!existing.TryGetValue((r.StoreId, r.Date), out var row))
            {
                db.ActualsDays.Add(r);
                continue;
            }
            row.ActualSales = r.ActualSales;
            row.ActualItems = r.ActualItems;
            row.ActualCustomers = r.ActualCustomers;
            row.ActualHours100 = r.ActualHours100;
            row.UkgActualHours = r.UkgActualHours;
            row.LrHours = r.LrHours;
            row.CleaningHours = r.CleaningHours;
            row.SpecialsHours = r.SpecialsHours;
            row.ResetHours = r.ResetHours;
            row.AdminHours = r.AdminHours;
            row.CheckoutHours = r.CheckoutHours;
            row.IsMhs = r.IsMhs;
            row.HasEsl = r.HasEsl;
            row.HasBaler = r.HasBaler;
            row.BatchId = r.BatchId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertPosIntervalStatsAsync(IReadOnlyList<PosIntervalStat> chunk, CancellationToken ct = default)
    {
        OnceEach(chunk, r => (r.StoreId, r.Date, r.BlockTime));
        await using var db = Db();
        var stores = chunk.Select(r => r.StoreId).Distinct().ToList();
        var dates = chunk.Select(r => r.Date).Distinct().ToList();
        var existing = (await db.PosIntervalStats.Where(p => stores.Contains(p.StoreId) && dates.Contains(p.Date)).ToListAsync(ct))
            .ToDictionary(p => (p.StoreId, p.Date, p.BlockTime));
        foreach (var r in chunk)
        {
            if (!existing.TryGetValue((r.StoreId, r.Date, r.BlockTime), out var row))
            {
                db.PosIntervalStats.Add(r);
                continue;
            }
            row.McoCustomers = r.McoCustomers;
            row.ScoCustomers = r.ScoCustomers;
            row.McoItems = r.McoItems;
            row.ScoItems = r.ScoItems;
            row.BatchId = r.BatchId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertLeaveLiabilityAsync(IReadOnlyList<LeaveLiabilitySnapshot> chunk, CancellationToken ct = default)
    {
        OnceEach(chunk, r => (r.StoreId, r.PeriodEnd, r.UkgEmployeeId));
        await using var db = Db();
        var stores = chunk.Select(r => r.StoreId).Distinct().ToList();
        var ends = chunk.Select(r => r.PeriodEnd).Distinct().ToList();
        var people = chunk.Select(r => r.UkgEmployeeId).Distinct().ToList();
        var existing = (await db.LeaveLiabilitySnapshots
                .Where(l => stores.Contains(l.StoreId) && ends.Contains(l.PeriodEnd) && people.Contains(l.UkgEmployeeId))
                .ToListAsync(ct))
            .ToDictionary(l => (l.StoreId, l.PeriodEnd, l.UkgEmployeeId));
        foreach (var r in chunk)
        {
            if (!existing.TryGetValue((r.StoreId, r.PeriodEnd, r.UkgEmployeeId), out var row))
            {
                db.LeaveLiabilitySnapshots.Add(r);
                continue;
            }
            row.UkgLocation = r.UkgLocation;
            row.EmployeeName = r.EmployeeName;
            row.Job = r.Job;
            row.RoleClass = r.RoleClass;
            row.ContractHours = r.ContractHours;
            row.NotionalHours = r.NotionalHours;
            row.SickHours = r.SickHours;
            row.AnnualHours = r.AnnualHours;
            row.LieuHours = r.LieuHours;
            row.BankedHours = r.BankedHours;
            row.Banked2Hours = r.Banked2Hours;
            row.BatchId = r.BatchId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertLeaveRequestsAsync(IReadOnlyList<LeaveRequestUkg> chunk, CancellationToken ct = default)
    {
        OnceEach(chunk, r => (r.StoreId, r.UkgEmployeeId, r.LeaveType, r.StartDate, r.EndDate));
        await using var db = Db();
        var stores = chunk.Select(r => r.StoreId).Distinct().ToList();
        var people = chunk.Select(r => r.UkgEmployeeId).Distinct().ToList();
        var existing = (await db.LeaveRequestsUkg
                .Where(l => stores.Contains(l.StoreId) && people.Contains(l.UkgEmployeeId))
                .ToListAsync(ct))
            .ToDictionary(l => (l.StoreId, l.UkgEmployeeId, l.LeaveType, l.StartDate, l.EndDate));
        foreach (var r in chunk)
        {
            if (!existing.TryGetValue((r.StoreId, r.UkgEmployeeId, r.LeaveType, r.StartDate, r.EndDate), out var row))
            {
                db.LeaveRequestsUkg.Add(r);
                continue;
            }
            row.EmployeeName = r.EmployeeName;
            row.Status = r.Status;
            row.Approver = r.Approver;
            row.BatchId = r.BatchId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteStaleAsync(StaleTable table, IReadOnlyList<Guid> storeIds, Guid batchId, CancellationToken ct = default)
    {
        await using var db = Db();
        var ids = storeIds.ToList();
        _ = table switch
        {
            StaleTable.PosIntervalStats => await db.PosIntervalStats
                .Where(p => ids.Contains(p.StoreId) && (p.BatchId == null || p.BatchId != batchId)).ExecuteDeleteAsync(ct),
            StaleTable.LeaveRequestsUkg => await db.LeaveRequestsUkg
                .Where(l => ids.Contains(l.StoreId) && (l.BatchId == null || l.BatchId != batchId)).ExecuteDeleteAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
    }

    public async Task<IReadOnlyList<ExistingEmployee>> EmployeesOfStoresAsync(IReadOnlyCollection<Guid> storeIds, CancellationToken ct = default)
    {
        await using var db = Db();
        var ids = storeIds.ToList();
        var rows = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.StoreId))
            .Select(e => new ExistingEmployee(e.Id, e.StoreId, e.Name, e.Role, e.PositionCode, e.UkgId))
            .ToListAsync(ct);
        // .order("id") on a Postgres uuid compares the bytes in order, which is the order
        // of the canonical lower-case text; SQL Server orders uniqueidentifier differently.
        return rows.OrderBy(e => e.Id.ToString("D"), StringComparer.Ordinal).ToList();
    }

    public async Task UpsertEmployeesAsync(IReadOnlyList<EmployeeImport> chunk, bool keepPosition, CancellationToken ct = default)
    {
        OnceEach(chunk, r => r.Id!.Value);
        await using var db = Db();
        var ids = chunk.Select(r => r.Id!.Value).ToList();
        var existing = (await db.Employees.Where(e => ids.Contains(e.Id)).ToListAsync(ct)).ToDictionary(e => e.Id);
        foreach (var r in chunk)
        {
            if (!existing.TryGetValue(r.Id!.Value, out var row))
            {
                db.Employees.Add(NewEmployee(r, r.Id));
                continue;
            }
            row.StoreId = r.StoreId;
            row.Name = r.Name;
            row.Role = r.Role;
            row.ContractHours = r.ContractHours;
            row.NotionalHours = r.NotionalHours;
            row.StartDate = r.StartDate;
            row.UkgId = r.UkgId;
            row.Active = true;
            if (!keepPosition) row.PositionCode = r.PositionCode;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task InsertEmployeesAsync(IReadOnlyList<EmployeeImport> chunk, CancellationToken ct = default)
    {
        await using var db = Db();
        foreach (var r in chunk) db.Employees.Add(NewEmployee(r, null));
        await db.SaveChangesAsync(ct);
    }

    private static Employee NewEmployee(EmployeeImport r, Guid? id)
    {
        var e = new Employee
        {
            StoreId = r.StoreId,
            Name = r.Name,
            Role = r.Role,
            PositionCode = r.PositionCode,
            ContractHours = r.ContractHours,
            NotionalHours = r.NotionalHours,
            StartDate = r.StartDate,
            UkgId = r.UkgId,
            Active = true,
        };
        if (id is { } given) e.Id = given;
        return e;
    }
}
