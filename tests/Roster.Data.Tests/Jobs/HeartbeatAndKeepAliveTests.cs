// New: the cron_runs heartbeat (src/lib/cron/heartbeat.ts) and the keep-alive's read, on the
// SQLite test database.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Jobs;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Jobs;

public sealed class HeartbeatAndKeepAliveTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 25, 17, 0, 0, TimeSpan.Zero);
    private readonly TestDatabase _db = new(new FixedClock(At));

    public void Dispose() => _db.Dispose();

    private ICronHeartbeat Heartbeat(IDatabaseFunctions? functions = null)
    {
        var contexts = new TestContexts(_db, AccessContext.Anonymous);
        return new CronHeartbeat(functions ?? new DatabaseFunctionsService(contexts, new StoreScope(contexts, contexts), _db.Clock));
    }

    private CronRun? Run(string job)
    {
        using var sys = _db.System();
        return sys.CronRuns.AsNoTracking().SingleOrDefault(r => r.Job == job);
    }

    [Fact]
    public async Task The_first_run_inserts_the_row_and_later_runs_count_up()
    {
        var beat = Heartbeat();
        Assert.True(await beat.RecordCronRunAsync("emso-check", new EmsoCheckResult(1, 2, 0)));
        var first = Run("emso-check")!;
        Assert.Equal(1, first.RunCount);
        Assert.Equal(At, first.LastRunAt);
        Assert.Equal("""{"created":1,"updated":2,"failed":0}""", first.LastResult);

        Assert.True(await beat.RecordCronRunAsync("emso-check", new EmsoCheckResult(0, 0, 0)));
        var second = Run("emso-check")!;
        Assert.Equal(2, second.RunCount);
        Assert.Equal("""{"created":0,"updated":0,"failed":0}""", second.LastResult);
    }

    [Fact]
    public async Task A_run_without_a_result_stores_null()
    {
        Assert.True(await Heartbeat().RecordCronRunAsync("keep-alive"));
        Assert.Null(Run("keep-alive")!.LastResult);
    }

    [Fact]
    public async Task A_heartbeat_that_cannot_be_written_reports_false_and_never_throws()
    {
        Assert.False(await Heartbeat(new BrokenFunctions()).RecordCronRunAsync("keep-alive", new { stores = 3 }));
    }

    [Fact]
    public async Task The_keep_alive_counts_every_store_as_the_server()
    {
        using (var sys = _db.System())
        {
            var dg = new DirectorGroup { Name = "Example Group" };
            sys.DirectorGroups.Add(dg);
            sys.SaveChanges();
            var region = new Region { Name = "901 - Example", DirectorGroupId = dg.Id };
            sys.Regions.Add(region);
            sys.SaveChanges();
            for (var i = 1; i <= 3; i++)
                sys.Stores.Add(new Store { Code = $"G80{i}", Name = $"Example {i}", Slug = $"g80{i}", RegionId = region.Id, KioskToken = $"k{i}" });
            sys.SaveChanges();
        }
        // The current identity (anonymous here) does not matter: the keep-alive reads as the server.
        var keepAlive = new KeepAlive(new TestContexts(_db, AccessContext.Anonymous));
        Assert.Equal(KeepAliveResult.Success(3), await keepAlive.TouchAsync());
    }

    [Fact]
    public async Task A_keep_alive_that_cannot_read_returns_the_error()
    {
        var keepAlive = new KeepAlive(new BrokenContexts());
        var result = await keepAlive.TouchAsync();
        Assert.False(result.Ok);
        Assert.Equal("database unavailable", result.Error);
    }

    private sealed class BrokenContexts : IRosterDbContextFactory
    {
        public RosterDbContext Create() => throw new InvalidOperationException("database unavailable");
        public RosterDbContext Create(AccessContext access) => throw new InvalidOperationException("database unavailable");
        public RosterDbContext CreateSystem(string reason) => throw new InvalidOperationException("database unavailable");
    }

    private sealed class BrokenFunctions : IDatabaseFunctions
    {
        public Task ApproveRosterDayAsync(Guid storeId, DateOnly date, IReadOnlyList<RosterDayShift>? shifts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OverviewRollupRow>> OverviewRollupAsync(IReadOnlyList<Guid> storeIds, DateOnly from, DateOnly to, bool asPeer = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DateOnly>> LeavePeriodEndsAsync(Guid? storeId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsPublicHolidayAsync(Guid storeId, DateOnly date, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BumpCronRunAsync(string job, System.Text.Json.JsonElement? result = null, CancellationToken ct = default) => throw new InvalidOperationException("no database");
    }
}
