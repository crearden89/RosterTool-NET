// Port of how the TypeScript's whole-pipeline tests use src/test/rosterScenario.ts:
// a store-fortnight's database, built by the TypeScript harness itself (dumped to
// harness-scenarios.json.gz by scenario-dump.ts), seeded into SQL Server, with the
// trade curve and the POS morning customers handed over at the refdata boundary
// exactly as the TypeScript tests mock getTradeCurveWeek and getPosCustomerRows.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.RefData;
using Roster.Engine.Planning.Stage1;
using Entities = Roster.Data.Entities;

namespace Roster.Data.Tests.Services.Harness;

internal sealed class HarnessScenario
{
    private static readonly Lazy<JsonElement> All = new(LoadAll);

    private readonly JsonElement _s;

    private HarnessScenario(string name, JsonElement s)
    {
        Name = name;
        _s = s;
        PeriodStart = DateOnly.Parse(s.GetProperty("params").GetProperty("periodStart").GetString()!);
        StoreId = IdFor(Rows("stores")[0].GetProperty("id").GetString()!);
        CurveWeek = s.GetProperty("curveWeek").EnumerateArray().ToDictionary(
            e => e[0].GetInt32(),
            e => (IReadOnlyList<QuarterHourRow>)e[1].EnumerateArray().Select(r => new QuarterHourRow
            {
                DayOfWeek = r.GetProperty("day_of_week").GetInt32(),
                BlockTime = r.GetProperty("block_time").GetString()!,
                Allocation = r.GetProperty("allocation").GetDouble(),
            }).ToList());
        PosCustomers = s.GetProperty("posCustomers").EnumerateArray()
            .Select(r => new PosCustomerRow(DateOnly.Parse(r.GetProperty("date").GetString()!), r.GetProperty("time").GetDouble(), r.GetProperty("customers").GetDouble()))
            .ToList();
    }

    /// <summary>"g999" (the G999 31/08-13/09 fortnight, realG999Params), "gen3", "gen11" (genParams(3), genParams(11)).</summary>
    public static HarnessScenario Load(string name) => new(name, All.Value.GetProperty(name));

    public string Name { get; }
    public DateOnly PeriodStart { get; }
    public Guid StoreId { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>> CurveWeek { get; }
    public IReadOnlyList<PosCustomerRow> PosCustomers { get; }

    private static JsonElement LoadAll()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "Services", "Harness", "harness-scenarios.json.gz");
            if (!File.Exists(file)) continue;
            using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            return JsonDocument.Parse(gz).RootElement.Clone();
        }
        throw new FileNotFoundException("harness-scenarios.json.gz (tests/Roster.Data.Tests/Services/Harness)");
    }

    private List<JsonElement> Rows(string table) =>
        _s.GetProperty("tables").TryGetProperty(table, out var t) ? t.EnumerateArray().ToList() : [];

    /// <summary>The harness's ids as Guids: a uuid stays itself, any other id ("store-harness", "a1") a fixed Guid of it.</summary>
    public static Guid IdFor(string id) =>
        Guid.TryParse(id, out var g) ? g : new Guid(MD5.HashData(Encoding.UTF8.GetBytes("harness:" + id)));

    public Guid EmployeeId(string id) => IdFor(id);

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement r, string name, bool fallback = false) =>
        r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static TimeOnly? Time(JsonElement r, string name) => Str(r, name) is { } t ? TimeOnly.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static DateOnly Date(JsonElement r, string name) => DateOnly.Parse(Str(r, name)!, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The *_100 figure whose computed *_90 (x / 0.9, as SQL Server stores it) is the harness's
    /// *_90 - exactly where a double divides to it, else the nearest (about one double in ten
    /// is no double's quotient by 0.9; the tests here check properties, not figures).
    /// </summary>
    private static double Hundred(double target90)
    {
        var best = target90 * 0.9;
        var x = best;
        for (var i = 0; i < 8; i++, x = Math.BitDecrement(x)) if (Math.Abs(x / 0.9 - target90) < Math.Abs(best / 0.9 - target90)) best = x;
        x = target90 * 0.9;
        for (var i = 0; i < 8; i++, x = Math.BitIncrement(x)) if (Math.Abs(x / 0.9 - target90) < Math.Abs(best / 0.9 - target90)) best = x;
        return best;
    }

    /// <summary>
    /// Write the scenario's rows. The configuration tables hold only the scenario's rows (the
    /// harness's database had nothing else), so the migrations' reference rows go first.
    /// </summary>
    public async Task SeedAsync(RosterDbContext db, Guid userId)
    {
        await db.AdminTaskChecks.ExecuteDeleteAsync();
        await db.AdminTasks.ExecuteDeleteAsync();
        await db.ResetTasks.ExecuteDeleteAsync();
        await db.LabourGroupRules.ExecuteDeleteAsync();
        await db.LabourGroupPriority.ExecuteDeleteAsync();
        await db.BreakRules.ExecuteDeleteAsync();
        await db.RosteringRules.ExecuteDeleteAsync();
        await db.RosterOrderSteps.ExecuteDeleteAsync();
        await db.PlanPlacementRules.ExecuteDeleteAsync();
        await db.PlanPlacementSettings.ExecuteDeleteAsync();
        await db.DailyLoads.ExecuteDeleteAsync();
        await db.LoadTypes.ExecuteDeleteAsync();

        var store = Rows("stores")[0];
        var group = new DirectorGroup { Id = Guid.NewGuid(), Name = "Harness group" };
        var region = new Region { Id = Guid.NewGuid(), Name = store.GetProperty("regions").GetProperty("name").GetString()!, DirectorGroupId = group.Id };
        db.AddRange(group, region, new Store
        {
            Id = StoreId, Code = "G999", Name = "Harness Store", Slug = "harness-" + Name, RegionId = region.Id,
            State = Str(store, "state")!, KioskToken = Guid.NewGuid().ToString("N"),
        });
        db.AppUsers.Add(new AppUser { Id = userId, Username = "user-1", Tier = Tier.SuperUser });

        foreach (var r in Rows("store_trading_hours"))
            db.StoreTradingHours.Add(new StoreTradingHours { StoreId = StoreId, DayOfWeek = (int)Num(r, "day_of_week")!, OpenTime = Time(r, "open_time"), CloseTime = Time(r, "close_time"), Closed = Bool(r, "closed") });
        foreach (var r in Rows("store_attributes"))
            db.StoreAttributes.Add(new StoreAttributes
            {
                StoreId = StoreId, HasSco = Bool(r, "has_sco"), IsNewScoModel = Bool(r, "is_new_sco_model"), McoRegisters = (int)Num(r, "mco_registers")!,
                ScoLanes = (int)Num(r, "sco_lanes")!, ScoEfficiency = Num(r, "sco_efficiency")!.Value, FirstStep = Num(r, "first_step")!.Value,
                ScoShare = Num(r, "sco_share")!.Value, NightFill = Bool(r, "night_fill"),
            });
        foreach (var r in Rows("school_terms"))
            db.SchoolTerms.Add(new Entities.SchoolTerm { Id = Guid.NewGuid(), State = Str(r, "state")!, TermLabel = "Term", StartDate = Date(r, "start_date"), EndDate = Date(r, "end_date") });
        foreach (var r in Rows("school_term_exceptions"))
            db.SchoolTermExceptions.Add(new SchoolTermException { Id = Guid.NewGuid(), State = Str(r, "state")!, Date = Date(r, "date") });
        foreach (var r in Rows("break_rules"))
            db.BreakRules.Add(new Entities.BreakRule
            {
                Role = Str(r, "role")!, MinShiftHours = Num(r, "min_shift_hours")!.Value, MaxShiftHours = Num(r, "max_shift_hours")!.Value,
                GapHours = Num(r, "gap_hours")!.Value, BreakType = Str(r, "break_type")!, WeeklyCap = Num(r, "weekly_cap"),
                ConsecDayCap = (int?)Num(r, "consec_day_cap"), Ot15AfterHours = Num(r, "ot_1_5_after_hours"), Ot19AfterHours = Num(r, "ot_1_9_after_hours"),
                OtPeriodAfterHours = Num(r, "ot_period_after_hours"), DoubleTimeUnderGapHours = Num(r, "double_time_under_gap_hours"),
            });
        // The harness's rows come back in the order they were written: created_at keeps it.
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var i = 0;
        foreach (var r in Rows("employees"))
            db.Employees.Add(new Employee
            {
                Id = EmployeeId(Str(r, "id")!), StoreId = StoreId, Name = Str(r, "name")!, Role = Str(r, "role")!, PositionCode = Str(r, "position_code"),
                ContractHours = Num(r, "contract_hours")!.Value, SmYearBand = (int?)Num(r, "sm_year_band"), NotionalHours = Num(r, "notional_hours"),
                Active = Bool(r, "active", true), CreatedAt = t0.AddSeconds(i++),
            });
        foreach (var r in Rows("benchmark_days"))
            db.BenchmarkDays.Add(new BenchmarkDay
            {
                StoreId = StoreId, Date = Date(r, "date"), SalesForecast = Num(r, "sales_forecast") ?? 0, EmployeeCount = (int)(Num(r, "employee_count") ?? 0),
                CheckoutHours100 = Hundred(Num(r, "checkout_hours_90") ?? 0), LrHours100 = Hundred(Num(r, "lr_hours_90") ?? 0),
                CleaningHours100 = Hundred(Num(r, "cleaning_hours_90") ?? 0), SpecialsHours100 = Hundred(Num(r, "specials_hours_90") ?? 0),
                ResetHours100 = Hundred(Num(r, "reset_hours_90") ?? 0), AdminHours100 = Hundred(Num(r, "admin_hours_90") ?? 0),
            });
        foreach (var r in Rows("labour_group_rules"))
            db.LabourGroupRules.Add(new LabourGroupRule
            {
                Id = Guid.NewGuid(), DayOfWeek = (int)Num(r, "day_of_week")!, LabourGroup = Str(r, "labour_group")!,
                WindowStart = Time(r, "window_start")!.Value, WindowEnd = Time(r, "window_end")!.Value, WindowHours = Num(r, "window_hours"),
            });
        foreach (var r in Rows("reset_tasks"))
            db.ResetTasks.Add(new ResetTask
            {
                Id = IdFor(Str(r, "id")!), Name = Str(r, "name")!, SharePct = Num(r, "share_pct"), FixedMinutes = Num(r, "fixed_minutes"),
                WindowStartKind = Str(r, "window_start_kind")!, WindowStartTime = Time(r, "window_start_time"), WindowStartOffsetMin = (int)(Num(r, "window_start_offset_min") ?? 0),
                WindowEndKind = Str(r, "window_end_kind")!, WindowEndTime = Time(r, "window_end_time"), WindowEndOffsetMin = (int)(Num(r, "window_end_offset_min") ?? 0),
                Placement = Str(r, "placement") ?? "spread", SortOrder = (int)(Num(r, "sort_order") ?? 0), Active = Bool(r, "active", true), SubTask = Str(r, "sub_task"),
            });
        foreach (var r in Rows("admin_tasks"))
            db.AdminTasks.Add(new AdminTask
            {
                Id = IdFor(Str(r, "id")!), Name = Str(r, "name")!, ScheduleType = Str(r, "schedule_type")!, DayOfWeek = (int?)Num(r, "day_of_week"),
                NthWeek = (int?)Num(r, "nth_week"), RegionDays = r.TryGetProperty("region_days", out var rd) && rd.ValueKind == JsonValueKind.Object ? rd.GetRawText() : null,
                DurationType = Str(r, "duration_type")!, FixedMinutes = Num(r, "fixed_minutes"), MinutesPerUnit = Num(r, "minutes_per_unit"), MinMinutes = Num(r, "min_minutes"),
                WindowStartKind = Str(r, "window_start_kind")!, WindowStartTime = Time(r, "window_start_time"), WindowStartOffsetMin = (int)(Num(r, "window_start_offset_min") ?? 0),
                WindowEndKind = Str(r, "window_end_kind")!, WindowEndTime = Time(r, "window_end_time"), WindowEndOffsetMin = (int)(Num(r, "window_end_offset_min") ?? 0),
                SortOrder = (int)(Num(r, "sort_order") ?? 0), Active = Bool(r, "active", true),
            });
        foreach (var r in Rows("plan_placement_settings"))
            db.PlanPlacementSettings.Add(new PlanPlacementSettings
            {
                Id = true, Enabled = Bool(r, "enabled"), PeakBasis = Str(r, "peak_basis")!, PeakSmoothingBlocks = (int)Num(r, "peak_smoothing_blocks")!,
                PeakThresholdPct = Num(r, "peak_threshold_pct")!.Value, NightTraderFrom = Time(r, "night_trader_from")!.Value,
                NightTraderSharePct = Num(r, "night_trader_share_pct")!.Value,
                StepOrder = r.TryGetProperty("step_order", out var so) && so.ValueKind == JsonValueKind.Array ? so.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
            });
        var nonOp = Rows("non_op_entries");
        if (nonOp.Count > 0)
        {
            var cat = new NonOpCategory { Id = Guid.NewGuid(), Name = "Harness" };
            var sub = new NonOpSubcategory { Id = Guid.NewGuid(), CategoryId = cat.Id, Name = "Booked" };
            db.AddRange(cat, sub);
            foreach (var r in nonOp)
                db.NonOpEntries.Add(new NonOpEntry { Id = Guid.NewGuid(), StoreId = StoreId, Date = Date(r, "date"), SubcategoryId = sub.Id, Minutes = (int)Num(r, "minutes")!, CreatedAt = t0.AddSeconds(i++) });
        }
        foreach (var r in Rows("fixed_rosters"))
            db.FixedRosters.Add(new FixedRoster
            {
                Id = Guid.NewGuid(), EmployeeId = EmployeeId(Str(r, "employee_id")!), DayOfWeek = (int)Num(r, "day_of_week")!,
                WeekIndex = (int)(Num(r, "week_index") ?? 0), StartTime = Time(r, "start_time")!.Value, EndTime = Time(r, "end_time")!.Value,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The data layer as the app registers it, on the fixture's database, acting as the system,
    /// with the two reads the TypeScript tests substitute handed over.
    /// </summary>
    public ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IRefData>(sp => new HarnessRefData(sp.GetRequiredService<IRosterDbContextFactory>(), this));
        services.AddRosterData(b => b.UseRosterSqlServer(connectionString), new ConfigurationBuilder().Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public static IServiceScope SystemScope(ServiceProvider sp)
    {
        var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessContextAccessor>().Current = AccessContext.System("the whole-pipeline harness");
        return scope;
    }

    /// <summary>The trade curve and the POS morning customers as the scenario gives them (the TypeScript's vi.mock).</summary>
    private sealed class HarnessRefData(IRosterDbContextFactory contexts, HarnessScenario scenario) : RefDataReader(contexts)
    {
        public override Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetTradeCurveWeekAsync(Guid storeId, CancellationToken ct = default) =>
            Task.FromResult(scenario.CurveWeek);

        public override Task<IReadOnlyList<PosCustomerRow>> GetPosCustomerRowsAsync(Guid storeId, CancellationToken ct = default) =>
            Task.FromResult(scenario.PosCustomers);
    }
}
