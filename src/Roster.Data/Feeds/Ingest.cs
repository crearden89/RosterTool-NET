// Port of src/lib/csv/ingest.ts.
// Validate (spec 4.4 mode=validate) and commit (mode=commit) an upload of any type. The
// writes go through IFeedStore (EfFeedStore on SQL Server), one call per statement the
// TypeScript sent to PostgREST, so the order of writes - and what a failure partway
// leaves behind - is the same.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Roster.Data.Entities;
using Roster.Engine.Core;
using Roster.Engine.Leave;

namespace Roster.Data.Feeds;

/// <summary>What validating an upload found (mode=validate). Serialises as the TypeScript JSON.</summary>
public record IngestValidation
{
    [JsonPropertyOrder(0)] public required string Type { get; init; }
    [JsonPropertyOrder(1)] public int TotalRows { get; init; }
    [JsonPropertyOrder(2)] public int ValidCount { get; init; }
    [JsonPropertyOrder(3)] public IReadOnlyList<RowError> Errors { get; init; } = [];
    [JsonPropertyOrder(4)] public IReadOnlyList<string> FileErrors { get; init; } = [];

    /// <summary>
    /// Rows that repeated another row of the file and were loaded once: exact
    /// copies for every type, plus - for leave requests, on commit - the same
    /// booking listed again under another status. Not a rejection.
    /// </summary>
    [JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DuplicatesCollapsed { get; init; }
}

/// <summary>What committing an upload did (mode=commit).</summary>
public sealed record IngestCommit : IngestValidation
{
    [JsonPropertyOrder(5)] public bool Committed { get; init; }
    [JsonPropertyOrder(6)] public int AcceptedRows { get; init; }
    /// <summary>The upload_batches row; null when nothing was committed.</summary>
    [JsonPropertyOrder(7)] public Guid? BatchId { get; init; }

    /// <summary>Contacts only: how many team members were created vs updated in place.</summary>
    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CreatedRows { get; init; }

    [JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UpdatedRows { get; init; }

    /// <summary>POS Quarter-Hour only: what the curve derivation used and left out.</summary>
    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PosDerivationReport>? PosReports { get; init; }
}

/// <summary>What the POS derivation decided, per store, for the upload summary.</summary>
public sealed record PosDerivationReport(
    [property: JsonPropertyName("storeCode")] string StoreCode,
    [property: JsonPropertyName("hasSco")] bool HasSco,
    [property: JsonPropertyName("datesUsed")] int DatesUsed,
    [property: JsonPropertyName("excludedPublicHoliday")] int ExcludedPublicHoliday,
    [property: JsonPropertyName("excludedNoTrade")] int ExcludedNoTrade,
    [property: JsonPropertyName("weekdaysWithoutData")] IReadOnlyList<int> WeekdaysWithoutData);

/// <summary>
/// Extra parameters a specific upload type needs. Only Leave Liability has one:
/// the export is "as at today" and carries no period of its own, so the pay
/// period it belongs to is chosen at upload time. <see cref="PeriodEnd"/> is the form's
/// "YYYY-MM-DD" text, checked here exactly as the TypeScript checks it.
/// </summary>
public sealed record UploadOptions(string? PeriodEnd = null)
{
    public static UploadOptions ForPeriodEnd(DateOnly periodEnd) => new(periodEnd.ToIso());
}

/// <summary>A commit that stopped partway; the message says what was saved and what was kept.</summary>
public sealed class UploadFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The upload pipeline: Admin → Uploads calls it directly, the two HTTP endpoints
/// (api/uploads, api/ingest) call it for everyone else.
/// </summary>
public interface IUploadIngest
{
    /// <summary>Validate only - no writes (validateUpload).</summary>
    Task<IngestValidation> ValidateUploadAsync(string type, string text, CancellationToken ct = default);

    /// <summary>
    /// Commit the valid rows (commitUpload). When <paramref name="acceptPartial"/> is false and
    /// any row is invalid, nothing is written (all-or-nothing). A write the database refuses
    /// throws <see cref="UploadFailedException"/>, after the batch history records how far it got.
    /// </summary>
    Task<IngestCommit> CommitUploadAsync(
        string type, string text, string filename, Guid? uploadedBy, bool acceptPartial,
        UploadOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Commit rows a direct feed has already validated (against the same store codes), through
    /// the same writes as an upload of <paramref name="type"/>.
    /// </summary>
    Task<IngestCommit> CommitRowsAsync<TRow>(
        string type, ValidationResult<TRow> result, string filename, Guid? uploadedBy, bool acceptPartial,
        UploadOptions? options = null, CancellationToken ct = default) where TRow : class, IFeedRow;
}

public sealed partial class UploadIngest(
    IFeedStore store,
    TimeProvider clock,
    IFeedParser<BenchmarkRow> benchmark,
    IFeedParser<ActualsRow> actuals,
    IFeedParser<ParsedLiabilityRow> leaveLiability,
    IFeedParser<ParsedRequestRow> leaveRequests,
    IFeedParser<ContactRow> contacts,
    IFeedParser<PosQuarterHourRow> posQuarterHour,
    ILogger<UploadIngest>? logger = null) : IUploadIngest
{
    /// <summary>Rows per write. PostgREST ran each request as one statement.</summary>
    private const int Chunk = 500;

    /// <summary>
    /// Store ids per delete. The list travelled in the request URL, and a
    /// network-wide file names hundreds of stores.
    /// </summary>
    private const int StoreChunk = 50;

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>The parsers wired to the CSV ports: what tests and tools use without DI.</summary>
    public static UploadIngest WithCsvFeeds(IFeedStore store, TimeProvider clock, ILogger<UploadIngest>? logger = null) =>
        new(store, clock, new BenchmarkCsvFeed(), new ActualsCsvFeed(), new LeaveLiabilityCsvFeed(),
            new LeaveRequestsCsvFeed(), new ContactsCsvFeed(), new PosQuarterHourCsvFeed(), logger);

    private ValidationResult<IFeedRow> ValidateByType(string type, string text, IReadOnlySet<string> codes) => type switch
    {
        FeedTypes.Benchmark => Erase(benchmark.Validate(text, codes)),
        FeedTypes.Actuals => Erase(actuals.Validate(text, codes)),
        FeedTypes.LeaveLiability => Erase(leaveLiability.Validate(text, codes)),
        FeedTypes.LeaveRequests => Erase(leaveRequests.Validate(text, codes)),
        FeedTypes.Contacts => Erase(contacts.Validate(text, codes)),
        FeedTypes.PosQuarterHour => Erase(posQuarterHour.Validate(text, codes)),
        _ => throw new ArgumentException($"Unknown upload type '{type}'", nameof(type)),
    };

    private static ValidationResult<IFeedRow> Erase<T>(ValidationResult<T> r) where T : class, IFeedRow =>
        new(r.TotalRows, r.ValidRows.Cast<IFeedRow>().ToList(), r.Errors, r.FileErrors, r.DuplicatesCollapsed);

    public async Task<IngestValidation> ValidateUploadAsync(string type, string text, CancellationToken ct = default)
    {
        var codes = (await store.StoreCodeMapAsync(ct)).Keys.ToHashSet(StringComparer.Ordinal);
        var result = ValidateByType(type, text, codes);
        return new IngestValidation
        {
            Type = type,
            TotalRows = result.TotalRows,
            ValidCount = result.ValidRows.Count,
            Errors = result.Errors,
            FileErrors = result.FileErrors,
            DuplicatesCollapsed = result.DuplicatesCollapsed ?? 0,
        };
    }

    public async Task<IngestCommit> CommitUploadAsync(
        string type, string text, string filename, Guid? uploadedBy, bool acceptPartial,
        UploadOptions? options = null, CancellationToken ct = default)
    {
        var map = await store.StoreCodeMapAsync(ct);
        var result = ValidateByType(type, text, map.Keys.ToHashSet(StringComparer.Ordinal));
        return await CommitAsync(type, result, map, filename, uploadedBy, acceptPartial, options ?? new UploadOptions(), ct);
    }

    public async Task<IngestCommit> CommitRowsAsync<TRow>(
        string type, ValidationResult<TRow> result, string filename, Guid? uploadedBy, bool acceptPartial,
        UploadOptions? options = null, CancellationToken ct = default) where TRow : class, IFeedRow
    {
        if (!FeedTypes.Uploads.Contains(type)) throw new ArgumentException($"Unknown upload type '{type}'", nameof(type));
        var map = await store.StoreCodeMapAsync(ct);
        return await CommitAsync(type, Erase(result), map, filename, uploadedBy, acceptPartial, options ?? new UploadOptions(), ct);
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex IsoDay();

    /// <summary>What a failed write leaves behind, in words for the upload history.</summary>
    public static string FailureMessage(string type, string phase, int written, int planned, string reason)
    {
        if (phase == "tidy")
        {
            var what = type == FeedTypes.LeaveRequests ? "bookings" : "readings";
            return
                $"All {planned} rows were saved, but removing the {what} this file no longer lists " +
                $"stopped partway ({reason}). Some stores may still show {what} the file has dropped. " +
                "Upload the same file again to finish.";
        }
        var kept = type is FeedTypes.LeaveRequests or FeedTypes.PosQuarterHour
            ? "Nothing was removed: each store keeps its earlier data until the whole file has loaded."
            : "Nothing was removed, and rows not yet saved keep their earlier values.";
        var saved = written == 0
            ? "Nothing was saved"
            : $"The upload stopped after {written} of {planned} rows were saved";
        return
            $"{saved}: the database refused a write ({reason}). {kept} " +
            "Upload the file again once the problem is fixed - re-sending the same file is safe.";
    }

    private async Task<IngestCommit> CommitAsync(
        string type, ValidationResult<IFeedRow> result, IReadOnlyDictionary<string, Guid> map, string filename,
        Guid? uploadedBy, bool acceptPartial, UploadOptions options, CancellationToken ct)
    {
        var @base = new IngestCommit
        {
            Type = type,
            TotalRows = result.TotalRows,
            ValidCount = result.ValidRows.Count,
            Errors = result.Errors,
            FileErrors = result.FileErrors,
            Committed = false,
            AcceptedRows = 0,
            BatchId = null,
        };

        // Leave Liability is a point-in-time snapshot, so it cannot be filed without
        // knowing WHICH pay period it is. Caught before the batch row is written.
        if (type == FeedTypes.LeaveLiability && !IsoDay().IsMatch(options.PeriodEnd ?? ""))
            return @base with { FileErrors = [.. @base.FileErrors, "Choose the pay-period end date this snapshot belongs to."] };

        // File-level errors always block; row errors block unless partial accepted.
        if (result.FileErrors.Count > 0) return @base;
        if (result.Errors.Count > 0 && !acceptPartial) return @base;
        if (result.ValidRows.Count == 0) return @base;

        // Resolve roster period for period-scoped uploads, snapped to the canonical
        // national fortnight grid (Core Period) rather than the file's raw min/max -
        // so roster_periods rows always align to real grid boundaries. Quarter-Hour
        // is now a standing per-store weekly pattern (day-of-week, not date), so it
        // has no period of its own.
        Guid? periodId = null;
        if (type == FeedTypes.Benchmark)
        {
            var p = Benchmark.BenchmarkPeriod(result.ValidRows.Cast<BenchmarkRow>().ToList());
            if (p is not null)
            {
                var gridStart = Period.PeriodStartFor(p.StartDate);
                periodId = await store.ResolvePeriodAsync(gridStart, Period.PeriodEndFrom(gridStart), ct);
            }
        }

        // Create the batch record first, so every data row can point at it - but it
        // claims nothing until the data has landed. It used to record every valid
        // row as accepted before writing any, so a write the database refused still
        // showed in the history as a clean, complete upload.
        var batchId = await store.InsertBatchAsync(
            new NewUploadBatch(type, filename, uploadedBy, periodId, result.TotalRows, 0, result.Errors.Count, result.Errors), ct);

        // Build + upsert type-specific records. `written` counts rows the database
        // has confirmed, so a failure can say exactly how far the write got.
        var duplicatesCollapsed = result.DuplicatesCollapsed ?? 0;
        var createdRows = 0;
        var updatedRows = 0;
        List<PosQuarterHourRow>? posRows = null;
        var written = 0;
        var planned = result.ValidRows.Count;
        var phase = "write";

        async Task UpsertChunks<T>(IReadOnlyList<T> records, Func<IReadOnlyList<T>, CancellationToken, Task> write)
        {
            for (var i = 0; i < records.Count; i += Chunk)
            {
                var chunk = records.Skip(i).Take(Chunk).ToList();
                await write(chunk, ct);
                written += chunk.Count;
            }
        }

        // The second half of a replace-style upload: for every store in the file,
        // remove what THIS batch did not write - dates that have rolled out of the POS
        // window, bookings deleted upstream. It runs only once every row of the new
        // file has landed. The old order was the reverse - delete each store's data,
        // then write - so a write that failed partway left stores with nothing.
        async Task DeleteStale(StaleTable table, IReadOnlyList<Guid> storeIds)
        {
            for (var i = 0; i < storeIds.Count; i += StoreChunk)
                await store.DeleteStaleAsync(table, storeIds.Skip(i).Take(StoreChunk).ToList(), batchId, ct);
        }

        try
        {
            if (type == FeedTypes.Benchmark)
            {
                var records = result.ValidRows.Cast<BenchmarkRow>().Select(r => new BenchmarkDay
                {
                    StoreId = map[r.StoreCode],
                    Date = r.Date,
                    IsSco = r.IsSco,
                    SalesForecast = r.SalesForecast,
                    Items = r.Items,
                    CustomerCount = r.CustomerCount,
                    LrHours100 = r.LrHours100,
                    CleaningHours100 = r.CleaningHours100,
                    SpecialsHours100 = r.SpecialsHours100,
                    ResetHours100 = r.ResetHours100,
                    AdminHours100 = r.AdminHours100,
                    CheckoutHours100 = r.CheckoutHours100,
                    EmployeeCount = IntColumn(r.EmployeeCount),
                    IsMhs = r.IsMhs,
                    HasEsl = r.HasEsl,
                    HasBaler = r.HasBaler,
                    RosterPeriodId = periodId,
                    BatchId = batchId,
                }).ToList();
                await store.UpsertBenchmarkDaysAsync(records, ct);
                written = records.Count;
            }
            else if (type == FeedTypes.Actuals)
            {
                double? Category(ActualsRow r, string col) => r.CategoryHours.TryGetValue(col, out var v) ? v : null;
                var records = result.ValidRows.Cast<ActualsRow>().Select(r => new ActualsDay
                {
                    StoreId = map[r.StoreCode],
                    Date = r.Date,
                    ActualSales = r.ActualSales,
                    ActualItems = r.ActualItems,
                    ActualCustomers = r.ActualCustomers,
                    ActualHours100 = r.ActualHours100,
                    UkgActualHours = r.UkgActualHours,
                    LrHours = Category(r, "LoadRunning_Hours"),
                    CleaningHours = Category(r, "Cleaning_Hours"),
                    SpecialsHours = Category(r, "Specials_Hours"),
                    ResetHours = Category(r, "LoadReset_Hours"),
                    AdminHours = Category(r, "AdminMgmt_Hours"),
                    CheckoutHours = Category(r, "Checkout_Hours"),
                    IsMhs = r.IsMhs,
                    HasEsl = r.HasEsl,
                    HasBaler = r.HasBaler,
                    BatchId = batchId,
                }).ToList();
                await store.UpsertActualsDaysAsync(records, ct);
                written = records.Count;
            }
            else if (type == FeedTypes.PosQuarterHour)
            {
                // The measured counts are kept exactly as they arrived - they ARE the
                // trade curve's source of record now, read and derived on demand.
                var rows = result.ValidRows.Cast<PosQuarterHourRow>().ToList();
                posRows = rows;
                // Each push REPLACES the store's sample rather than accumulating onto it:
                // the extract is a rolling four-week window, and an upsert alone would
                // leave the previous window's dates behind, silently turning every
                // weekday average into an eight-, twelve-, sixteen-week one. Only stores
                // present in this file are touched. The new sample is written first and
                // the old one removed after (DeleteStale), so a failed write leaves each
                // store's previous curve in place.
                var replaceIds = rows.Select(r => map[r.StoreCode]).Distinct().ToList();
                var records = rows.Select(r => new PosIntervalStat
                {
                    StoreId = map[r.StoreCode],
                    Date = r.Date,
                    BlockTime = TimeOnly.ParseExact(r.BlockTime, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    McoCustomers = r.McoCustomers,
                    ScoCustomers = r.ScoCustomers,
                    McoItems = r.McoItems,
                    ScoItems = r.ScoItems,
                    BatchId = batchId,
                }).ToList();
                // Four weeks x 49 blocks x every store in the file is far past the
                // single-statement ceiling, so these go up in chunks like the other bulk
                // ingests.
                await UpsertChunks(records, store.UpsertPosIntervalStatsAsync);
                phase = "tidy";
                await DeleteStale(StaleTable.PosIntervalStats, replaceIds);
            }
            else if (type == FeedTypes.LeaveLiability)
            {
                // One snapshot per (store, period, employee). Re-uploading a period
                // supersedes it, so backfilling ten periods is ten uploads in any order.
                var periodEnd = DateColumn(options.PeriodEnd!);
                var records = result.ValidRows.Cast<ParsedLiabilityRow>().Select(r => new LeaveLiabilitySnapshot
                {
                    StoreId = map[r.StoreCode],
                    PeriodEnd = periodEnd,
                    UkgEmployeeId = r.UkgEmployeeId,
                    UkgLocation = r.UkgLocation,
                    EmployeeName = r.EmployeeName,
                    Job = r.Job,
                    RoleClass = r.RoleClass,
                    ContractHours = r.ContractHours,
                    NotionalHours = r.NotionalHours,
                    SickHours = r.SickHours,
                    AnnualHours = r.AnnualHours,
                    LieuHours = r.LieuHours,
                    BankedHours = r.BankedHours,
                    Banked2Hours = r.Banked2Hours,
                    BatchId = batchId,
                }).ToList();
                await UpsertChunks(records, store.UpsertLeaveLiabilityAsync);
            }
            else if (type == FeedTypes.Contacts)
            {
                var plan = await PlanContacts(result.ValidRows.Cast<ContactRow>().ToList(), map, ct);
                await UpsertChunks(plan.UpdatesKeep, (chunk, t) => store.UpsertEmployeesAsync(chunk, keepPosition: true, t));
                await UpsertChunks(plan.UpdatesFull, (chunk, t) => store.UpsertEmployeesAsync(chunk, keepPosition: false, t));
                await UpsertChunks(plan.Inserts, store.InsertEmployeesAsync);
                createdRows = plan.Inserts.Count;
                updatedRows = plan.Updated;
            }
            else
            {
                // Leave requests: the export is a full-year view per store, so each store
                // present in the file has its bookings REPLACED - a request deleted
                // upstream must disappear here too, which an upsert alone never does.
                // Stores absent from the file are left untouched. The new bookings are
                // written first and the dropped ones removed after (DeleteStale): deleting
                // first meant one refused row left most of the network with no leave.
                // One row per booking before anything is written. The export can list the
                // same booking twice with different statuses, and two rows sharing the
                // unique key inside one upsert make the database refuse the whole statement.
                var deduped = LeaveDedupe.DedupeLeaveRequests(result.ValidRows.Cast<ParsedRequestRow>());
                duplicatesCollapsed += deduped.Dropped.Count;
                var records = deduped.Rows.Select(r => new LeaveRequestUkg
                {
                    StoreId = map[r.StoreCode],
                    UkgEmployeeId = r.UkgEmployeeId,
                    EmployeeName = r.EmployeeName,
                    LeaveType = r.LeaveType,
                    StartDate = r.StartDate,
                    EndDate = r.EndDate,
                    Status = r.Status,
                    Approver = r.Approver,
                    BatchId = batchId,
                }).ToList();
                var touched = records.Select(r => r.StoreId).Distinct().ToList();
                planned = records.Count;
                await UpsertChunks(records, store.UpsertLeaveRequestsAsync);
                phase = "tidy";
                await DeleteStale(StaleTable.LeaveRequestsUkg, touched);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Say what actually happened - how many rows landed and what was kept -
            // on the batch the history shows, and to whoever sent the file.
            var message = FailureMessage(type, phase, written, planned, ReasonOf(e));
            try
            {
                await store.UpdateBatchAsync(
                    batchId, written, result.Errors.Count + Math.Max(0, planned - written),
                    [new RowError(0, null, "Upload", message), .. result.Errors], ct);
            }
            catch (Exception recordFailure) when (recordFailure is not OperationCanceledException)
            {
                _log.LogError(recordFailure, "upload {BatchId}: the failure could not be recorded on the batch", batchId);
            }
            throw new UploadFailedException(message, e);
        }

        // Every row has landed; only now does the batch say so.
        try
        {
            await store.UpdateBatchAsync(batchId, result.ValidRows.Count, null, null, ct);
        }
        catch (Exception doneErr) when (doneErr is not OperationCanceledException)
        {
            _log.LogError(doneErr, "upload {BatchId}: data saved, batch count not updated: {Reason}", batchId, ReasonOf(doneErr));
        }

        // POS only: what the curve derivation made of the new sample. Nothing else
        // is written: the trade curve is assembled at READ time from these rows
        // (refdata getTradeCurveWeek), so retuning the weights or reloading a
        // sample re-states every plan with no derived write to keep in step.
        List<PosDerivationReport>? posReports = null;
        if (posRows is not null)
        {
            // Public holidays come out of the sample: four weeks is too short to
            // absorb a day that trades nothing like its weekday normally does.
            var storeIds = posRows.Select(r => map[r.StoreCode]).Distinct().ToList();
            var holidaysByStore = await store.HolidaysForStoresAsync(storeIds, ct);
            var excludeDates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in posRows)
                if (holidaysByStore.TryGetValue(map[r.StoreCode], out var set) && set.Contains(r.Date))
                    excludeDates.Add($"{r.StoreCode}|{r.Date.ToIso()}");
            var reports = PosAllocation.DeriveAllocations(posRows, new DeriveOptions(excludeDates)).Reports;
            // Surface what the derivation decided - which formula each store got and
            // what was left out - so a curve is never silently built on three weeks.
            posReports = reports.Select(r => new PosDerivationReport(
                r.StoreCode, r.HasSco, r.DatesUsed, r.DatesExcludedPublicHoliday, r.DatesExcludedNoTrade, r.WeekdaysWithoutData)).ToList();
        }

        return @base with
        {
            Committed = true,
            AcceptedRows = result.ValidRows.Count,
            DuplicatesCollapsed = duplicatesCollapsed,
            CreatedRows = createdRows,
            UpdatedRows = updatedRows,
            PosReports = posReports,
            BatchId = batchId,
        };
    }

    // UKG Contact Details: creates or updates TEAM MEMBERS. Match by the UKG
    // employee id first (survives name changes), then by exact name within the
    // store (so a team typed in by hand before this import existed doesn't get
    // duplicated); otherwise insert. Never deactivates anyone - a partial
    // export must not switch off the rest of the team.
    private sealed record ContactsPlan(
        List<EmployeeImport> Inserts, List<EmployeeImport> UpdatesKeep, List<EmployeeImport> UpdatesFull, int Updated);

    private async Task<ContactsPlan> PlanContacts(List<ContactRow> rows, IReadOnlyDictionary<string, Guid> map, CancellationToken ct)
    {
        var today = Dates.TodayAu(clock.GetUtcNow());
        var touched = rows.Select(r => map[r.StoreCode]).Distinct().ToList();
        // Every team member of the touched stores (a national contacts feed touches
        // every store), in id order, so the last of two same-named people wins byName
        // exactly as it did.
        var existing = await store.EmployeesOfStoresAsync(touched, ct);
        var byUkg = new Dictionary<string, ExistingEmployee>(StringComparer.Ordinal);
        var byName = new Dictionary<string, ExistingEmployee>(StringComparer.Ordinal);
        foreach (var e in existing)
        {
            if (!string.IsNullOrEmpty(e.UkgId)) byUkg[$"{e.StoreId}|{e.UkgId}"] = e;
            byName[$"{e.StoreId}|{JsText.ToLower(Js.Trim(e.Name))}"] = e;
        }
        // Each existing team member is matched by at most ONE row, and matches by
        // UKG id are settled before any by name. Two people sharing a name used to
        // both match the same record: in one write the database refused it as
        // touching a row twice, across two writes the second silently overwrote
        // the first. A name match that finds its record taken is a new person.
        var claimed = new HashSet<Guid>();
        foreach (var r in rows)
            if (byUkg.TryGetValue($"{map[r.StoreCode]}|{r.UkgEmployeeId}", out var hit)) claimed.Add(hit.Id);

        // Matched rows update in BATCHES (chunked upserts keyed on id), not one
        // round trip per employee - a national file is ~18k rows and a per-row
        // loop would outlive any request timeout. Two batches because "keep the
        // manually-set position" means the position column must be left alone.
        var inserts = new List<EmployeeImport>();
        var updatesKeep = new List<EmployeeImport>();
        var updatesFull = new List<EmployeeImport>();
        var updated = 0;
        foreach (var r in rows)
        {
            var storeId = map[r.StoreCode];
            byUkg.TryGetValue($"{storeId}|{r.UkgEmployeeId}", out var match);
            if (match is null &&
                byName.TryGetValue($"{storeId}|{JsText.ToLower(r.Name)}", out var named) && !claimed.Contains(named.Id))
            {
                match = named;
                claimed.Add(named.Id);
            }
            var position = UkgContacts.PositionForImport(r.Role, r.ContractHours, r.BirthDate, today);
            if (match is not null)
            {
                // A manually-set position (year band, SA-LR, JSA-17) survives the
                // re-import as long as it still agrees on the base role - the export
                // carries no year band, so overwriting would undo the manual edit the
                // import explicitly leaves to the user.
                var keepPosition = match.PositionCode is not null && match.PositionCode.Split('-')[0] == r.Role;
                var record = new EmployeeImport(
                    match.Id, storeId, r.Name, r.Role, keepPosition ? null : position,
                    r.ContractHours, r.NotionalHours, r.HireDate, r.UkgEmployeeId);
                (keepPosition ? updatesKeep : updatesFull).Add(record);
                updated++;
            }
            else
            {
                inserts.Add(new EmployeeImport(
                    null, storeId, r.Name, r.Role, position, r.ContractHours, r.NotionalHours, r.HireDate, r.UkgEmployeeId));
            }
        }
        return new ContactsPlan(inserts, updatesKeep, updatesFull, updated);
    }

    /// <summary>The database's own words for a refused write.</summary>
    private static string ReasonOf(Exception e)
    {
        while (e.InnerException is not null && e is not FeedWriteException) e = e.InnerException;
        return e.Message;
    }

    /// <summary>An INT column's value, refused as Postgres refuses one it cannot hold.</summary>
    private static int IntColumn(double v)
    {
        if (!double.IsFinite(v) || v < int.MinValue || v > int.MaxValue || v != Math.Floor(v))
            throw new FeedWriteException($"invalid input syntax for type integer: \"{Js.NumberToString(v)}\"");
        return (int)v;
    }

    /// <summary>A DATE column's value from "YYYY-MM-DD" text, refused as Postgres refuses an impossible date.</summary>
    private static DateOnly DateColumn(string iso)
    {
        var real = CsvParse.ToIsoDate(iso);
        if (real is null || real != iso) throw new FeedWriteException($"date/time field value out of range: \"{iso}\"");
        return CsvParse.Date(real);
    }
}
