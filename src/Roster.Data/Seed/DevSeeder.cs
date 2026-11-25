// New: the development seeder. The TypeScript app's scripts/seed.mjs made one director
// group, region and store, four logins with a password, and a few calendar rows; this
// seeder makes that organisation and enough invented data around it for every page to
// show something real. Everything here is invented: store numbers, names and people.
// There are no passwords: the development sign-in picks one of the seeded users.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Roster.Data.Configurations;
using Roster.Data.Jobs;
using Roster.Data.Users;
using Roster.Engine.Core;

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

internal sealed class DevSeeder(
    IRosterDbContextFactory contexts,
    TimeProvider clock,
    IOptions<UserDirectoryOptions> userOptions) : IDevSeeder
{
    public async Task<DevSeedReport> SeedAsync(DevSeedOptions options, CancellationToken cancellationToken = default)
    {
        var today = options.Today ?? Dates.TodayAu(clock.GetUtcNow());
        await using var db = contexts.CreateSystem("development seed");

        if (options.Reset) await db.Database.EnsureDeletedAsync(cancellationToken);
        if (options.Migrate || options.Reset)
        {
            if (db.Provider == DatabaseProvider.SqlServer) await db.Database.MigrateAsync(cancellationToken);
            else await db.Database.EnsureCreatedAsync(cancellationToken); // SQLite: the unit tests
        }

        var existing = await db.Stores.CountAsync(cancellationToken);
        if (existing > 0)
            return new DevSeedReport
            {
                Seeded = false,
                Today = today,
                Message = $"The database already has {existing} store(s); nothing was seeded. Start the app with --reseed to drop it and seed it again.",
            };

        var counts = new Dictionary<string, int>(await ReferenceData.EnsureAsync(db, cancellationToken));
        var builder = new DevSeedBuilder(db, today, userOptions.Value.FirstSuperUser);
        await builder.RunAsync(cancellationToken);
        foreach (var (table, n) in builder.Counts) counts[table] = counts.GetValueOrDefault(table) + n;

        // This fortnight's EMSO alerts, raised by the real check as of the seeded day.
        var emso = await new EmsoRosterCheck(contexts, new FixedDay(today)).RunEmsoUnapprovedCheckAsync(cancellationToken);
        counts["notifications"] = counts.GetValueOrDefault("notifications") + emso.Created;

        return new DevSeedReport
        {
            Seeded = true,
            Today = today,
            RowCounts = counts,
            Logins = builder.Logins,
            KioskLinks = builder.KioskLinks,
            Message = $"Seeded {SeedCatalogue.Stores.Count} invented stores around {Dates.AuFull(today)}: "
                + $"{counts.GetValueOrDefault("employees")} team members, {counts.GetValueOrDefault("generated_shifts")} published shifts, "
                + $"{counts.Values.Sum()} rows in all; the EMSO check raised {emso.Created} alert(s).",
        };
    }

    /// <summary>6 pm in Sydney on the seeded day, for the EMSO check.</summary>
    private sealed class FixedDay(DateOnly day) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(day.ToDateTime(new TimeOnly(18, 0)), TimeSpan.FromHours(10)).ToUniversalTime();
    }
}
