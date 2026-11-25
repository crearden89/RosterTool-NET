// Port of the reference rows the Postgres migrations insert: 0002 load_types (with the
// 0032 tenure-band minutes), 0018 the non-op catalogue, 0019 admin_tasks (with the 0046
// window changes), 0020 positions, 0021 plan_placement_settings, 0022 variance_settings,
// 0028 leave_accrual_bands and leave_settings, 0030 probation_buffers, 0031 the
// paidBreakFactorPct rule and 0038 reset_tasks. The ReferenceData migration inserts the
// same rows (a test keeps the two in step); the development seeder writes a table's rows
// only when it is empty, which happens on a database built without the migrations (the
// SQLite unit tests), never on a migrated one.
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Seed;

/// <summary>The catalogues and settings rows the app expects to find in a new database.</summary>
public static class ReferenceData
{
    // ── 0020 positions ───────────────────────────────────────────────────────

    private static readonly int[] AsmHours = [30, 32, 35, 40, 45];
    private static readonly int[] AsmYears = [1, 2];
    private static readonly int[] SmHours = [40, 45, 50];
    private static readonly int[] SmYears = [1, 2, 3, 4, 7];

    public static IReadOnlyList<Position> Positions()
    {
        var list = new List<Position>
        {
            new() { Code = "JSA-16", Label = "JSA 16 year old", BaseRole = Roles.Jsa, HasRate = true, SortOrder = 10 },
            new() { Code = "JSA-17", Label = "JSA 17 year old", BaseRole = Roles.Jsa, HasRate = true, SortOrder = 20 },
            new() { Code = "SA-FF", Label = "Store Assistant Fully Flexible", BaseRole = Roles.Sa, HasRate = true, SortOrder = 30 },
            new() { Code = "SA-LR", Label = "Store Assistant Limited Roster", BaseRole = Roles.Sa, HasRate = true, SortOrder = 40 },
            new() { Code = "DSM", Label = "DSM", BaseRole = Roles.Dsm, HasRate = true, SortOrder = 50 },
            new() { Code = "RSM", Label = "Relieving Shift Manager", BaseRole = Roles.Rsm, HasRate = false, SortOrder = 60 },
        };
        // 100 + array_position(hours) * 10 + year (array_position is 1-based).
        for (var h = 0; h < AsmHours.Length; h++)
            foreach (var y in AsmYears)
                list.Add(new Position
                {
                    Code = $"ASM-{AsmHours[h]}-{y}", Label = $"ASM {AsmHours[h]} hour year {y}", BaseRole = Roles.Asm,
                    WeeklyHours = AsmHours[h], YearBand = y, HasRate = true, SortOrder = 100 + (h + 1) * 10 + y,
                });
        for (var h = 0; h < SmHours.Length; h++)
            foreach (var y in SmYears)
                list.Add(new Position
                {
                    Code = $"SM-{SmHours[h]}-{y}", Label = $"SM {SmHours[h]} hour year {y}", BaseRole = Roles.Sm,
                    WeeklyHours = SmHours[h], YearBand = y, HasRate = true, SortOrder = 200 + (h + 1) * 10 + y,
                });
        return list;
    }

    // ── 0002 load_types, 0032 tenure bands ───────────────────────────────────

    public static IReadOnlyList<LoadType> LoadTypes()
    {
        (string Name, double Minutes, string Color)[] rows =
        [
            ("Ambient", 19, "#2563eb"), ("Long Life", 28, "#7c3aed"), ("Short Life", 20, "#059669"),
            ("Freezer", 28, "#0891b2"), ("Bread", 17, "#d97706"), ("Produce", 10, "#16a34a"),
            ("Bulky", 5, "#dc2626"), ("Freeze to Thaw", 11, "#0d9488"),
        ];
        // 0032: round(minutes * 1.10 / 1.25 / 1.40, 1) - Postgres numeric rounding, half away
        // from zero; every product here is exact in decimal.
        static double Band(double minutes, decimal factor) =>
            (double)Math.Round((decimal)minutes * factor, 1, MidpointRounding.AwayFromZero);
        return rows.Select((r, i) => new LoadType
        {
            Name = r.Name, MinutesPerPallet = r.Minutes, Color = r.Color, SortOrder = i + 1,
            MinutesPerPalletLt90 = Band(r.Minutes, 1.10m),
            MinutesPerPalletLt60 = Band(r.Minutes, 1.25m),
            MinutesPerPalletLt30 = Band(r.Minutes, 1.40m),
        }).ToList();
    }

    // ── 0018 non-op catalogue ────────────────────────────────────────────────

    public static readonly IReadOnlyList<(string Name, int Sort)> NonOpCategories =
        [("Meetings", 1), ("Merchandising", 2), ("Miscellaneous", 3), ("Cleaning", 4), ("Training", 5)];

    /// <summary>(category, name, default minutes, max minutes, note): the sheet 0018 loaded verbatim.</summary>
    public static readonly IReadOnlyList<(string Category, string Name, int? Default, int? Max, string? Note)> NonOpSubcategories =
    [
        ("Meetings", "Store Manager Meetings", 120, 120, null),
        ("Meetings", "Performance Appraisal Preparation (JSA, SA, RSM)", 5, 5, null),
        ("Meetings", "Performance Appraisal Preparation (DSM, ASM, SM)", 30, 30, null),
        ("Meetings", "Performance Appraisal Completion (JSA, SA, RSM)", 15, 15, null),
        ("Meetings", "Performance Appraisal Completion (DSM, ASM, SM)", 60, 60, null),
        ("Meetings", "Grievance Investigations", null, null, "As approved by EMSO"),
        ("Meetings", "Career Conversations (Employee)", 30, 30, null),
        ("Meetings", "Career Conversations (Leader)", 30, 30, null),
        ("Meetings", "Performance Development Plan (Employee)", 30, 30, null),
        ("Meetings", "Performance Development Plan (Leader)", 30, 30, null),
        ("Meetings", "Liquor Accord", null, 60, null),
        ("Meetings", "SM Interview Completion", 60, 60, null),
        ("Meetings", "SM 1:1 Interview Completion", 30, 30, null),
        ("Meetings", "30/60/90 Review (Employee)", 30, 30, null),
        ("Meetings", "30/60/90 Review (Leader)", 30, 30, null),
        ("Meetings", "End of Probation: Personnel Leader Catch Up", 15, 15, null),
        ("Meetings", "30/60/90 Position Change Review (Employee)", 30, 30, null),
        ("Meetings", "30/60/90 Position Change Review (Leader)", 30, 30, null),
        ("Meetings", "Position Change: Personnel Leader Catch Up", 15, 15, null),
        ("Merchandising", "Monthly Product Launch", null, null, "As determined by Merchandising"),
        ("Merchandising", "Quarterly Remerches", null, null, "As determined by Merchandising"),
        ("Merchandising", "Yearly Retorfit Program (MSR)", null, null, "As approved by National Merchandising Working Group"),
        ("Merchandising", "Store Refresh or Extension", null, null, "As determined by Refresh Guide"),
        ("Merchandising", "Merchndising Trials", null, null, "As determined by Merchandising"),
        ("Merchandising", "Seasonal POS Install", null, null, "As determined by Merchandising"),
        ("Merchandising", "Grand Openings", null, null, "As determined by Grand Opening Guide"),
        ("Merchandising", "Seasonal Remerch", null, null, "As determined by Merchandising"),
        ("Miscellaneous", "Travel Time", null, null, "As per travel policy"),
        ("Miscellaneous", "Alarm Callouts", 180, 540, null),
        ("Miscellaneous", "Power Outages", null, null, "As approved by EMSO"),
        ("Miscellaneous", "Store Evacuations", null, null, "As approved by EMSO"),
        ("Miscellaneous", "Fire Drills (per person)", 15, 15, null),
        ("Miscellaneous", "Quarterly H&S Audits and Regional Safety Day", 120, 120, null),
        ("Miscellaneous", "Seasonal Process", null, null, "As approved by DSO"),
        ("Miscellaneous", "EA Update", null, null, "As approved by DSO"),
        ("Miscellaneous", "Broken Compactor", 15, 60, null),
        ("Miscellaneous", "Capacity Constraints", null, null, "As approved by DSO"),
        ("Cleaning", "Monthly", null, null, "Per approved cleaning time note"),
        ("Cleaning", "Bi-monthly", null, null, "Per approved cleaning time note"),
        ("Cleaning", "Quarterly", null, null, "Per approved cleaning time note"),
        ("Cleaning", "Biannual or Annual", null, null, "Per approved cleaning time note"),
        ("Training", "JSA Pathway (Day 1)", 330, 330, null),
        ("Training", "JSA Pathway (Day 2)", 315, 315, null),
        ("Training", "JSA Pathway (Day 3)", 495, 495, null),
        ("Training", "Store Assistant Pathway (Day 1)", 360, 360, null),
        ("Training", "Store Assistant Pathway (Day 2)", 270, 270, null),
        ("Training", "Store Assistant Pathway (Day 3)", 540, 540, null),
        ("Training", "Store Assistant Pathway (Day 4)", 420, 420, null),
        ("Training", "RSM Pathway (Module 1)", 180, 180, null),
        ("Training", "RSM Pathway (Module 2)", 180, 180, null),
        ("Training", "DSM Pathway", 72, 72, null),
        ("Training", "ASM Pathway", 103, 103, null),
        ("Training", "Future Leader Program", null, null, "As per national agenda"),
        ("Training", "Store Manager Pathway", null, null, null),
        ("Training", "Quarterly Training", null, null, "As per Academy NWCP Guide"),
        ("Training", "Head Office Training", null, null, "As per agenda"),
        ("Training", "EMSO Store Training", null, null, "As approved by DSO"),
    ];

    // ── 0019 admin_tasks, 0046 windows ───────────────────────────────────────

    private static AdminTask Task(string name, string schedule, int? dow, int? nth, string duration,
        double? fixedMin, double? perUnit, double? minMin,
        string startKind, string? startTime, int startOffset, string endKind, string? endTime, int endOffset, int sort,
        string? regionDays = null) => new()
    {
        Name = name, ScheduleType = schedule, DayOfWeek = dow, NthWeek = nth, RegionDays = regionDays, DurationType = duration,
        FixedMinutes = fixedMin, MinutesPerUnit = perUnit, MinMinutes = minMin,
        WindowStartKind = startKind, WindowStartTime = startTime is null ? null : Clock(startTime), WindowStartOffsetMin = startOffset,
        WindowEndKind = endKind, WindowEndTime = endTime is null ? null : Clock(endTime), WindowEndOffsetMin = endOffset,
        SortOrder = sort, Active = true,
    };

    private static TimeOnly Clock(string hhmm) => TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture);

    public static IReadOnlyList<AdminTask> AdminTasks() =>
    [
        Task("APM", "weekly", 0, null, "fixed", 15, null, null, "clock", "09:00", 0, "clock", "12:00", 0, 10),
        // 0046: End of Day no longer has an end window.
        Task("End of Day", "daily", null, null, "fixed", 17, null, null, "close", null, 0, "none", null, 0, 20),
        Task("Comms Overhaul", "weekly", 0, null, "fixed", 30, null, null, "clock", "12:00", 0, "clock", "15:00", 0, 30),
        // 0046: Fresh Ordering from 3.5 hours before close to close.
        Task("Fresh Ordering", "daily", null, null, "fixed", 150, null, null, "close", null, -210, "close", null, 0, 40),
        Task("Longlife Date Checks", "weekly", 0, null, "fixed", 120, null, null, "none", null, 0, "clock", "08:30", 0, 50),
        Task("Non BAU STM Tasks", "weekly", 0, null, "fixed", 60, null, null, "none", null, 0, "none", null, 0, 60),
        Task("Parameter Deviation & Custom Bays", "weekly", 0, null, "fixed", 15, null, null, "none", null, 0, "clock", "13:00", 0, 70),
        Task("STM Date Check", "daily", null, null, "fixed", 5, null, null, "clock", "17:00", 0, "none", null, 0, 80),
        Task("Safe Handover", "weekly", 4, null, "fixed", 14, null, null, "clock", "09:00", 0, "none", null, 0, 90),
        Task("Till Calibration", "weekly", null, null, "till_calibration", null, null, null, "none", null, 0, "none", null, 0, 100,
            regionDays: """{"501":0,"505":0,"506":0,"504":1,"507":1,"509":1,"503":3,"502":3}"""),
        Task("UKG End of Day", "daily", null, null, "fixed", 6, null, null, "close", null, 0, "none", null, 0, 110),
        Task("Use By Date Check", "daily", null, null, "fixed", 18, null, null, "clock", "17:00", 0, "none", null, 0, 120),
        Task("CGOS Ordering", "daily", null, null, "fixed", 58.5, null, null, "clock", "13:00", 0, "clock", "16:00", 0, 130),
        Task("EMSO Meeting", "monthly_nth", 1, 3, "fixed", 60, null, null, "none", null, 0, "none", null, 0, 140),
        Task("UKG End of PayPeriod", "planning_week", 0, null, "per_employee", null, 1.32, 20, "none", null, 0, "clock", "10:00", 0, 150),
        Task("UKG Rostering", "planning_week", 1, null, "per_employee", null, 3.16, 48, "none", null, 0, "none", null, 0, 160),
        Task("Specials Inventory Management", "monthly_nth", 1, 2, "fixed", 210, null, null, "none", null, 0, "none", null, 0, 170),
        Task("MyWalk", "monthly_nth", 1, 2, "fixed", 45, null, null, "none", null, 0, "none", null, 0, 180),
        Task("Register Assignment", "daily", null, null, "per_register", null, 3, null, "none", null, 0, "open", null, 0, 190),
        // 0046: Register Cash-Up from half an hour before close.
        Task("Register Cash-Up", "daily", null, null, "per_register", null, 5, null, "close", null, -30, "none", null, 0, 200),
        Task("Register Date Checks", "monthly_nth", 1, 3, "fixed", 45, null, null, "none", null, 0, "none", null, 0, 210),
        Task("EMSO Meeting Prep", "monthly_nth", 0, 3, "fixed", 60, null, null, "none", null, 0, "none", null, 0, 220),
        Task("SPi Planning", "planning_week", 1, null, "per_employee", null, 1.79, 27, "none", null, 0, "none", null, 0, 230),
        Task("Specials Price Card Audit", "weekly", 1, null, "fixed", 4, null, null, "none", null, 0, "none", null, 0, 240),
    ];

    // ── 0038 reset_tasks ─────────────────────────────────────────────────────

    public static IReadOnlyList<ResetTask> ResetTasks() =>
    [
        new() { Name = "Re-run 1", SharePct = 20, WindowStartKind = "clock", WindowStartTime = new(10, 0), WindowEndKind = "clock", WindowEndTime = new(14, 0), SortOrder = 10 },
        new() { Name = "Re-run 2", SharePct = 20, WindowStartKind = "clock", WindowStartTime = new(14, 0), WindowEndKind = "clock", WindowEndTime = new(18, 0), SortOrder = 20 },
        new() { Name = "First declare (light)", SharePct = 15, WindowStartKind = "clock", WindowStartTime = new(10, 0), WindowEndKind = "clock", WindowEndTime = new(14, 0), SortOrder = 30 },
        new() { Name = "Full decard", WindowStartKind = "close", WindowStartOffsetMin = -150, WindowEndKind = "close", SortOrder = 40 },
    ];

    // ── 0028 leave_accrual_bands ─────────────────────────────────────────────

    public static IReadOnlyList<LeaveAccrualBand> LeaveAccrualBands()
    {
        (double Contract, string Role, double Notional, double Weeks, double Annual)[] rows =
        [
            (100, "SM", 10.00, 5, 250.00), (90, "SM", 9.00, 5, 225.00), (80, "SM", 8.75, 5, 218.75),
            (90, "ASM", 9.00, 5, 225.00), (80, "ASM", 8.75, 5, 218.75), (60, "ASM", 6.00, 5, 150.00),
            (70, "DSM", 7.50, 5, 187.50), (60, "DSM", 7.50, 5, 187.50),
            (70, "SA", 7.25, 4, 145.00), (60, "SA", 6.50, 4, 130.00), (55, "SA", 6.00, 4, 120.00), (50, "SA", 5.50, 4, 110.00),
            (40, "SA", 5.25, 4, 80.00), (30, "SA", 4.75, 4, 60.00), (21, "SA", 4.00, 4, 42.00),
        ];
        return rows.Select(r => new LeaveAccrualBand
        {
            ContractHours = r.Contract, RoleClass = r.Role, LeaveNotionalHours = r.Notional, AccrualWeeks = r.Weeks, AnnualAccrualHours = r.Annual,
        }).ToList();
    }

    // ── 0030 probation_buffers ───────────────────────────────────────────────

    public static IReadOnlyList<ProbationBuffer> ProbationBuffers() =>
    [
        new() { LabourGroup = "*", MaxDays = 30, UpliftPct = 40 },
        new() { LabourGroup = "*", MaxDays = 60, UpliftPct = 25 },
        new() { LabourGroup = "*", MaxDays = 90, UpliftPct = 10 },
    ];

    // ── 0031 rostering_rules ─────────────────────────────────────────────────

    public static RosteringRule PaidBreakFactor() => new()
    {
        Key = "paidBreakFactorPct",
        Value = 4.5,
        Note = "Paid break allowance added to actual hours @100% before OE is measured (Labour Group Rules -> Breaks).",
    };

    /// <summary>Write every reference table that is still empty. Returns the rows written per table.</summary>
    public static async Task<Dictionary<string, int>> EnsureAsync(RosterDbContext db, CancellationToken cancellationToken = default)
    {
        var written = new Dictionary<string, int>();

        async Task Fill<T>(string table, DbSet<T> set, Func<IEnumerable<T>> rows) where T : class
        {
            if (await set.IgnoreQueryFilters().AnyAsync(cancellationToken)) return;
            var list = rows().ToList();
            set.AddRange(list);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            written[table] = list.Count;
        }

        await Fill("positions", db.Positions, Positions);
        await Fill("load_types", db.LoadTypes, LoadTypes);
        await Fill("non_op_categories", db.NonOpCategories,
            () => NonOpCategories.Select(c => new NonOpCategory { Name = c.Name, Sort = c.Sort }));
        if (!await db.NonOpSubcategories.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            var categoryIds = await db.NonOpCategories.IgnoreQueryFilters().ToDictionaryAsync(c => c.Name, c => c.Id, cancellationToken);
            var subs = NonOpSubcategories
                .Where(s => categoryIds.ContainsKey(s.Category))
                .Select(s => new NonOpSubcategory
                {
                    CategoryId = categoryIds[s.Category], Name = s.Name, MinMinutes = null, MaxMinutes = s.Max,
                    DaysAllowed = [0, 1, 2, 3, 4, 5, 6], DefaultMinutes = s.Default, Note = s.Note,
                })
                .ToList();
            db.NonOpSubcategories.AddRange(subs);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            written["non_op_subcategories"] = subs.Count;
        }
        await Fill("admin_tasks", db.AdminTasks, AdminTasks);
        await Fill("reset_tasks", db.ResetTasks, ResetTasks);
        await Fill("plan_placement_settings", db.PlanPlacementSettings, () => [new PlanPlacementSettings()]);
        await Fill("variance_settings", db.VarianceSettings, () => [new VarianceSettings()]);
        await Fill("leave_accrual_bands", db.LeaveAccrualBands, LeaveAccrualBands);
        await Fill("leave_settings", db.LeaveSettings, () => [new LeaveSettings()]);
        await Fill("probation_buffers", db.ProbationBuffers, ProbationBuffers);
        if (!await db.RosteringRules.IgnoreQueryFilters().AnyAsync(r => r.Key == "paidBreakFactorPct", cancellationToken))
        {
            db.RosteringRules.Add(PaidBreakFactor());
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            written["rostering_rules"] = 1;
        }
        return written;
    }
}
