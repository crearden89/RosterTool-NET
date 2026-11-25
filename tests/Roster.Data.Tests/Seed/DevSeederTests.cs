// New: the development seeder fills every area of the app with invented data, once, and
// the reference rows the Postgres migrations inserted are there afterwards.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Roster.Data.Entities;
using Roster.Data.Jobs;
using Roster.Data.Scope;
using Roster.Data.Seed;
using Roster.Data.Tests.Infrastructure;
using Roster.Data.Tests.Jobs;
using Roster.Data.Tests.Support;
using Roster.Data.Users;

namespace Roster.Data.Tests.Seed;

/// <summary>The checks both databases share.</summary>
internal static class SeedChecks
{
    public static readonly DateOnly Today = new(2026, 11, 25); // a Wednesday: current period 23/11-06/12

    public static DevSeeder Seeder(IRosterDbContextFactory contexts, UserDirectoryOptions? users = null) =>
        new(contexts, new FixedClock(new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero)), Options.Create(users ?? new UserDirectoryOptions()));

    public static async Task AssertSeededAsync(RosterDbContext db, DevSeedReport report)
    {
        Assert.True(report.Seeded);
        Assert.Equal(Today, report.Today);
        Assert.Equal(SeedCatalogue.Stores.Select(s => s.Code).Order(StringComparer.Ordinal), await db.Stores.Select(s => s.Code).OrderBy(c => c).ToListAsync());
        Assert.Equal(4, (await db.Stores.Select(s => s.State).Distinct().ToListAsync()).Count);
        Assert.Equal(7, await db.AppUsers.CountAsync());
        Assert.All(await db.Stores.ToListAsync(), s => Assert.Matches("^[0-9a-f]{32}$", s.KioskToken));

        // Every page has something: teams, rules, pay, demand, rosters, kiosk, leave.
        Assert.Equal(6 * 19, await db.Employees.CountAsync());
        Assert.Equal(6, await db.BreakRules.CountAsync());
        Assert.Equal(30 * 24 * 4, await db.PayRates.CountAsync());
        Assert.Equal(6 * 42, await db.BenchmarkDays.CountAsync());
        Assert.True(await db.ActualsDays.AnyAsync(a => a.Date < Today));
        Assert.False(await db.ActualsDays.AnyAsync(a => a.Date >= Today));
        Assert.True(await db.PosIntervalStats.CountAsync() > 1000);
        Assert.True(await db.GeneratedShifts.CountAsync() > 300);
        Assert.True(await db.ShiftTaskBlocks.CountAsync() > 5000);
        Assert.True(await db.GeneratedShifts.AnyAsync(s => s.Date == Today), "today has a published roster (Floor Display)");
        Assert.True(await db.DailyLoads.AnyAsync(l => l.Date == Today));
        Assert.True(await db.LeaveLiabilitySnapshots.CountAsync() > 50);
        Assert.Equal(4, await db.EmployeeLeave.CountAsync(l => l.Employee!.Store!.Code == "G001"));

        // The migrations' reference rows.
        Assert.Equal(31, await db.Positions.CountAsync());
        Assert.Equal(8, await db.LoadTypes.CountAsync());
        Assert.Equal(57, await db.NonOpSubcategories.CountAsync());
        Assert.Equal(24, await db.AdminTasks.CountAsync());
        Assert.Equal(4, await db.ResetTasks.CountAsync());
        Assert.Equal(1, await db.PlanPlacementSettings.CountAsync());
        Assert.Equal(4.5, (await db.RosteringRules.SingleAsync(r => r.Key == "paidBreakFactorPct")).Value);
        var ambient = await db.LoadTypes.SingleAsync(l => l.Name == "Ambient");
        Assert.Equal((20.9, 23.8, 26.6), (ambient.MinutesPerPalletLt90!.Value, ambient.MinutesPerPalletLt60!.Value, ambient.MinutesPerPalletLt30!.Value));

        // The EMSO check ran on the seeded day: the planning fortnight (07/12-20/12) is not
        // approved anywhere in either EMSO's area.
        var emso1 = await db.AppUsers.SingleAsync(u => u.Username == "emso1");
        var alert = await db.Notifications.SingleAsync(n => n.UserId == emso1.Id && n.ReadAt == null);
        Assert.Equal($"2 store(s) without an approved roster for 07/12 – 20/12: G001, G102", alert.Body);
        Assert.Equal(EmsoAlert.Kind, alert.Kind);

        Assert.Contains(report.Logins, l => l.Username == "superuser");
        Assert.Equal(6, report.KioskLinks.Count);
        Assert.Equal(await db.Employees.CountAsync(), report.RowCounts["employees"]);
    }
}

public sealed class DevSeederTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Seeds_every_area_once_and_then_leaves_the_database_alone()
    {
        var contexts = new TestContexts(_db, AccessContext.Anonymous);
        var report = await SeedChecks.Seeder(contexts).SeedAsync(new DevSeedOptions { Today = SeedChecks.Today });
        await using (var db = _db.System()) await SeedChecks.AssertSeededAsync(db, report);

        var again = await SeedChecks.Seeder(contexts).SeedAsync(new DevSeedOptions { Today = SeedChecks.Today });
        Assert.False(again.Seeded);
        Assert.Contains("already has 6 store(s)", again.Message);
        await using (var db = _db.System()) Assert.Equal(6 * 19, await db.Employees.CountAsync());
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task The_same_seed_draws_the_same_people()
    {
        static async Task<List<string>> Names(TestDatabase db)
        {
            await SeedChecks.Seeder(new TestContexts(db, AccessContext.Anonymous)).SeedAsync(new DevSeedOptions { Today = SeedChecks.Today });
            await using var ctx = db.System();
            return await ctx.Employees.OrderBy(e => e.UkgId).Select(e => e.Name).ToListAsync();
        }
        using var other = new TestDatabase();
        var first = await Names(_db);
        Assert.Equal(first, await Names(other));
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.Contains(first, n => n.StartsWith("1-", StringComparison.Ordinal));
        Assert.Contains(first, n => !n.Contains(','));  // one typed in by hand, without a rank prefix
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task The_configured_first_super_user_is_seeded_too()
    {
        var users = new UserDirectoryOptions { FirstSuperUser = new FirstSuperUserOptions { UserPrincipalName = "Admin.Person@Example.com", DisplayName = "Admin Person" } };
        var report = await SeedChecks.Seeder(new TestContexts(_db, AccessContext.Anonymous), users).SeedAsync(new DevSeedOptions { Today = SeedChecks.Today });
        await using var db = _db.System();
        var row = await db.AppUsers.SingleAsync(u => u.UserPrincipalName == "admin.person@example.com");
        Assert.Equal(Tier.SuperUser, row.Tier);
        Assert.Contains(report.Logins, l => l.Username == "admin.person@example.com");
    }
}

public sealed class ReferenceDataTests
{
    [Fact]
    public void The_positions_are_migration_0020s_catalogue()
    {
        var positions = ReferenceData.Positions();
        Assert.Equal(31, positions.Count);
        Assert.Equal(30, positions.Count(p => p.HasRate));
        Assert.Equal(["RSM"], positions.Where(p => !p.HasRate).Select(p => p.Code));
        var asm = positions.Single(p => p.Code == "ASM-35-2");
        Assert.Equal((35.0, 2, 132), (asm.WeeklyHours!.Value, asm.YearBand!.Value, asm.SortOrder));
        Assert.Equal(227, positions.Single(p => p.Code == "SM-45-7").SortOrder); // 200 + 2 * 10 + 7
    }

    [Fact]
    public void Catalogue_sizes_match_the_migrations()
    {
        Assert.Equal(57, ReferenceData.NonOpSubcategories.Count);
        Assert.Equal(24, ReferenceData.AdminTasks().Count);
        Assert.Equal(15, ReferenceData.LeaveAccrualBands().Count);
        var cashUp = ReferenceData.AdminTasks().Single(t => t.Name == "Register Cash-Up");
        Assert.Equal(("close", -30, "none"), (cashUp.WindowStartKind, cashUp.WindowStartOffsetMin, cashUp.WindowEndKind));
        Assert.Equal(-150, ReferenceData.ResetTasks().Single(t => t.Name == "Full decard").WindowStartOffsetMin);
    }

    [Fact]
    public void The_seed_random_numbers_are_mulberry32s()
    {
        // mulberry32(1) in JavaScript: 0.6270739405881613, 0.002735721180215478, 0.5274470399599522.
        var rnd = new SeedRandom(1);
        Assert.Equal(0.6270739405881613, rnd.Next());
        Assert.Equal(0.002735721180215478, rnd.Next());
        Assert.Equal(0.5274470399599522, rnd.Next());
    }
}

[Trait("Category", "SqlServer")]
public sealed class DevSeederSqlServerTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => db.ResetAsync();

    [SqlServerFact]
    public async Task Seeds_a_migrated_sql_server_database()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = await SeedChecks.Seeder(db.Factory()).SeedAsync(new DevSeedOptions { Today = SeedChecks.Today });
        await using var ctx = db.System();
        await SeedChecks.AssertSeededAsync(ctx, report);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"seeding took {watch.Elapsed}");
    }
}
