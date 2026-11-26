// New: the Review page's service over a private in-memory SQLite database built from the
// data layer's model, with the real review reader, notices and roster gate - the
// TypeScript test's "real server actions, review loader and roster gate, against an
// in-memory database". As there, only the signed-in user, the held plan snapshot and the
// day-by-day plan approval writes are stood in for, plus the store-write guard's refusal
// (the TypeScript test's H.deny).

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Held;
using Roster.Data.Services.Review;
using Roster.Data.Snapshots;
using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Review;
using HeldPlan = Roster.Data.Services.Held.PlanSnapshot;

namespace Roster.Web.Tests.Review;

public sealed class ReviewTestDb : IDisposable
{
    public static readonly Guid DirectorGroupId = Guid.Parse("d3000000-0000-4000-8000-000000000001");
    public static readonly Guid RegionId = Guid.Parse("e3000000-0000-4000-8000-000000000001");
    public static readonly Guid A1 = Guid.Parse("a3000000-0000-4000-8000-000000000001");
    public static readonly Guid St1 = Guid.Parse("53000000-0000-4000-8000-000000000999");

    public static readonly ScopedUser Sm = new(Guid.Parse("c3000000-0000-4000-8000-000000000001"), "sm.g999", Tier.Store, St1, null, null, null);
    public static readonly ScopedUser Emso = new(Guid.Parse("c3000000-0000-4000-8000-000000000002"), "j.morgan", Tier.Emso, null, null, A1, null);
    public static readonly ScopedUser Admin = new(Guid.Parse("c3000000-0000-4000-8000-000000000003"), "admin", Tier.SuperUser, null, null, null, null);
    public static readonly ScopedUser Dso = new(Guid.Parse("c3000000-0000-4000-8000-000000000004"), "dso.north", Tier.Director, null, null, null, null);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    /// <summary>The held plan (H.snap.plan); null when none has been generated.</summary>
    public HeldPlan? Plan { get; set; }

    /// <summary>What the store-write guard refuses with, when set (H.deny).</summary>
    public string? Deny { get; set; }

    /// <summary>The fortnights the plan approval approved, and withdrew, as dates.</summary>
    public List<IReadOnlyList<DateOnly>> ApprovedPlans { get; } = [];
    public List<IReadOnlyList<DateOnly>> WithdrawnPlans { get; } = [];

    public ReviewTestDb()
    {
        _connection.Open();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddRosterData(b => b.UseSqlite(_connection), configuration);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new TickingClock(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero))));
        services.Replace(ServiceDescriptor.Scoped<IHeldPlans>(_ => new HeldPlansStandIn(this)));
        services.Replace(ServiceDescriptor.Scoped<IPlanSnapshotStore>(_ => new SnapshotStoreStandIn(this)));
        services.Replace(ServiceDescriptor.Scoped<IPlanApproval>(_ => new PlanApprovalStandIn(this)));
        services.Replace(ServiceDescriptor.Scoped<IStoreScope>(sp => new GuardStandIn(
            new StoreScope(sp.GetRequiredService<IAccessContextAccessor>(), sp.GetRequiredService<IRosterDbContextFactory>()), this)));
        services.AddScoped<IRememberedStore, NoRememberedStore>();
        new ReviewModule().Register(services, configuration);
        new Roster.Web.Features.Notifications.NotificationsModule().Register(services, configuration);
        _root = services.BuildServiceProvider();
        _scope = _root.CreateAsyncScope();

        using (var db = System())
        {
            db.Database.EnsureCreated();
            db.DirectorGroups.Add(new DirectorGroup { Id = DirectorGroupId, Name = "Group One" });
            db.Regions.Add(new Region { Id = RegionId, Name = "501 - Test Region", DirectorGroupId = DirectorGroupId });
            db.Areas.Add(new Area { Id = A1, Name = "Area One", RegionId = RegionId });
            db.Stores.Add(new Store { Id = St1, Code = "G999", Name = "Sample Store", Slug = "g999", RegionId = RegionId, AreaId = A1, KioskToken = "k-g999" });
            foreach (var u in new[] { Sm, Emso, Admin, Dso })
                db.AppUsers.Add(new AppUser { Id = u.Id, Username = u.Username, Tier = u.Tier, StoreId = u.StoreId, AreaId = u.AreaId });
            db.SaveChanges();
        }
        As(Admin);
    }

    private IServiceProvider Services => _scope.ServiceProvider;

    public IReviewService Service => Services.GetRequiredService<IReviewService>();
    public IReviewReader Reader => Services.GetRequiredService<IReviewReader>();
    public IReviewGate Gate => Services.GetRequiredService<IReviewGate>();
    public Roster.Web.Features.Notifications.INotificationService Notifications =>
        Services.GetRequiredService<Roster.Web.Features.Notifications.INotificationService>();

    /// <summary>Signs a user in (the TypeScript test's <c>as(u)</c>).</summary>
    public void As(ScopedUser user) => Services.GetRequiredService<AccessContextAccessor>().Current = AccessContext.ForUser(user);

    public RosterDbContext System() => Services.GetRequiredService<IRosterDbContextFactory>().CreateSystem("review test");

    public void Dispose()
    {
        _scope.Dispose();
        _root.Dispose();
        _connection.Dispose();
    }

    private sealed class HeldPlansStandIn(ReviewTestDb t) : IHeldPlans
    {
        public Task<HeldPlan?> ReadPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) => Task.FromResult(t.Plan);
        public Task<HeldPlan?> ReadPeerPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) => Task.FromResult(t.Plan);
        public Task<SnapshotWriteResult> WritePlanSnapshotAsync(Guid storeId, DateOnly periodStart, IReadOnlyList<PlanSnapshotDay> days, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<PlanSnapshotDay>> ComputePlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HeldPlan> StorePlanSnapshotAsync(Guid storeId, DateOnly periodStart, IReadOnlyList<PlanSnapshotDay> days, Guid? userId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HeldPlan> ComputeAndStorePlanSnapshotAsync(Guid storeId, DateOnly periodStart, Guid? userId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // The gate reads only the held snapshots' versions and times.
    private sealed class SnapshotStoreStandIn(ReviewTestDb t) : IPlanSnapshotStore
    {
        public Task<HeldSnapshots> ReadAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HeldSnapshots(
                t.Plan is { } p ? new HeldSnapshot(storeId, periodStart, SnapshotKinds.Plan, "{}", p.GeneratedAt, p.GeneratedBy, p.Version) : null,
                null));

        public Task<HeldSnapshot?> ReadPeerPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SnapshotWriteResult> WritePlanPayloadAsync(Guid storeId, DateOnly periodStart, string payloadJson, int expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StoreAsync(Guid storeId, DateOnly periodStart, string kind, string payloadJson, DateTimeOffset generatedAt, Guid? userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PlanApprovalStandIn(ReviewTestDb t) : IPlanApproval
    {
        public Task<PlanApprovalResult> ApprovePlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default)
        {
            t.ApprovedPlans.Add([.. dates]);
            return Task.FromResult(new PlanApprovalResult(true, dates.Count));
        }

        public Task<ApprovalResult> WithdrawPlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default)
        {
            t.WithdrawnPlans.Add([.. dates]);
            return Task.FromResult(ApprovalResult.Success);
        }
    }

    // The real store scope, except that a set Deny refuses every store write.
    private sealed class GuardStandIn(IStoreScope inner, ReviewTestDb t) : IStoreScope
    {
        public AccessContext Access => inner.Access;
        public ScopedUser? User => inner.User;
        public Task<IReadOnlyList<Guid>?> ReadableStoreIdsAsync(CancellationToken cancellationToken = default) => inner.ReadableStoreIdsAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> WritableStoreIdsAsync(CancellationToken cancellationToken = default) => inner.WritableStoreIdsAsync(cancellationToken);
        public Task<bool> CanReadStoreAsync(Guid storeId, CancellationToken cancellationToken = default) => inner.CanReadStoreAsync(storeId, cancellationToken);
        public Task<bool> CanPeerReadAsync(Guid storeId, CancellationToken cancellationToken = default) => inner.CanPeerReadAsync(storeId, cancellationToken);
        public Task<bool> CanWriteStoreAsync(Guid storeId, CancellationToken cancellationToken = default) => inner.CanWriteStoreAsync(storeId, cancellationToken);
        public Task<StoreAccess?> AccessForStoreAsync(Guid storeId, CancellationToken cancellationToken = default) => inner.AccessForStoreAsync(storeId, cancellationToken);
        public string? DenyWrite() => t.Deny ?? inner.DenyWrite();
        public async Task<string?> DenyStoreWriteAsync(Guid? storeId, CancellationToken cancellationToken = default) =>
            t.Deny ?? await inner.DenyStoreWriteAsync(storeId, cancellationToken);
        public Task<string?> DenyEmployeeWriteAsync(Guid employeeId, CancellationToken cancellationToken = default) => inner.DenyEmployeeWriteAsync(employeeId, cancellationToken);
        public Task<string?> DenyEmployeesWriteAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken = default) => inner.DenyEmployeesWriteAsync(employeeIds, cancellationToken);
        public bool HasTier(params Tier[] tiers) => inner.HasTier(tiers);
        public Task<IReadOnlyList<Guid>?> NetworkStoreIdsAsync(NetworkScope scope, CancellationToken cancellationToken = default) => inner.NetworkStoreIdsAsync(scope, cancellationToken);
    }

    private sealed class NoRememberedStore : IRememberedStore
    {
        public Guid? StoreId => null;
    }
}

/// <summary>A clock that moves on a second every time it is read, so the activity log keeps its order.</summary>
internal sealed class TickingClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
}
