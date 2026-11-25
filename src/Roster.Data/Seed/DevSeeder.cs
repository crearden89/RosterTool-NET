// New: the development seeder. The TypeScript app's scripts/seed.mjs made one director
// group, region and store, four logins with a password, and a few calendar rows; this
// seeder makes that organisation and enough invented data around it for every page to
// show something real. Everything here is invented: store numbers, names and people.
// There are no passwords: the development sign-in picks one of the seeded users.
namespace Roster.Data.Seed;

/// <summary>How to seed.</summary>
public sealed record DevSeedOptions
{
    /// <summary>
    /// The day the data is built around (the store's "today"): the previous, current and
    /// planning fortnights are filled relative to it. Null = today in Sydney.
    /// </summary>
    public DateOnly? Today { get; init; }

    /// <summary>Apply the EF Core migrations before seeding (creating the database if needed).</summary>
    public bool Migrate { get; init; } = true;

    /// <summary>
    /// Drop the database and build it again before seeding. Only for a development database:
    /// everything in it is lost.
    /// </summary>
    public bool Reset { get; init; }
}

/// <summary>A seeded login: the development sign-in lists these.</summary>
public sealed record DevSeedLogin(string Username, string Tier, string Scope);

/// <summary>A seeded store's kiosk pages (Floor Display, Load Planner).</summary>
public sealed record DevSeedKioskLink(string StoreCode, string FloorPath, string LoadPath);

/// <summary>What the seeder did.</summary>
public sealed record DevSeedReport
{
    /// <summary>False when the database already held stores and nothing was written.</summary>
    public required bool Seeded { get; init; }

    /// <summary>One line for the console.</summary>
    public required string Message { get; init; }

    /// <summary>The day the data was built around.</summary>
    public DateOnly Today { get; init; }

    /// <summary>Rows written per table (snake_case table name).</summary>
    public IReadOnlyDictionary<string, int> RowCounts { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<DevSeedLogin> Logins { get; init; } = [];

    public IReadOnlyList<DevSeedKioskLink> KioskLinks { get; init; } = [];
}

/// <summary>Seeds a development database with invented data.</summary>
public interface IDevSeeder
{
    /// <summary>
    /// Seed the database. Does nothing (Seeded = false) when it already holds stores, unless
    /// <see cref="DevSeedOptions.Reset"/> rebuilds it first. Runs as the server.
    /// </summary>
    Task<DevSeedReport> SeedAsync(DevSeedOptions options, CancellationToken cancellationToken = default);
}

internal sealed class DevSeeder : IDevSeeder
{
    public Task<DevSeedReport> SeedAsync(DevSeedOptions options, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
