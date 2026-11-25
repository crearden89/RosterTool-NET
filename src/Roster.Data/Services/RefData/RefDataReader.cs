// Port of src/lib/refdata.ts (the reads; the TypeScript's row types are the
// engine's records) and the reset task catalogue read of src/lib/resetTasks.ts.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Feeds;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Planning;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Painter;
using EngineBreakRule = Roster.Engine.Core.BreakRule;

namespace Roster.Data.Services.RefData;

/// <summary>
/// <see cref="IRefData"/> over the database, as the current identity. Every getter is
/// request-cached (<see cref="RequestMemo"/>): inside a memo scope each table is read once.
/// Where the TypeScript read a table with no ORDER BY, rows are taken in a fixed order
/// (see the agent notes' DIFFS): the same rows, never a different set.
/// </summary>
public partial class RefDataReader(IRosterDbContextFactory contexts) : IRefData, ITradeCurveSource
{
    internal static string Hms(TimeOnly t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>timeToMin of the "HH:MM:SS" text Postgres gave for a time column.</summary>
    internal static double Min(TimeOnly t) => Grid.TimeToMin(Hms(t));

    // ─── Store basics ────────────────────────────────────────────────────────

    public Task<string?> GetStoreStateAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getStoreState", [storeId], async () =>
        {
            await using var db = contexts.Create();
            return await db.Stores.AsNoTracking().Where(s => s.Id == storeId).Select(s => (string?)s.State).FirstOrDefaultAsync(ct);
        });

    // The store's region code prefix ("501".."509") - regions are named
    // "501 - Minchinbury" etc. Used by region-varying admin task schedules
    // (e.g. Till Calibration runs on a different weekday per region).
    public Task<string?> GetRegionPrefixAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getRegionPrefix", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var name = await db.Stores.AsNoTracking().Where(s => s.Id == storeId)
                .Select(s => s.Region != null ? s.Region.Name : null).FirstOrDefaultAsync(ct);
            var m = name is null ? null : RegionPrefix().Match(name);
            return m is { Success: true } ? m.Groups[1].Value : null;
        });

    [GeneratedRegex("^([0-9]{3})")]
    private static partial Regex RegionPrefix();

    public Task<IReadOnlyList<TradingDayRow>> GetTradingWeekAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<TradingDayRow>>("refdata.getTradingWeek", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.StoreTradingHours.AsNoTracking().Where(t => t.StoreId == storeId).OrderBy(t => t.DayOfWeek).ToListAsync(ct);
            return rows.Select(t => new TradingDayRow(t.DayOfWeek, t.OpenTime is { } o ? Hms(o) : null, t.CloseTime is { } c ? Hms(c) : null, t.Closed)).ToList();
        });

    public Task<IReadOnlyList<SpecialHoursRow>> GetSpecialHoursAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<SpecialHoursRow>>("refdata.getSpecialHours", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.SpecialTradingHours.AsNoTracking().Where(s => s.StoreId == storeId)
                .OrderBy(s => s.StartDate).ThenBy(s => s.Id).ToListAsync(ct);
            return rows.Select(s => new SpecialHoursRow(s.StartDate, s.EndDate, s.Closed,
                s.OpenTime is { } o ? Hms(o) : null, s.CloseTime is { } c ? Hms(c) : null)).ToList();
        });

    public Task<StoreAttrsRow?> GetStoreAttrsAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getStoreAttrs", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var a = await db.StoreAttributes.AsNoTracking().Where(x => x.StoreId == storeId).FirstOrDefaultAsync(ct);
            return a is null ? null : new StoreAttrsRow
            {
                HasSco = a.HasSco,
                IsNewScoModel = a.IsNewScoModel,
                McoRegisters = a.McoRegisters,
                ScoLanes = a.ScoLanes,
                ScoEfficiency = a.ScoEfficiency,
                FirstStep = a.FirstStep,
                ScoShare = a.ScoShare,
                NightFill = a.NightFill,
            };
        });

    // ─── Public holidays (3-tier calendar, resolved in memory) ───────────────
    // Replaces the per-(store,date) is_public_holiday call - a fortnight render
    // was making 40+ of those calls for a handful of static calendar rows.

    public Task<IReadOnlySet<DateOnly>> GetHolidaySetAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlySet<DateOnly>>("refdata.getHolidaySet", [storeId], async () =>
        {
            var state = await GetStoreStateAsync(storeId, ct);
            await using var db = contexts.Create();
            var national = await db.PublicHolidaysNational.AsNoTracking().OrderBy(h => h.Date).Select(h => h.Date).ToListAsync(ct);
            var stateRows = state is null
                ? []
                : await db.PublicHolidaysState.AsNoTracking().Where(h => h.State == state).OrderBy(h => h.Date).Select(h => h.Date).ToListAsync(ct);
            var local = await db.PublicHolidaysLocal.AsNoTracking().Where(h => h.StoreId == storeId).OrderBy(h => h.Date).Select(h => h.Date).ToListAsync(ct);
            var set = new HashSet<DateOnly>();
            foreach (var d in national) set.Add(d);
            foreach (var d in stateRows) set.Add(d);
            foreach (var d in local) set.Add(d);
            return set;
        });

    public async Task<bool> IsPublicHolidayAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        (await GetHolidaySetAsync(storeId, ct)).Contains(date);

    // ─── School terms ────────────────────────────────────────────────────────

    public Task<SchoolCalendar> GetSchoolCalendarAsync(string? state, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getSchoolCalendar", [state], async () =>
        {
            if (string.IsNullOrEmpty(state)) return SchoolCalendar.None;
            await using var db = contexts.Create();
            var terms = await db.SchoolTerms.AsNoTracking().Where(t => t.State == state)
                .OrderBy(t => t.StartDate).Select(t => new { t.StartDate, t.EndDate }).ToListAsync(ct);
            var exceptions = await db.SchoolTermExceptions.AsNoTracking().Where(e => e.State == state)
                .OrderBy(e => e.Date).Select(e => e.Date).ToListAsync(ct);
            return new SchoolCalendar
            {
                Terms = terms.Select(t => new SchoolTerm(t.StartDate, t.EndDate)).ToList(),
                ExceptionDates = exceptions.Distinct().ToList(),
            };
        });

    // School day = weekday within a school term for the store's state that isn't an
    // exception (pupil-free) day. Same rule everywhere Stage 2 sizes JSA windows.
    public async Task<bool> IsSchoolDayForAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        var dow = Period.DowMon0(date);
        if (dow == 5 || dow == 6) return false;
        var cal = await GetSchoolCalendarAsync(await GetStoreStateAsync(storeId, ct), ct);
        return cal.IsSchoolDay(date);
    }

    // ─── Break rules ─────────────────────────────────────────────────────────

    private sealed record BreakRuleRow(
        string Role, double MinShiftHours, double MaxShiftHours, double GapHours, string BreakType, double? WeeklyCap,
        double? ConsecDayCap, double? Ot15AfterHours, double? Ot19AfterHours, double? OtPeriodAfterHours, double? DoubleTimeUnderGapHours);

    private Task<List<BreakRuleRow>> GetBreakRuleRowsAsync(CancellationToken ct) =>
        RequestMemo.Cached("refdata.getBreakRuleRows", async () =>
        {
            await using var db = contexts.Create();
            return await db.BreakRules.AsNoTracking().OrderBy(r => r.Role)
                .Select(r => new BreakRuleRow(r.Role, r.MinShiftHours, r.MaxShiftHours, r.GapHours, r.BreakType, r.WeeklyCap,
                    r.ConsecDayCap, r.Ot15AfterHours, r.Ot19AfterHours, r.OtPeriodAfterHours, r.DoubleTimeUnderGapHours))
                .ToListAsync(ct);
        });

    public Task<IReadOnlyDictionary<string, EngineBreakRule>> GetBreakRulesAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<string, EngineBreakRule>>("refdata.getBreakRules", async () =>
        {
            var rules = new Dictionary<string, EngineBreakRule>();
            foreach (var r in await GetBreakRuleRowsAsync(ct))
            {
                rules[r.Role] = new EngineBreakRule
                {
                    MinShiftHours = r.MinShiftHours,
                    MaxShiftHours = r.MaxShiftHours,
                    GapHours = r.GapHours,
                    BreakType = r.BreakType,
                    WeeklyCap = r.WeeklyCap,
                    ConsecDayCap = r.ConsecDayCap,
                    Ot15AfterHours = r.Ot15AfterHours,
                    Ot19AfterHours = r.Ot19AfterHours,
                };
            }
            return rules;
        });

    // The per-role halves of the pay rules (break_rules); the global multipliers
    // and the Sunday band live in pay_settings. A null threshold means the rule
    // does not apply to that role at all - which is how the salaried SM/ASM are
    // kept out of overtime and double time.
    public Task<IReadOnlyDictionary<string, RolePayRule>> GetRolePayRulesAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<string, RolePayRule>>("refdata.getRolePayRules", async () =>
        {
            var map = new Dictionary<string, RolePayRule>();
            foreach (var r in await GetBreakRuleRowsAsync(ct))
                map[r.Role] = new RolePayRule(r.Ot15AfterHours, r.OtPeriodAfterHours, r.DoubleTimeUnderGapHours);
            return map;
        });

    // ─── Rostering-rule overrides ────────────────────────────────────────────
    // The Rules → Rostering Rules admin tab stores OVERRIDES only; defaults live in
    // the engine. Rows whose key is not a RosteringRules field are tolerated
    // (custom entries) and simply ignored by the engine.
    public Task<IReadOnlyDictionary<string, double>> GetRuleOverridesAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<string, double>>("refdata.getRuleOverrides", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.RosteringRules.AsNoTracking().OrderBy(r => r.Key).Select(r => new { r.Key, r.Value }).ToListAsync(ct);
            var out_ = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var r in rows) out_[r.Key] = r.Value;
            return out_;
        });

    // ─── Roster order steps ──────────────────────────────────────────────────
    // The configured Stage 2 phase order. null = table empty = the shipped
    // default order (the engine's normaliseOrder applies it).
    public Task<IReadOnlyList<OrderStep>?> GetRosterOrderAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<OrderStep>?>("refdata.getRosterOrder", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.RosterOrderSteps.AsNoTracking().OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
            if (rows.Count == 0) return null;
            return rows.Select(r => new OrderStep(r.StepType, r.DayOrder)).ToList();
        });

    // The config-driven task-assignment priority order (labour_group_priority),
    // falling back to the engine default if the table is empty. Fetched once per
    // request however many engine runs need it.
    public Task<IReadOnlyList<string>> GetTaskPriorityAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<string>>("stage2.getTaskPriority", async () =>
        {
            List<string>? groups;
            try
            {
                await using var db = contexts.Create();
                groups = await db.LabourGroupPriority.AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.LabourGroup)
                    .Select(r => r.LabourGroup).ToListAsync(ct);
            }
            catch (Exception e) when (e is DbUpdateException or System.Data.Common.DbException)
            {
                groups = null; // unavailable: the engine's default order
            }
            return TaskPriority.FromConfig(groups);
        });

    // All labour-group time windows keyed by day-of-week (global config, a few
    // dozen rows) - Stage 1 was re-querying this table once per planned date.
    public Task<IReadOnlyDictionary<int, IReadOnlyList<WindowRule>>> GetLabourWindowsAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<int, IReadOnlyList<WindowRule>>>("refdata.getLabourWindows", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.LabourGroupRules.AsNoTracking()
                .OrderBy(r => r.DayOfWeek).ThenBy(r => r.WindowStart).ThenBy(r => r.WindowEnd).ThenBy(r => r.LabourGroup)
                .ToListAsync(ct);
            var byDow = new Dictionary<int, IReadOnlyList<WindowRule>>();
            foreach (var r in rows)
            {
                var list = byDow.TryGetValue(r.DayOfWeek, out var l) ? (List<WindowRule>)l : new List<WindowRule>();
                list.Add(new WindowRule(r.LabourGroup, Min(r.WindowStart), Min(r.WindowEnd), r.WindowHours));
                byDow[r.DayOfWeek] = list;
            }
            return byDow;
        });

    // ─── Reset task catalogue ────────────────────────────────────────────────
    // Named reset sub-tasks (re-runs, first declare, full decard) with their own
    // windows, resolved per store-day by the engine's ResetTasks. Same
    // global-config shape as the admin task catalogue.
    public Task<IReadOnlyList<ResetTaskRule>> GetResetTasksAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<ResetTaskRule>>("refdata.getResetTasks", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.ResetTasks.AsNoTracking().Where(r => r.Active).OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct);
            return rows.Select(r => new ResetTaskRule
            {
                Id = r.Id.ToString("D"),
                Name = r.Name,
                SharePct = r.SharePct,
                FixedMinutes = r.FixedMinutes,
                WindowStartKind = r.WindowStartKind,
                WindowStartTime = r.WindowStartTime is { } s ? Min(s) : null,
                WindowStartOffsetMin = r.WindowStartOffsetMin,
                WindowEndKind = r.WindowEndKind,
                WindowEndTime = r.WindowEndTime is { } e ? Min(e) : null,
                WindowEndOffsetMin = r.WindowEndOffsetMin,
                Placement = string.IsNullOrEmpty(r.Placement) ? "spread" : r.Placement,
                SortOrder = r.SortOrder,
                SubTask = r.SubTask,
            }).ToList();
        });

    // ─── Planning Order (placement) config ───────────────────────────────────
    // The Process → Planning Order tab's rules + settings, normalised so every group
    // always has a rule. Consulted by Stage 1 only while the master switch is on.
    public Task<PlacementConfig> GetPlacementConfigAsync(CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getPlacementConfig", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.PlanPlacementRules.AsNoTracking().OrderBy(r => r.Position).ToListAsync(ct);
            var s = await db.PlanPlacementSettings.AsNoTracking().Where(x => x.Id).FirstOrDefaultAsync(ct);
            var mapped = rows.Select(r => new PlacementRule
            {
                Group = r.LabourGroup,
                Strategy = r.Strategy,
                AnchorKind = r.AnchorKind,
                AnchorTime = r.AnchorTime is { } t ? Min(t) : null,
                AnchorOffsetMin = r.AnchorOffsetMin,
                PrePeakPct = r.PrePeakPct,
                PostPeakPct = r.PostPeakPct,
                AvoidStrength = r.AvoidStrength,
                NightTraderStrategy = r.NightTraderStrategy,
            }).ToList();
            var rules = Placement.NormaliseRules(mapped);
            // A saved row counts only where NormaliseRules kept it (a known group
            // with a known strategy); a row it replaced with the default did not.
            var savedGroups = rules.Where(r => mapped.Any(m => m.Group == r.Group && m.Strategy == r.Strategy)).Select(r => r.Group).ToList();
            var settings = s is null
                ? Placement.DefaultSettings
                : new PlacementSettings
                {
                    Enabled = s.Enabled,
                    PeakBasis = s.PeakBasis,
                    PeakSmoothingBlocks = s.PeakSmoothingBlocks,
                    PeakThresholdPct = s.PeakThresholdPct,
                    NightTraderFromMin = Min(s.NightTraderFrom),
                    NightTraderSharePct = s.NightTraderSharePct,
                };
            // A stale or partial saved order can never drop a group: anything missing
            // is appended in the shipped sequence.
            var saved = (s?.StepOrder ?? []).Where(g => WindowGroups.All.Contains(g)).ToList();
            var stepOrder = saved.Concat(WindowGroups.DefaultStepOrder.Where(g => !saved.Contains(g))).ToList();
            return new PlacementConfig { Rules = rules, Settings = settings, StepOrder = stepOrder, SavedGroups = savedGroups };
        });

    // ─── Admin task catalogue ────────────────────────────────────────────────
    // The national catalogue of named administration tasks (schedule + duration
    // formula + placement window). Resolved per store-day by the engine's AdminTasks.
    public Task<IReadOnlyList<AdminTaskRule>> GetAdminTasksAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<AdminTaskRule>>("refdata.getAdminTasks", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.AdminTasks.AsNoTracking().Where(r => r.Active).OrderBy(r => r.SortOrder).ToListAsync(ct);
            // .order("sort_order").order("name"): names compared as Postgres's collation does.
            return Js.Sorted(rows, (a, b) => a.SortOrder != b.SortOrder ? a.SortOrder - b.SortOrder : Js.LocaleCompare(a.Name, b.Name))
                .Select(MapAdminTaskRow).ToList();
        });

    /// <summary>mapAdminTaskRow: an admin_tasks row as the engine's rule.</summary>
    internal static AdminTaskRule MapAdminTaskRow(Entities.AdminTask r) => new()
    {
        Id = r.Id.ToString("D"),
        Name = r.Name,
        ScheduleType = r.ScheduleType,
        DayOfWeek = r.DayOfWeek,
        NthWeek = r.NthWeek,
        RegionDays = string.IsNullOrWhiteSpace(r.RegionDays) ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(r.RegionDays),
        DurationType = r.DurationType,
        FixedMinutes = r.FixedMinutes,
        MinutesPerUnit = r.MinutesPerUnit,
        MinMinutes = r.MinMinutes,
        WindowStartKind = r.WindowStartKind,
        WindowStartTime = r.WindowStartTime is { } s ? Min(s) : null,
        WindowStartOffsetMin = r.WindowStartOffsetMin,
        WindowEndKind = r.WindowEndKind,
        WindowEndTime = r.WindowEndTime is { } e ? Min(e) : null,
        WindowEndOffsetMin = r.WindowEndOffsetMin,
        SortOrder = r.SortOrder,
    };

    // ─── Employees ───────────────────────────────────────────────────────────

    public Task<IReadOnlyList<EmployeeRow>> GetEmployeesAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<EmployeeRow>>("refdata.getEmployees", [storeId], async () =>
        {
            await using var db = contexts.Create();
            return await db.Employees.AsNoTracking().Where(e => e.StoreId == storeId)
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
                .Select(e => new EmployeeRow(e.Id, e.Name, e.Role, e.PositionCode, e.ContractHours, e.SmYearBand, e.NotionalHours, e.Active))
                .ToListAsync(ct);
        });

    // ─── Positions catalogue ─────────────────────────────────────────────────
    // The payroll position list (also the pay_rates key set), extendable by a
    // super user, so it's read from the database rather than the shipped list.
    public Task<IReadOnlyList<PositionRow>> GetPositionsAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<PositionRow>>("refdata.getPositions", async () =>
        {
            await using var db = contexts.Create();
            return await db.Positions.AsNoTracking().Where(p => p.Active).OrderBy(p => p.SortOrder).ThenBy(p => p.Code)
                .Select(p => new PositionRow(p.Code, p.Label, p.BaseRole, p.WeeklyHours, p.YearBand, p.HasRate, p.SortOrder))
                .ToListAsync(ct);
        });

    // ─── Planning inputs ─────────────────────────────────────────────────────

    // Dates for this store with PH load-running reallocation switched on. The flag
    // is rare, so the whole set is one tiny query instead of 2 per planned date.
    public Task<IReadOnlySet<DateOnly>> GetPhReallocSetAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlySet<DateOnly>>("refdata.getPhReallocSet", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var dates = await db.DayPlans.AsNoTracking().Where(d => d.StoreId == storeId && d.PhLoadReallocated)
                .OrderBy(d => d.Date).Select(d => d.Date).ToListAsync(ct);
            return dates.ToHashSet();
        });

    // One benchmark fetch per (store, date) per request - Stage 1, the Till engine
    // and PH reallocation all read from this same row.
    public Task<BenchmarkDayRow?> GetBenchmarkDayAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getBenchmarkDay", [storeId, date], async () =>
        {
            await using var db = contexts.Create();
            return await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == storeId && b.Date == date)
                .Select(b => new BenchmarkDayRow
                {
                    SalesForecast = b.SalesForecast,
                    CheckoutHours90 = b.CheckoutHours90,
                    CheckoutHours100 = b.CheckoutHours100,
                    LrHours90 = b.LrHours90,
                    CleaningHours90 = b.CleaningHours90,
                    SpecialsHours90 = b.SpecialsHours90,
                    ResetHours90 = b.ResetHours90,
                    AdminHours90 = b.AdminHours90,
                    EmployeeCount = b.EmployeeCount,
                })
                .FirstOrDefaultAsync(ct);
        });

    public Task<IReadOnlyDictionary<DateOnly, SalesOverrideRow>> GetSalesOverrideMapAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<DateOnly, SalesOverrideRow>>("refdata.getSalesOverrideMap", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.SalesOverrides.AsNoTracking().Where(o => o.StoreId == storeId).OrderBy(o => o.Date).ToListAsync(ct);
            var map = new Dictionary<DateOnly, SalesOverrideRow>();
            foreach (var r in rows) map[r.Date] = new SalesOverrideRow { Closed = r.Closed, Method = r.Method, Pct = r.Pct, Value = r.Value };
            return map;
        });

    // The store's full weekly quarter-hour allocation curve (all 7 days), sorted by
    // block time within each day.
    public Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetQuarterHourWeekAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>>("refdata.getQuarterHourWeek", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.QuarterHourAllocations.AsNoTracking().Where(q => q.StoreId == storeId)
                .OrderBy(q => q.BlockTime).ThenBy(q => q.DayOfWeek).ToListAsync(ct);
            var byDow = new Dictionary<int, IReadOnlyList<QuarterHourRow>>();
            foreach (var r in rows)
            {
                var list = byDow.TryGetValue(r.DayOfWeek, out var l) ? (List<QuarterHourRow>)l : new List<QuarterHourRow>();
                list.Add(new QuarterHourRow { DayOfWeek = r.DayOfWeek, BlockTime = Hms(r.BlockTime), Allocation = r.Allocation });
                byDow[r.DayOfWeek] = list;
            }
            return byDow;
        });

    // ─── POS interval statistics ─────────────────────────────────────────────
    // The measured trade behind the curve (pos_interval_stats). One read per store
    // per request - a four-week sample is ~1,400 rows - shared by everything that
    // reads it: the trade-curve derivation and the Load Running start rule's
    // customer figure.
    public Task<IReadOnlyList<PosQuarterHourRow>> GetPosIntervalRowsAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<PosQuarterHourRow>>("refdata.getPosIntervalRows", [storeId], async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.PosIntervalStats.AsNoTracking().Where(p => p.StoreId == storeId)
                .OrderBy(p => p.Date).ThenBy(p => p.BlockTime)
                .Select(p => new { p.Date, p.BlockTime, p.McoCustomers, p.ScoCustomers, p.McoItems, p.ScoItems })
                .ToListAsync(ct);
            var code = storeId.ToString("D");
            return rows.Select(r => new PosQuarterHourRow(0, code, r.Date, Hms(r.BlockTime), r.McoCustomers, r.ScoCustomers, r.McoItems, r.ScoItems)).ToList();
        });

    public virtual Task<IReadOnlyList<PosCustomerRow>> GetPosCustomerRowsAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<PosCustomerRow>>("refdata.getPosCustomerRows", [storeId], async () =>
            (await GetPosIntervalRowsAsync(storeId, ct))
                .Select(r => new PosCustomerRow(r.Date, Grid.TimeToMin(r.BlockTime), r.McoCustomers + r.ScoCustomers)).ToList());

    // ─── The trade curve a store plans with ──────────────────────────────────
    // Derived at READ time from the POS interval statistics (public holidays and
    // zero-trade dates excluded), per weekday, falling back per weekday to the
    // legacy quarter_hour_allocation rows for stores whose POS export has not
    // been loaded yet. This is THE curve source: the Till engine, the Stage 1
    // placement curve and the plan page's trade-curve card all read it, so a new
    // POS upload changes every one of them at the next request with no derived
    // write in between.
    public virtual Task<IReadOnlyDictionary<int, IReadOnlyList<QuarterHourRow>>> GetTradeCurveWeekAsync(Guid storeId, CancellationToken ct = default) =>
        RequestMemo.Cached("refdata.getTradeCurveWeek", [storeId], async () =>
        {
            var pos = await GetPosIntervalRowsAsync(storeId, ct);
            var legacyWeek = await GetQuarterHourWeekAsync(storeId, ct);
            var holidays = await GetHolidaySetAsync(storeId, ct);
            return PosAllocation.AssembleCurveWeek(pos, legacyWeek.Values.SelectMany(v => v), holidays);
        });
}
