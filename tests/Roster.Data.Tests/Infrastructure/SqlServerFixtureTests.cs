// New: the SQL Server fixture itself: a migrated database of its own, a reset that empties
// what tests wrote (children before parents) and keeps what the migrations put there, and
// the database gone afterwards.
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Tests.Infrastructure;

[Trait("Category", "SqlServer")]
public sealed class SqlServerFixtureTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static int Scalar(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var cmd = new SqlCommand(sql, connection);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [SqlServerFact]
    public void The_database_is_this_classs_own_and_fully_migrated()
    {
        Assert.Matches($"^Roster_{SqlServerTestSettings.TestId}_[0-9a-f]{{8}}$", db.DatabaseName);
        Assert.Equal(db.DatabaseName, new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog);
        using var ctx = db.System();
        Assert.Empty(ctx.Database.GetPendingMigrations());
        Assert.Equal(70, Scalar(db.ConnectionString,
            "SELECT COUNT(*) FROM sys.tables t WHERE SCHEMA_NAME(t.schema_id) = 'dbo' AND t.name <> '__ef_migrations_history'"));
    }

    [SqlServerFact]
    public async Task A_reset_removes_everything_the_tests_wrote_across_foreign_keys()
    {
        await using (var ctx = db.System())
        {
            var dg = new DirectorGroup { Name = "Example Group" };
            ctx.DirectorGroups.Add(dg);
            await ctx.SaveChangesAsync();
            var region = new Region { Name = "901 - Example", DirectorGroupId = dg.Id };
            ctx.Regions.Add(region);
            await ctx.SaveChangesAsync();
            var store = new Store { Code = "G801", Name = "Example", Slug = "g801", RegionId = region.Id, KioskToken = "k801" };
            ctx.Stores.Add(store);
            await ctx.SaveChangesAsync();
            var e = new Employee { StoreId = store.Id, Name = "4-Example, Person", Role = Roles.Sa, ContractHours = 60 };
            ctx.Employees.Add(e);
            await ctx.SaveChangesAsync();
            ctx.GeneratedShifts.Add(new GeneratedShift
            {
                StoreId = store.Id, Date = new DateOnly(2026, 12, 7), EmployeeId = e.Id, EmployeeName = e.Name, Role = Roles.Sa,
                StartTime = new TimeOnly(6, 0), EndTime = new TimeOnly(12, 0),
                Blocks = [new ShiftTaskBlock { BlockTime = new TimeOnly(6, 0), LabourGroup = "load_running" }],
            });
            var user = new AppUser { Username = "store@example.com", Tier = Tier.Store, StoreId = store.Id };
            ctx.AppUsers.Add(user);
            await ctx.SaveChangesAsync();
            ctx.Notifications.Add(new Notification { UserId = user.Id, Kind = "test", Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        await db.ResetAsync();

        await using var after = db.System();
        Assert.Equal(0, await after.Stores.CountAsync());
        Assert.Equal(0, await after.GeneratedShifts.CountAsync());
        Assert.Equal(0, await after.ShiftTaskBlocks.CountAsync());
        Assert.Equal(0, await after.AppUsers.CountAsync());
        Assert.Equal(0, await after.Notifications.CountAsync());
        Assert.Equal(0, await after.DirectorGroups.CountAsync());
    }

    [SqlServerFact]
    public async Task A_reset_puts_back_the_rows_the_migrations_left()
    {
        // Whatever the migrations inserted (the reference catalogues) is checkpointed and
        // restored, also after a test changed or deleted it.
        var before = db.ReferenceTables.ToDictionary(t => t, t => Scalar(db.ConnectionString, $"SELECT COUNT(*) FROM {t}"));
        foreach (var table in db.ReferenceTables)
            Scalar(db.ConnectionString, $"DELETE FROM {table}; SELECT 0");

        await db.ResetAsync();

        foreach (var (table, count) in before)
            Assert.Equal(count, Scalar(db.ConnectionString, $"SELECT COUNT(*) FROM {table}"));
    }

    [SqlServerFact]
    public async Task Resets_are_quick_enough_to_run_before_every_test()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await db.ResetAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"five resets took {watch.Elapsed}");
    }

    [SqlServerFact]
    public async Task The_database_is_dropped_when_the_class_is_done()
    {
        var fixture = new SqlServerDatabaseFixture();
        await fixture.InitializeAsync();
        await using (var ctx = fixture.System()) Assert.Equal(0, await ctx.Stores.CountAsync());
        Assert.Equal(1, Exists(fixture.DatabaseName));

        await fixture.DisposeAsync();

        Assert.Equal(0, Exists(fixture.DatabaseName));
    }

    private static int Exists(string name) =>
        Scalar(SqlServerTestSettings.ConnectionStringFor(SqlServerTestSettings.RequireServer(), null),
            $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{name}'");
}

/// <summary>The reset's delete order, worked out from the foreign keys (no server needed).</summary>
public sealed class DatabaseResetOrderTests
{
    [Fact]
    public void Delete_order_puts_children_before_parents_and_switches_off_only_cycles()
    {
        var (order, cyclic) = DatabaseReset.Order(
            ["[dbo].[a]", "[dbo].[b]", "[dbo].[c]", "[dbo].[x]", "[dbo].[y]"],
            [
                ("fk_b_a", "[dbo].[b]", "[dbo].[a]"), // b references a
                ("fk_c_b", "[dbo].[c]", "[dbo].[b]"), // c references b
                ("fk_c_c", "[dbo].[c]", "[dbo].[c]"), // self-reference: one DELETE clears it
                ("fk_x_y", "[dbo].[x]", "[dbo].[y]"), // x and y reference each other
                ("fk_y_x", "[dbo].[y]", "[dbo].[x]"),
            ]);
        Assert.Equal(["[dbo].[c]", "[dbo].[b]", "[dbo].[a]", "[dbo].[x]", "[dbo].[y]"], order);
        Assert.Equal([("fk_x_y", "[dbo].[x]"), ("fk_y_x", "[dbo].[y]")], cyclic);
    }
}
