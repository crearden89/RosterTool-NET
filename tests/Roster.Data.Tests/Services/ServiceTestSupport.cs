// New: the data services' test helpers - contexts over D's in-memory SQLite
// TestDatabase, and a few invented stores to read and write.

using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Services;

/// <summary>
/// An <see cref="IRosterDbContextFactory"/> over a <see cref="TestDatabase"/>, acting as <see cref="Current"/>
/// (also the access accessor, so D's StoreScope sees the same caller).
/// </summary>
internal sealed class TestContexts(TestDatabase db) : IRosterDbContextFactory, IAccessContextAccessor
{
    public AccessContext Current { get; set; } = AccessContext.System("test caller");

    public RosterDbContext Create() => db.As(Current);

    public RosterDbContext Create(AccessContext access) => db.As(access);

    public RosterDbContext CreateSystem(string reason) => db.As(AccessContext.System(reason));

    public StoreScope Scope() => new(this, this);
}

/// <summary>A fixed clock for the services' now().</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Seed
{
    private static int _nextCode = 900;

    /// <summary>A director group, a region named "501 - Example" and <paramref name="count"/> stores in it (state NSW).</summary>
    public static List<Store> Stores(TestDatabase tdb, int count = 1, string regionName = "501 - Example")
    {
        using var db = tdb.System();
        var group = new DirectorGroup { Id = Guid.NewGuid(), Name = "Group " + Guid.NewGuid().ToString("N")[..6] };
        var region = new Region { Id = Guid.NewGuid(), Name = regionName, DirectorGroupId = group.Id };
        db.DirectorGroups.Add(group);
        db.Regions.Add(region);
        var stores = Enumerable.Range(1, count).Select(i => new Store
        {
            Id = Guid.NewGuid(),
            Code = $"G{Interlocked.Increment(ref _nextCode)}",
            Name = $"Example Store {i}",
            Slug = $"example-{i}-{Guid.NewGuid():N}"[..20],
            RegionId = region.Id,
            State = AuStates.Nsw,
            KioskToken = Guid.NewGuid().ToString("N"),
        }).ToList();
        db.Stores.AddRange(stores);
        db.SaveChanges();
        return stores;
    }

    public static Employee Employee(TestDatabase tdb, Guid storeId, string name, string role = "SA")
    {
        using var db = tdb.System();
        var e = new Employee { Id = Guid.NewGuid(), StoreId = storeId, Name = name, Role = role, ContractHours = 60 };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }
}
