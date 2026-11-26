// New: the ReferenceData migration puts the Postgres migrations' reference rows into a
// database built from the migrations alone, and holds exactly the rows Seed/ReferenceData.cs
// ports (the seeder's copy for databases built without migrations).
//
// The unit-level SQLite database (Support/TestDatabase) is created from the model, not the
// migrations (the migrations are SQL Server T-SQL), so the no-database test below reads the
// migration's operations instead, and the SQL Server test checks a migrated database.
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Roster.Data.Entities;
using Roster.Data.Seed;
using Roster.Data.Tests.Infrastructure;

namespace Roster.Data.Tests.Seed;

public sealed partial class ReferenceDataMigrationTests
{
    private static readonly IReadOnlyList<MigrationOperation> Up = new Roster.Data.Migrations.ReferenceData().UpOperations;

    /// <summary>The rows one InsertData call writes, each as column -> value.</summary>
    private static List<Dictionary<string, object?>> Rows(string table)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var op in Up.OfType<InsertDataOperation>().Where(o => o.Table == table))
            for (var r = 0; r < op.Values.GetLength(0); r++)
                rows.Add(op.Columns.Select((c, i) => (c, op.Values[r, i])).ToDictionary(x => x.c, x => x.Item2));
        return rows;
    }

    private static double? D(object? v) => v is null ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    [Fact]
    public void The_migration_inserts_the_seeders_positions()
    {
        var rows = Rows("positions");
        var expected = ReferenceData.Positions();
        Assert.Equal(expected.Count, rows.Count);
        foreach (var (p, row) in expected.Zip(rows))
        {
            Assert.Equal(p.Code, row["code"]);
            Assert.Equal(p.Label, row["label"]);
            Assert.Equal(p.BaseRole, row["base_role"]);
            Assert.Equal(p.WeeklyHours, D(row["weekly_hours"]));
            Assert.Equal(p.YearBand, (int?)row["year_band"]);
            Assert.Equal(p.HasRate, row["has_rate"]);
            Assert.Equal(p.SortOrder, row["sort_order"]);
        }
    }

    [Fact]
    public void The_migration_inserts_the_seeders_load_types_admin_and_reset_tasks()
    {
        var loads = Rows("load_types");
        Assert.Equal(ReferenceData.LoadTypes().Select(l => (l.Name, l.MinutesPerPallet, l.Color, l.SortOrder, l.MinutesPerPalletLt90, l.MinutesPerPalletLt60, l.MinutesPerPalletLt30)),
            loads.Select(r => ((string)r["name"]!, D(r["minutes_per_pallet"])!.Value, (string)r["color"]!, (int)r["sort_order"]!, D(r["minutes_per_pallet_lt90"]), D(r["minutes_per_pallet_lt60"]), D(r["minutes_per_pallet_lt30"]))));

        var tasks = Rows("admin_tasks");
        Assert.Equal(ReferenceData.AdminTasks().Select(t => (t.Name, t.ScheduleType, t.DayOfWeek, t.NthWeek, t.RegionDays, t.DurationType, t.FixedMinutes, t.MinutesPerUnit, t.MinMinutes,
                t.WindowStartKind, t.WindowStartTime, t.WindowStartOffsetMin, t.WindowEndKind, t.WindowEndTime, t.WindowEndOffsetMin, t.SortOrder)),
            tasks.Select(r => ((string)r["name"]!, (string)r["schedule_type"]!, (int?)r["day_of_week"], (int?)r["nth_week"], (string?)r["region_days"], (string)r["duration_type"]!,
                D(r["fixed_minutes"]), D(r["minutes_per_unit"]), D(r["min_minutes"]), (string)r["window_start_kind"]!, (TimeOnly?)r["window_start_time"], (int)r["window_start_offset_min"]!,
                (string)r["window_end_kind"]!, (TimeOnly?)r["window_end_time"], (int)r["window_end_offset_min"]!, (int)r["sort_order"]!)));

        var resets = Rows("reset_tasks");
        Assert.Equal(ReferenceData.ResetTasks().Select(t => (t.Name, t.SharePct, t.WindowStartKind, t.WindowStartTime, t.WindowStartOffsetMin, t.WindowEndKind, t.WindowEndTime, t.WindowEndOffsetMin, t.SortOrder)),
            resets.Select(r => ((string)r["name"]!, D(r["share_pct"]), (string)r["window_start_kind"]!, (TimeOnly?)r["window_start_time"], (int)r["window_start_offset_min"]!,
                (string)r["window_end_kind"]!, (TimeOnly?)r["window_end_time"], (int)r["window_end_offset_min"]!, (int)r["sort_order"]!)));
    }

    [Fact]
    public void The_migration_inserts_the_seeders_catalogues_and_settings()
    {
        Assert.Equal(ReferenceData.NonOpCategories.Select(c => (c.Name, c.Sort)),
            Rows("non_op_categories").Select(r => ((string)r["name"]!, (int)r["sort"]!)));

        // The sub-categories are one INSERT ... SELECT joined to their category by name.
        var sql = Up.OfType<SqlOperation>().Single(o => o.Sql.Contains("[non_op_subcategories]", StringComparison.Ordinal)).Sql;
        var subs = SubcategoryRow().Matches(sql).Select(m => (
            m.Groups["cat"].Value, m.Groups["name"].Value.Replace("''", "'"),
            m.Groups["def"].Value == "NULL" ? (int?)null : int.Parse(m.Groups["def"].Value, CultureInfo.InvariantCulture),
            m.Groups["max"].Value == "NULL" ? (int?)null : int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture),
            m.Groups["note"].Value == "NULL" ? null : m.Groups["note"].Value[2..^1].Replace("''", "'"))).ToList();
        Assert.Equal(ReferenceData.NonOpSubcategories.Select(s => (s.Category, s.Name, s.Default, s.Max, s.Note)), subs);

        Assert.Equal(ReferenceData.LeaveAccrualBands().Select(b => (b.ContractHours, b.RoleClass, b.LeaveNotionalHours, b.AccrualWeeks, b.AnnualAccrualHours)),
            Rows("leave_accrual_bands").Select(r => (D(r["contract_hours"])!.Value, (string)r["role_class"]!, D(r["leave_notional_hours"])!.Value, D(r["accrual_weeks"])!.Value, D(r["annual_accrual_hours"])!.Value)));
        Assert.Equal(ReferenceData.ProbationBuffers().Select(b => (b.LabourGroup, b.MaxDays, b.UpliftPct)),
            Rows("probation_buffers").Select(r => ((string)r["labour_group"]!, (int)r["max_days"]!, D(r["uplift_pct"])!.Value)));
        var rule = Assert.Single(Rows("rostering_rules"));
        Assert.Equal((ReferenceData.PaidBreakFactor().Key, ReferenceData.PaidBreakFactor().Value, ReferenceData.PaidBreakFactor().Note), ((string)rule["key"]!, D(rule["value"])!.Value, (string?)rule["note"]));
        foreach (var table in new[] { "plan_placement_settings", "variance_settings", "leave_settings" })
            Assert.Equal(true, Assert.Single(Rows(table))["id"]);
    }

    [GeneratedRegex(@"\(N'(?<cat>[^']+)', N'(?<name>(?:[^']|'')+)', (?<def>NULL|\d+), (?<max>NULL|\d+), (?<note>NULL|N'(?:[^']|'')*')\)")]
    private static partial Regex SubcategoryRow();
}

[Trait("Category", "SqlServer")]
public sealed class ReferenceDataSqlServerTests(SqlServerDatabaseFixture db) : IClassFixture<SqlServerDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => db.ResetAsync();

    [SqlServerFact]
    public async Task A_database_built_by_the_migrations_alone_has_every_reference_row()
    {
        await using var ctx = db.System();
        Assert.Equal(ReferenceData.Positions().Select(p => p.Code).Order(StringComparer.Ordinal),
            (await ctx.Positions.Select(p => p.Code).ToListAsync()).Order(StringComparer.Ordinal));
        Assert.All(await ctx.Positions.ToListAsync(), p => Assert.True(p.Active));
        var loads = await ctx.LoadTypes.OrderBy(l => l.SortOrder).ToListAsync();
        Assert.Equal(ReferenceData.LoadTypes().Select(l => (l.Name, l.MinutesPerPalletLt90, l.MinutesPerPalletLt60, l.MinutesPerPalletLt30)),
            loads.Select(l => (l.Name, l.MinutesPerPalletLt90, l.MinutesPerPalletLt60, l.MinutesPerPalletLt30)));
        Assert.All(loads, l => Assert.Equal((100, (string?)null), (l.Priority, l.SubTask)));

        Assert.Equal(5, await ctx.NonOpCategories.CountAsync());
        var subs = await ctx.NonOpSubcategories.Include(s => s.Category).ToListAsync();
        Assert.Equal(57, subs.Count);
        var drill = subs.Single(s => s.Name == "Fire Drills (per person)");
        Assert.Equal(("Miscellaneous", (int?)15, (int?)15, (int?)null), (drill.Category!.Name, drill.DefaultMinutes, drill.MaxMinutes, drill.MinMinutes));
        Assert.All(subs, s => Assert.Equal([0, 1, 2, 3, 4, 5, 6], s.DaysAllowed));

        var tasks = await ctx.AdminTasks.ToListAsync();
        Assert.Equal(24, tasks.Count);
        Assert.All(tasks, t => Assert.True(t.Active));
        var fresh = tasks.Single(t => t.Name == "Fresh Ordering");
        Assert.Equal(("close", -210, "close", 0), (fresh.WindowStartKind, fresh.WindowStartOffsetMin, fresh.WindowEndKind, fresh.WindowEndOffsetMin));
        Assert.Equal(new TimeOnly(10, 0), tasks.Single(t => t.Name == "UKG End of PayPeriod").WindowEndTime);
        var resets = await ctx.ResetTasks.OrderBy(t => t.SortOrder).ToListAsync();
        Assert.Equal(["Re-run 1", "Re-run 2", "First declare (light)", "Full decard"], resets.Select(t => t.Name));
        Assert.All(resets, t => Assert.Equal("spread", t.Placement));

        var placement = await ctx.PlanPlacementSettings.SingleAsync();
        Assert.Equal((false, "hourly", 3, 85.0, new TimeOnly(17, 0), 25.0), (placement.Enabled, placement.PeakBasis, placement.PeakSmoothingBlocks, placement.PeakThresholdPct, placement.NightTraderFrom, placement.NightTraderSharePct));
        Assert.Equal(["admin", "load_running", "reset", "specials", "cleaning"], placement.StepOrder);
        var variance = await ctx.VarianceSettings.SingleAsync();
        Assert.Equal((15.0, 70.0, 20.0), (variance.DataLightPct, variance.MetricPct, variance.HoursPct));
        var leave = await ctx.LeaveSettings.SingleAsync();
        Assert.Equal((76.0, 76.0), (leave.TargetHoursPerFte, leave.FteDivisor));
        Assert.Equal(15, await ctx.LeaveAccrualBands.CountAsync());
        Assert.Equal(3, await ctx.ProbationBuffers.CountAsync());
        Assert.Equal(4.5, (await ctx.RosteringRules.SingleAsync(r => r.Key == "paidBreakFactorPct")).Value);
        Assert.Contains("[positions]", db.ReferenceTables.Single(t => t.Contains("[positions]")));
    }

    [SqlServerFact]
    public async Task The_seeders_top_up_leaves_a_migrated_database_alone()
    {
        await using var ctx = db.System();
        Assert.Empty(await ReferenceData.EnsureAsync(ctx));
        Assert.Equal(31, await ctx.Positions.CountAsync());
        Assert.Equal(57, await ctx.NonOpSubcategories.CountAsync());
        Assert.Equal(1, await ctx.RosteringRules.CountAsync());
    }
}
