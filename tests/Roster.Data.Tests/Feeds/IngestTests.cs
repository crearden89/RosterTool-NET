// Port of src/lib/csv/ingest.test.ts.
// The real UploadIngest, driven through the real EfFeedStore against the roster database
// (in-memory SQLite, Support/TestDatabase), with a refusal injected before a chosen
// statement - the TypeScript's fakeSupabase failWhen. Roster Engine Review 07/11/2026,
// A09-1 / A09-2: replace-style uploads deleted first and wrote second, so one refused
// write left stores with no leave bookings or no trade sample, and the upload history
// recorded the file as fully accepted either way.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Feeds;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;
using Roster.Engine.Core;
using Roles = Roster.Engine.Core.Roles;

namespace Roster.Data.Tests.Feeds;

public sealed class IngestTests : IDisposable
{
    private const string Timeout = "canceling statement due to statement timeout";

    private readonly TestDatabase _db = new();
    private readonly RefusingStore _store;
    private readonly UploadIngest _ingest;
    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);
    private readonly string[] _codes = Enumerable.Range(1, 30).Select(i => $"G{i:000}").ToArray();
    private Guid _oldBatch;

    public IngestTests()
    {
        using (var sys = _db.System())
        {
            var group = new DirectorGroup { Name = "Example Group" };
            sys.DirectorGroups.Add(group);
            sys.SaveChanges();
            var region = new Region { Name = "901 - Example Region", DirectorGroupId = group.Id };
            sys.Regions.Add(region);
            sys.SaveChanges();
            foreach (var code in _codes)
                sys.Stores.Add(new Store
                {
                    Code = code, Name = $"Example Store {code}", Slug = code.ToLowerInvariant(), RegionId = region.Id,
                    State = Roster.Data.Entities.AuStates.Nsw, KioskToken = $"kiosk-{code}",
                });
            var order = 0;
            foreach (var p in Positions.All)
                sys.Positions.Add(new Position
                {
                    Code = p.Code, Label = p.Label, BaseRole = p.BaseRole, WeeklyHours = p.WeeklyHours, YearBand = p.YearBand,
                    HasRate = p.HasRate, SortOrder = order++,
                });
            var old = new UploadBatch { Type = FeedTypes.LeaveRequests, Filename = "old.csv" };
            sys.UploadBatches.Add(old);
            sys.SaveChanges();
            _oldBatch = old.Id;
            foreach (var s in sys.Stores) _ids[s.Code] = s.Id;
        }
        _store = new RefusingStore(new EfFeedStore(new TestContexts(_db, AccessContext.System("ingest tests"))));
        _ingest = UploadIngest.WithCsvFeeds(_store, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private List<T> Table<T>(Func<RosterDbContext, IQueryable<T>> set)
    {
        using var db = _db.System();
        return set(db).ToList();
    }

    private List<UploadBatch> Batches() => Table(db => db.UploadBatches.Where(b => b.Id != _oldBatch));

    private static async Task<string> Thrown(Task task)
    {
        try
        {
            await task;
            return "";
        }
        catch (UploadFailedException e)
        {
            return e.Message;
        }
    }

    private static IEnumerable<DateOnly> Days(DateOnly start, int n) => Enumerable.Range(0, n).Select(start.AddDays);
    private static string Dmy(DateOnly d) => $"{d.Day}/{d.Month:00}/{d.Year}";

    // ─── Leave requests ──────────────────────────────────────────────────────────

    private const string ReqH = "StoreID,Location,Employee ID,Employee Name,Leave Type,Start Date,End Date,Status,Approver";

    private static string Booking(string code, int k) =>
        $"{code},505999,{code}-{k},\"4-Person, {k}\",REQ - Annual Leave,01-12-2026 12:00 AM,05-12-2026 12:00 AM,Approved,x";

    /// <summary>30 stores x 40 bookings already held, half of them from before batches were tracked.</summary>
    private void HoldBookings()
    {
        using var sys = _db.System();
        for (var i = 0; i < _codes.Length; i++)
            for (var k = 0; k < 40; k++)
                sys.LeaveRequestsUkg.Add(new LeaveRequestUkg
                {
                    StoreId = _ids[_codes[i]], UkgEmployeeId = $"{_codes[i]}-{k}", EmployeeName = "x", LeaveType = "REQ - Annual Leave",
                    StartDate = new DateOnly(2026, 12, 1), EndDate = new DateOnly(2026, 12, 5), Status = "Approved",
                    BatchId = i % 2 == 1 ? _oldBatch : null,
                });
        sys.SaveChanges();
    }

    /// <summary>The re-export: booking 39 has been deleted upstream, booking 40 is new.</summary>
    private string ReExport() =>
        string.Join("\n", [ReqH, .. _codes.SelectMany(c => Enumerable.Range(0, 39).Select(k => Booking(c, k)).Append(Booking(c, 40)))]);

    private Dictionary<Guid, HashSet<string>> BookingsByStore() =>
        Table(db => db.LeaveRequestsUkg).GroupBy(r => r.StoreId).ToDictionary(g => g.Key, g => g.Select(r => r.UkgEmployeeId).ToHashSet());

    [Fact]
    public async Task Leave_requests_keep_every_store_bookings_when_a_write_is_refused_partway()
    {
        HoldBookings();
        _store.RefuseNth("leave_requests_ukg.upsert", 2);

        var message = await Thrown(_ingest.CommitUploadAsync(FeedTypes.LeaveRequests, ReExport(), "req.csv", null, false));
        Assert.Contains("stopped after 500 of 1200 rows were saved", message);
        Assert.Contains("Nothing was removed", message);

        // The review's run of the old code: 17 of 30 stores left with no bookings.
        var held = BookingsByStore();
        foreach (var c in _codes)
            for (var k = 0; k < 40; k++) Assert.True(held[_ids[c]].Contains($"{c}-{k}"), $"{c}-{k}");
        // The history says what happened, not "1200 accepted".
        var batch = Assert.Single(Batches());
        Assert.Equal((500, 700), (batch.AcceptedRows, batch.RejectedRows));
        Assert.Equal((0, message), (batch.ErrorReport[0].Row, batch.ErrorReport[0].Message));
    }

    [Fact]
    public async Task Leave_requests_replace_each_store_bookings_once_every_row_has_landed()
    {
        HoldBookings();
        var res = await _ingest.CommitUploadAsync(FeedTypes.LeaveRequests, ReExport(), "req.csv", null, false);
        Assert.True(res.Committed);

        var held = BookingsByStore();
        foreach (var c in _codes)
        {
            var ids = held[_ids[c]];
            Assert.Equal(40, ids.Count);
            Assert.DoesNotContain($"{c}-39", ids); // deleted upstream - gone here too
            Assert.Contains($"{c}-40", ids);
        }
        Assert.All(Table(db => db.LeaveRequestsUkg), r => Assert.Equal(res.BatchId, r.BatchId));
        var batch = Assert.Single(Batches());
        Assert.Equal((1200, 0), (batch.AcceptedRows, batch.RejectedRows));
    }

    [Fact]
    public async Task Leave_requests_finish_the_job_when_the_same_file_is_sent_again_after_a_failure()
    {
        HoldBookings();
        _store.RefuseNth("leave_requests_ukg.upsert", 2);
        await Thrown(_ingest.CommitUploadAsync(FeedTypes.LeaveRequests, ReExport(), "req.csv", null, false));
        _store.FailWhen = null;

        var res = await _ingest.CommitUploadAsync(FeedTypes.LeaveRequests, ReExport(), "req.csv", null, false);
        Assert.True(res.Committed);
        var rows = Table(db => db.LeaveRequestsUkg);
        Assert.Equal(1200, rows.Count);
        Assert.All(rows, r => Assert.Equal(res.BatchId, r.BatchId));
    }

    [Fact]
    public async Task Leave_requests_leave_stores_that_are_not_in_the_file_alone()
    {
        HoldBookings();
        await _ingest.CommitUploadAsync(FeedTypes.LeaveRequests, string.Join("\n", ReqH, Booking("G001", 0)), "req.csv", null, false);
        var held = BookingsByStore();
        Assert.Single(held[_ids["G001"]]);
        Assert.Equal(40, held[_ids["G002"]].Count);
    }

    [Fact]
    public async Task Leave_requests_say_so_when_the_tidy_up_after_a_complete_write_stops_partway()
    {
        HoldBookings();
        _store.RefuseNth("leave_requests_ukg.delete", 1);
        var message = await Thrown(_ingest.CommitUploadAsync(FeedTypes.LeaveRequests, ReExport(), "req.csv", null, false));
        Assert.Contains("All 1200 rows were saved", message);
        var batch = Assert.Single(Batches());
        Assert.Equal((1200, 0), (batch.AcceptedRows, batch.RejectedRows));
    }

    // ─── POS quarter-hour ────────────────────────────────────────────────────────

    private const string PosH = "StoreID,RegisterType1,Period,DataGroup,Interval,value";
    private static readonly List<DateOnly> OldWindow = Days(new DateOnly(2026, 6, 22), 28).ToList();
    private static readonly List<DateOnly> NewWindow = Days(new DateOnly(2026, 7, 20), 28).ToList();

    private void HoldSamples()
    {
        using var sys = _db.System();
        foreach (var code in new[] { "G001", "G002" })
            foreach (var date in OldWindow)
                sys.PosIntervalStats.Add(new PosIntervalStat
                {
                    StoreId = _ids[code], Date = date, BlockTime = new TimeOnly(9, 0), McoCustomers = 5, ScoCustomers = 0,
                    McoItems = 50, ScoItems = 0, BatchId = _oldBatch,
                });
        sys.SaveChanges();
    }

    private static string PosFile()
    {
        var lines = new List<string> { PosH };
        foreach (var code in new[] { "G001", "G002" })
            foreach (var d in NewWindow)
                for (var b = 8 * 60; b < 20 * 60; b += 15)
                {
                    var hhmm = $"{b / 60:00}:{b % 60:00}";
                    lines.Add($"{code},M,{Dmy(d)},2. Customer Count,{hhmm} - {hhmm},7");
                }
        return string.Join("\n", lines);
    }

    private HashSet<DateOnly> DatesHeld(string code) =>
        Table(db => db.PosIntervalStats).Where(r => r.StoreId == _ids[code]).Select(r => r.Date).ToHashSet();

    [Fact]
    public async Task Pos_keeps_each_store_previous_sample_when_a_write_is_refused_partway()
    {
        HoldSamples();
        _store.RefuseNth("pos_interval_stats.upsert", 4);
        var message = await Thrown(_ingest.CommitUploadAsync(FeedTypes.PosQuarterHour, PosFile(), "pos.csv", null, false));
        Assert.Contains("stopped after 1500 of 2688 rows were saved", message);

        // The old code had already deleted G002's sample: 14 of its 28 dates survived.
        foreach (var code in new[] { "G001", "G002" })
        {
            var held = DatesHeld(code);
            foreach (var d in OldWindow) Assert.True(held.Contains(d), $"{code} {d}");
        }
    }

    [Fact]
    public async Task Pos_replaces_the_sample_once_the_whole_file_has_landed()
    {
        HoldSamples();
        var res = await _ingest.CommitUploadAsync(FeedTypes.PosQuarterHour, PosFile(), "pos.csv", null, false);
        Assert.True(res.Committed);
        foreach (var code in new[] { "G001", "G002" }) Assert.Equal(NewWindow, DatesHeld(code).Order());
        Assert.Equal([28, 28], res.PosReports!.Select(r => r.DatesUsed));
    }

    // ─── Benchmark and actuals ───────────────────────────────────────────────────

    private const string BenchH =
        "StoreID,Date,Has_Sco,Retail_Sales,Total_Items1,Customer_Count,LoadRunning_Hours,Cleaning_Hours,Specials_Hours,LoadReset_Hours,AdminMgmt_Hours,Checkout_Hours";

    private static string BenchRow(DateOnly d, string lr = "18") => $"G001,{d.ToIso()},1,45000,3200,1800.5,{lr},9,4.5,3.6,7.2,36";

    [Fact]
    public async Task Benchmark_records_nothing_as_accepted_when_the_write_is_refused()
    {
        _store.FailWhen = op => op == "benchmark_days.upsert";
        var text = string.Join("\n", [BenchH, .. Days(new DateOnly(2026, 7, 20), 14).Select(d => BenchRow(d))]);
        var message = await Thrown(_ingest.CommitUploadAsync(FeedTypes.Benchmark, text, "bench.csv", null, false));
        Assert.StartsWith("Nothing was saved", message);
        Assert.Empty(Table(db => db.BenchmarkDays));
        var batch = Assert.Single(Batches());
        Assert.Equal((0, 14), (batch.AcceptedRows, batch.RejectedRows));
    }

    [Fact]
    public async Task Benchmark_stops_a_store_day_listed_twice_with_different_figures_before_it_reaches_the_database()
    {
        // Postgres refused the whole file ("ON CONFLICT DO UPDATE command cannot
        // affect row a second time") and the history still said 15 accepted.
        var text = string.Join("\n",
            [BenchH, .. Days(new DateOnly(2026, 7, 20), 14).Select(d => BenchRow(d)), BenchRow(new DateOnly(2026, 7, 21), "20")]);
        var v = await _ingest.ValidateUploadAsync(FeedTypes.Benchmark, text);
        Assert.Equal([2, 15], v.Errors.Select(e => e.Row));

        var strict = await _ingest.CommitUploadAsync(FeedTypes.Benchmark, text, "bench.csv", null, false);
        Assert.False(strict.Committed);
        Assert.Empty(Batches());

        var partial = await _ingest.CommitUploadAsync(FeedTypes.Benchmark, text, "bench.csv", null, true);
        Assert.True(partial.Committed);
        Assert.Equal(13, Table(db => db.BenchmarkDays).Count);
        var batch = Assert.Single(Batches());
        Assert.Equal((13, 2), (batch.AcceptedRows, batch.RejectedRows));
    }

    [Fact]
    public async Task Actuals_lose_one_row_not_the_push_when_a_feed_sends_an_impossible_date()
    {
        const string actH =
            "StoreID,Period,Retail_Sales,Total_Items,Customer_Count,LoadRunning_Hours,Cleaning_Hours,Specials_Hours,LoadReset_Hours,AdminMgmt_Hours,Checkout_Hours,Kronos_Actual_Hours";
        static string Good(string d) => $"G001,{d},44200,3150,1810,72.5,18,9,14,29,40,180";
        var text = string.Join("\n", actH, Good("2026-07-20"), "G001,31/02/2026,1,1,1,1,1,1,1,1,1,1", Good("2026-07-21"));
        // Exactly what /api/ingest/actuals calls: partial accept.
        var res = await _ingest.CommitUploadAsync(FeedTypes.Actuals, text, "api:sync", null, true);
        Assert.True(res.Committed);
        Assert.Equal([new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21)], Table(db => db.ActualsDays).Select(r => r.Date).Order());
        var batch = Assert.Single(Batches());
        Assert.Equal((2, 1), (batch.AcceptedRows, batch.RejectedRows));
    }

    // ─── Leave liability ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Leave_liability_rejects_an_unreadable_balance_up_front_instead_of_half_writing_the_snapshot()
    {
        var lines = new List<string>
        {
            "StoreID,Location,Employee Name,Employee ID,Job,Contract Hrs,Notional Hrs,Sick Lve Accrued,Annual Lve Accrued,Lieu Hrs Accrued,Banked Hrs Accrued,Banked 2 Hrs Accrued",
        };
        foreach (var c in _codes)
            for (var k = 0; k < 40; k++)
            {
                var sick = c == "G020" && k == 0 ? "-" : "12.50";
                lines.Add($"{c},505999,\"4-Person, {k}\",{c}-{k},ST ASSIST,50.00,5.00,{sick},40.00,,,");
            }
        var text = string.Join("\n", lines);
        Assert.Single((await _ingest.ValidateUploadAsync(FeedTypes.LeaveLiability, text)).Errors);

        var options = new UploadOptions("2026-08-08");
        var strict = await _ingest.CommitUploadAsync(FeedTypes.LeaveLiability, text, "liab.csv", null, false, options);
        Assert.False(strict.Committed);
        Assert.Empty(Table(db => db.LeaveLiabilitySnapshots));

        var partial = await _ingest.CommitUploadAsync(FeedTypes.LeaveLiability, text, "liab.csv", null, true, options);
        Assert.True(partial.Committed);
        Assert.Equal(1199, Table(db => db.LeaveLiabilitySnapshots).Count);
    }

    // ─── Contacts ────────────────────────────────────────────────────────────────

    private const string ContactsH = "StoreID,Location,Employee Name,Employee ID,Job,Contract Hrs,Notional Hrs,Birth Date,Hire Date";

    private static string Person(string name, string id, string contract = "50.00") =>
        $"G001,505001,\"{name}\",{id},ST ASSIST,{contract},5.00,01/01/2000,01/02/2024";

    private void HoldEmployees(params Employee[] employees)
    {
        using var sys = _db.System();
        sys.Employees.AddRange(employees);
        sys.SaveChanges();
    }

    [Fact]
    public async Task Contacts_load_a_repeated_new_hire_once()
    {
        // The second copy used to fail the unique index after the rest of the
        // team had already been updated.
        HoldEmployees(new Employee
        {
            Id = Guid.NewGuid(), StoreId = _ids["G001"], Name = "1-Reyes, Noah", Role = Roles.SM, PositionCode = "SM-45-1", UkgId = "910001",
        });
        var text = string.Join("\n",
            ContactsH,
            "G001,505001,\"1-Reyes, Noah\",910001,ST MANAGER,100.00,10.00,01/01/1980,01/02/2015",
            Person("4-Nye, Harper", "910020"),
            Person("4-Nye, Harper", "910020"));
        var res = await _ingest.CommitUploadAsync(FeedTypes.Contacts, text, "contacts.csv", null, false);
        Assert.Equal((true, 1, 1, 1), (res.Committed, res.CreatedRows, res.UpdatedRows, res.DuplicatesCollapsed));
        Assert.Single(Table(db => db.Employees).Where(e => e.UkgId == "910020"));
    }

    [Fact]
    public async Task Contacts_never_match_one_team_member_to_two_rows()
    {
        // A hand-typed record, plus one UKG already knows by id - both named the
        // same as two different people in the file.
        Guid typed = Guid.NewGuid(), known = Guid.NewGuid();
        HoldEmployees(
            new Employee { Id = typed, StoreId = _ids["G001"], Name = "4-Smith, Jo", Role = Roles.SA, PositionCode = "SA-FF" },
            new Employee { Id = known, StoreId = _ids["G001"], Name = "4-Lee, Sam", Role = Roles.SA, PositionCode = "SA-FF", UkgId = "111" });
        var text = string.Join("\n",
            ContactsH,
            Person("4-Smith, Jo", "301"),
            Person("4-Smith, Jo", "302"),
            Person("4-Lee, Sam", "222"), // named like "known", but a different person...
            Person("4-Lee, Sam", "111")); // ...because "known" is this one, by id.
        var res = await _ingest.CommitUploadAsync(FeedTypes.Contacts, text, "contacts.csv", null, false);
        Assert.Equal((true, 2, 2), (res.Committed, res.CreatedRows, res.UpdatedRows));
        var employees = Table(db => db.Employees);
        Assert.Equal("301", employees.Single(e => e.Id == typed).UkgId);
        Assert.Equal("111", employees.Single(e => e.Id == known).UkgId);
        Assert.Equal(["111", "222", "301", "302"], employees.Select(e => e.UkgId!).Order(StringComparer.Ordinal));
    }

    /// <summary>The store with a refusal injected before a chosen statement ("table.op").</summary>
    private sealed class RefusingStore(IFeedStore inner) : IFeedStore
    {
        public Func<string, bool>? FailWhen { get; set; }

        /// <summary>Refuse the nth statement of <paramref name="op"/>, once.</summary>
        public void RefuseNth(string op, int n)
        {
            var seen = 0;
            FailWhen = o => o == op && ++seen == n;
        }

        private void Check(string op)
        {
            if (FailWhen?.Invoke(op) == true) throw new FeedWriteException(Timeout);
        }

        public Task<IReadOnlyDictionary<string, Guid>> StoreCodeMapAsync(CancellationToken ct = default) => inner.StoreCodeMapAsync(ct);

        public Task<IReadOnlyDictionary<Guid, IReadOnlySet<DateOnly>>> HolidaysForStoresAsync(IReadOnlyCollection<Guid> storeIds, CancellationToken ct = default) =>
            inner.HolidaysForStoresAsync(storeIds, ct);

        public Task<Guid> ResolvePeriodAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
        {
            Check("roster_periods.insert");
            return inner.ResolvePeriodAsync(start, end, ct);
        }

        public Task<Guid> InsertBatchAsync(NewUploadBatch batch, CancellationToken ct = default)
        {
            Check("upload_batches.insert");
            return inner.InsertBatchAsync(batch, ct);
        }

        public Task UpdateBatchAsync(Guid batchId, int acceptedRows, int? rejectedRows, IReadOnlyList<RowError>? errorReport, CancellationToken ct = default)
        {
            Check("upload_batches.update");
            return inner.UpdateBatchAsync(batchId, acceptedRows, rejectedRows, errorReport, ct);
        }

        public Task UpsertBenchmarkDaysAsync(IReadOnlyList<BenchmarkDay> records, CancellationToken ct = default)
        {
            Check("benchmark_days.upsert");
            return inner.UpsertBenchmarkDaysAsync(records, ct);
        }

        public Task UpsertActualsDaysAsync(IReadOnlyList<ActualsDay> records, CancellationToken ct = default)
        {
            Check("actuals_days.upsert");
            return inner.UpsertActualsDaysAsync(records, ct);
        }

        public Task UpsertPosIntervalStatsAsync(IReadOnlyList<PosIntervalStat> chunk, CancellationToken ct = default)
        {
            Check("pos_interval_stats.upsert");
            return inner.UpsertPosIntervalStatsAsync(chunk, ct);
        }

        public Task UpsertLeaveLiabilityAsync(IReadOnlyList<LeaveLiabilitySnapshot> chunk, CancellationToken ct = default)
        {
            Check("leave_liability_snapshots.upsert");
            return inner.UpsertLeaveLiabilityAsync(chunk, ct);
        }

        public Task UpsertLeaveRequestsAsync(IReadOnlyList<LeaveRequestUkg> chunk, CancellationToken ct = default)
        {
            Check("leave_requests_ukg.upsert");
            return inner.UpsertLeaveRequestsAsync(chunk, ct);
        }

        public Task DeleteStaleAsync(StaleTable table, IReadOnlyList<Guid> storeIds, Guid batchId, CancellationToken ct = default)
        {
            Check(table == StaleTable.PosIntervalStats ? "pos_interval_stats.delete" : "leave_requests_ukg.delete");
            return inner.DeleteStaleAsync(table, storeIds, batchId, ct);
        }

        public Task<IReadOnlyList<ExistingEmployee>> EmployeesOfStoresAsync(IReadOnlyCollection<Guid> storeIds, CancellationToken ct = default) =>
            inner.EmployeesOfStoresAsync(storeIds, ct);

        public Task UpsertEmployeesAsync(IReadOnlyList<EmployeeImport> chunk, bool keepPosition, CancellationToken ct = default)
        {
            Check("employees.upsert");
            return inner.UpsertEmployeesAsync(chunk, keepPosition, ct);
        }

        public Task InsertEmployeesAsync(IReadOnlyList<EmployeeImport> chunk, CancellationToken ct = default)
        {
            Check("employees.insert");
            return inner.InsertEmployeesAsync(chunk, ct);
        }
    }
}
