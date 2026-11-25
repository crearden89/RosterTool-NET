// Port of src/app/(app)/admin/rules/labour/actions.ts, the row shapes its page.tsx passes
// to LabourRulesClient, and toTaskRow's summary labels.

using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Stage1;

namespace Roster.Web.Features.Rules.Labour;

/// <summary>One window a labour group may be worked in, on one weekday (labour_group_rules).</summary>
/// <param name="WindowStart">"HH:MM".</param>
/// <param name="WindowHours">The optional quota: this window fills first, up to this many hours.</param>
public sealed record LabourWindowRow(Guid Id, string LabourGroup, int DayOfWeek, string WindowStart, string WindowEnd, double? WindowHours);

/// <summary>A row of the window grid: the group, its label and its swatch.</summary>
public sealed record WindowGroupChip(string Key, string Label, string Color);

/// <summary>A labour group in the overlap priority list.</summary>
public sealed record PriorityGroup(string Key, string Label);

/// <summary>One admin task as the catalogue's form edits and saves it.</summary>
public sealed record AdminTaskInput
{
    public string Name { get; init; } = "";
    /// <summary>daily, weekly, monthly_nth or planning_week.</summary>
    public string ScheduleType { get; init; } = "weekly";
    public int? DayOfWeek { get; init; }
    public int? NthWeek { get; init; }
    /// <summary>Weekday by region prefix ("501" → 0); null when the day does not vary.</summary>
    public IReadOnlyDictionary<string, int>? RegionDays { get; init; }
    /// <summary>fixed, per_employee, per_register or till_calibration.</summary>
    public string DurationType { get; init; } = "fixed";
    public double? FixedMinutes { get; init; }
    public double? MinutesPerUnit { get; init; }
    public double? MinMinutes { get; init; }
    /// <summary>none, clock, open or close.</summary>
    public string WindowStartKind { get; init; } = "none";
    /// <summary>"HH:MM", for a clock side.</summary>
    public string? WindowStartTime { get; init; }
    public double WindowStartOffsetMin { get; init; }
    public string WindowEndKind { get; init; } = "none";
    public string? WindowEndTime { get; init; }
    public double WindowEndOffsetMin { get; init; }
}

/// <summary>A catalogue row: the task and the table's three summary columns.</summary>
public sealed record AdminTaskRow(Guid Id, AdminTaskInput Task, string ScheduleLabel, string DurationLabel, string WindowLabel);

/// <summary>One reset task as its editor edits and saves it.</summary>
public sealed record ResetTaskInput
{
    public string Name { get; init; } = "";
    /// <summary>share, fixed or remainder.</summary>
    public string Sizing { get; init; } = "share";
    public double? SharePct { get; init; }
    public double? FixedMinutes { get; init; }
    public string WindowStartKind { get; init; } = "none";
    public string? WindowStartTime { get; init; }
    public double WindowStartOffsetMin { get; init; }
    public string WindowEndKind { get; init; } = "none";
    public string? WindowEndTime { get; init; }
    public double WindowEndOffsetMin { get; init; }
    /// <summary>spread, avoid_peak, follow_demand, late or early.</summary>
    public string Placement { get; init; } = "spread";
    public bool Active { get; init; } = true;
    /// <summary>Which Reset sub-task the task counts under: re_runs, decarding, or "" (by its name).</summary>
    public string SubTask { get; init; } = "";
}

/// <summary>A reset task as stored (reset_tasks), times as "HH:MM".</summary>
public sealed record ResetTaskRow(
    Guid Id, string Name, double? SharePct, double? FixedMinutes,
    string WindowStartKind, string? WindowStartTime, double WindowStartOffsetMin,
    string WindowEndKind, string? WindowEndTime, double WindowEndOffsetMin,
    string Placement, bool Active, string? SubTask);

/// <summary>The Load Running start rule's thresholds, as saveLoadStartRules takes them.</summary>
public sealed record LoadStartInput(bool Enabled, double SmallLoadMaxHours, double LargeLoadMinHours, double QuietCustomersMax, double BusyCustomersMax, double CustomerWindowMin);

/// <summary>Everything the Labour Group Rules page shows.</summary>
public sealed record LabourRulesData(
    IReadOnlyList<LabourWindowRow> Rules,
    IReadOnlyList<AdminTaskRow> Tasks,
    IReadOnlyList<string> RegionPrefixes,
    IReadOnlyList<WindowGroupChip> Groups,
    IReadOnlyList<PriorityGroup> PriorityGroups,
    double BreakFactorPct,
    LoadStartRules LoadStartRules,
    IReadOnlyList<ResetTaskRow> ResetTasks);

/// <summary>The Labour Group Rules page's read and actions (Super User only).</summary>
public interface ILabourRulesService
{
    Task<LabourRulesData> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>addWindow: a new window ("HH:MM" times); a quota, when given, must be positive.</summary>
    Task<SaveResult> AddWindowAsync(string labourGroup, int dayOfWeek, string windowStart, string windowEnd, double? windowHours = null, CancellationToken cancellationToken = default);

    /// <summary>updateWindow: an existing window's day, times and quota, in place.</summary>
    Task<SaveResult> UpdateWindowAsync(Guid id, int dayOfWeek, string windowStart, string windowEnd, double? windowHours = null, CancellationToken cancellationToken = default);

    Task<SaveResult> DeleteWindowAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>clearWindows: every window of one labour group, in one action.</summary>
    Task<SaveResult> ClearWindowsAsync(string labourGroup, CancellationToken cancellationToken = default);

    Task<SaveResult> AddAdminTaskAsync(AdminTaskInput input, CancellationToken cancellationToken = default);

    Task<SaveResult> UpdateAdminTaskAsync(Guid id, AdminTaskInput input, CancellationToken cancellationToken = default);

    Task<SaveResult> DeleteAdminTaskAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>savePriorityOrder: the overlap / assignment priority (index 0 = highest).</summary>
    Task<SaveResult> SavePriorityOrderAsync(IReadOnlyList<string> orderedGroups, CancellationToken cancellationToken = default);

    /// <summary>saveBreakFactor: the paid-break allowance, a rostering_rules override (0-100%).</summary>
    Task<SaveResult> SaveBreakFactorAsync(double pct, CancellationToken cancellationToken = default);

    /// <summary>saveResetTask: add (null id) or change one reset task.</summary>
    Task<SaveResult> SaveResetTaskAsync(Guid? id, ResetTaskInput input, CancellationToken cancellationToken = default);

    Task<SaveResult> DeleteResetTaskAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>saveLoadStartRules: the start rule's switch and thresholds, as rostering_rules overrides.</summary>
    Task<SaveResult> SaveLoadStartRulesAsync(LoadStartInput input, CancellationToken cancellationToken = default);
}

/// <summary>An admin_tasks row as cleanTask leaves it.</summary>
public sealed record AdminTaskRecord(
    string Name, string ScheduleType, int? DayOfWeek, int? NthWeek, IReadOnlyDictionary<string, int>? RegionDays,
    string DurationType, double? FixedMinutes, double? MinutesPerUnit, double? MinMinutes,
    string WindowStartKind, string? WindowStartTime, double WindowStartOffsetMin,
    string WindowEndKind, string? WindowEndTime, double WindowEndOffsetMin);

/// <summary>A reset_tasks row as resetTaskRecord leaves it.</summary>
public sealed record ResetTaskRecord(
    string Name, double? SharePct, double? FixedMinutes,
    string WindowStartKind, string? WindowStartTime, double WindowStartOffsetMin,
    string WindowEndKind, string? WindowEndTime, double WindowEndOffsetMin,
    string Placement, bool Active, string? SubTask);

/// <summary>The actions' checks, before anything is written.</summary>
public static class LabourRulesChecks
{
    public const string BreakFactorNote = "Paid break allowance added to actual hours @100% before OE is measured (Labour Group Rules → Breaks).";
    public const string LoadStartNote = "Load Running start rule (Labour Group Rules → Load Running start and finish).";

    /// <summary>A window's quota: blank, or positive.</summary>
    public static string? CheckQuota(double? windowHours) =>
        windowHours is { } h && !(h > 0) ? "Hours must be positive (or blank for no quota)." : null;

    /// <summary>updateWindow: start before end ("HH:MM" compared as text), and the quota.</summary>
    public static string? CheckWindowUpdate(string windowStart, string windowEnd, double? windowHours)
    {
        if (windowStart == "" || windowEnd == "" || string.CompareOrdinal(windowStart, windowEnd) >= 0) return "Start must be before end.";
        return CheckQuota(windowHours);
    }

    private static bool IsOffsetKind(string kind) => kind is "open" or "close";

    // Math.round(x || 0): a blank (NaN) offset is 0.
    private static double RoundOffset(double x) => Js.Round(Js.Truthy(x) ? x : 0);

    /// <summary>cleanTask: the row to store, or why the task was refused.</summary>
    public static (AdminTaskRecord? Row, string? Error) CleanTask(AdminTaskInput input)
    {
        var name = Js.Trim(input.Name);
        if (name == "") return (null, "Enter a task name.");
        var needsDay = input.ScheduleType is "monthly_nth" or "planning_week" || (input.ScheduleType == "weekly" && input.RegionDays is null);
        if (needsDay && input.DayOfWeek is null) return (null, "Pick a day of the week.");
        if (input.ScheduleType == "monthly_nth" && input.NthWeek is null) return (null, "Pick which week of the month.");
        if (input.DurationType == "fixed" && !(input.FixedMinutes is { } f && f > 0)) return (null, "Enter the fixed minutes.");
        if (input.DurationType is "per_employee" or "per_register" && !(input.MinutesPerUnit is { } u && u > 0))
            return (null, "Enter the minutes per unit.");
        if (input.WindowStartKind == "clock" && string.IsNullOrEmpty(input.WindowStartTime)) return (null, "Enter the window start time.");
        if (input.WindowEndKind == "clock" && string.IsNullOrEmpty(input.WindowEndTime)) return (null, "Enter the window end time.");
        return (new AdminTaskRecord(
            name,
            input.ScheduleType,
            input.ScheduleType == "daily" ? null : input.DayOfWeek,
            input.ScheduleType == "monthly_nth" ? input.NthWeek : null,
            input.ScheduleType == "weekly" && input.RegionDays is { Count: > 0 } ? input.RegionDays : null,
            input.DurationType,
            input.DurationType == "fixed" ? input.FixedMinutes : null,
            input.DurationType is "per_employee" or "per_register" ? input.MinutesPerUnit : null,
            input.MinMinutes,
            input.WindowStartKind,
            input.WindowStartKind == "clock" ? input.WindowStartTime : null,
            IsOffsetKind(input.WindowStartKind) ? RoundOffset(input.WindowStartOffsetMin) : 0,
            input.WindowEndKind,
            input.WindowEndKind == "clock" ? input.WindowEndTime : null,
            IsOffsetKind(input.WindowEndKind) ? RoundOffset(input.WindowEndOffsetMin) : 0), null);
    }

    /// <summary>resetTaskRecord: the row to store, or why the task was refused.</summary>
    public static (ResetTaskRecord? Row, string? Error) ResetTaskRecordFor(ResetTaskInput input)
    {
        var name = Js.Trim(input.Name);
        if (name == "") return (null, "Name is required");
        if (input.Sizing == "share" && !(input.SharePct is { } s && s > 0 && s <= 100)) return (null, "Share must be between 1 and 100%");
        if (input.Sizing == "fixed" && !(input.FixedMinutes is { } f && f > 0)) return (null, "Fixed minutes must be positive");
        if (input.WindowStartKind == "clock" && string.IsNullOrEmpty(input.WindowStartTime)) return (null, "Window start needs a time");
        if (input.WindowEndKind == "clock" && string.IsNullOrEmpty(input.WindowEndTime)) return (null, "Window end needs a time");
        if (!ResetPlacements.All.Any(o => o.Value == input.Placement)) return (null, "Unknown placement");
        if (input.SubTask != "" && input.SubTask is not ("re_runs" or "decarding")) return (null, "A Reset task counts as Re-runs or Decarding");
        return (new ResetTaskRecord(
            name,
            input.Sizing == "share" ? input.SharePct : null,
            input.Sizing == "fixed" ? input.FixedMinutes : null,
            input.WindowStartKind,
            input.WindowStartKind == "clock" ? input.WindowStartTime : null,
            RoundOffset(input.WindowStartOffsetMin),
            input.WindowEndKind,
            input.WindowEndKind == "clock" ? input.WindowEndTime : null,
            RoundOffset(input.WindowEndOffsetMin),
            input.Placement,
            input.Active,
            input.SubTask is "re_runs" or "decarding" ? input.SubTask : null), null);
    }

    /// <summary>saveBreakFactor: a percentage from 0 to 100.</summary>
    public static string? CheckBreakFactor(double pct) =>
        !double.IsFinite(pct) || pct < 0 || pct > 100 ? "Break factor must be between 0 and 100%." : null;

    /// <summary>saveLoadStartRules: every threshold positive, each pair in order, the sample 15-240 minutes.</summary>
    public static string? CheckLoadStart(LoadStartInput input)
    {
        double[] nums = [input.SmallLoadMaxHours, input.LargeLoadMinHours, input.QuietCustomersMax, input.BusyCustomersMax, input.CustomerWindowMin];
        if (nums.Any(n => !double.IsFinite(n) || n <= 0)) return "Every threshold must be a positive number.";
        if (input.SmallLoadMaxHours >= input.LargeLoadMinHours)
            return "The 07:00 hours threshold must sit below the 06:00 large-load threshold.";
        if (input.QuietCustomersMax >= input.BusyCustomersMax) return "The 10:00 customer threshold must sit below the 09:30 threshold.";
        if (input.CustomerWindowMin < 15 || input.CustomerWindowMin > 240)
            return "The customer sample window must be between 15 and 240 minutes.";
        return null;
    }

    /// <summary>The rostering_rules rows saveLoadStartRules writes, in its order (enabled as 1/0).</summary>
    public static IReadOnlyList<(string Key, double Value)> LoadStartRows(LoadStartInput input) =>
    [
        (LoadStart.Keys.Enabled, input.Enabled ? 1 : 0),
        (LoadStart.Keys.SmallLoadMaxHours, input.SmallLoadMaxHours),
        (LoadStart.Keys.LargeLoadMinHours, input.LargeLoadMinHours),
        (LoadStart.Keys.QuietCustomersMax, input.QuietCustomersMax),
        (LoadStart.Keys.BusyCustomersMax, input.BusyCustomersMax),
        (LoadStart.Keys.CustomerWindowMin, input.CustomerWindowMin),
    ];
}

/// <summary>The page's fixed lists and the catalogue's summary labels (labour/page.tsx).</summary>
public static class LabourPage
{
    // Window-based groups configurable on this page. Registers/SCOs (checkout) are
    // excluded - they're demand-driven within trading hours, not a fixed window.
    public static readonly IReadOnlyList<WindowGroupChip> WindowGroups =
    [
        .. new[] { "load_running", "reset", "admin", "specials", "cleaning", "non_op" }
            .Select(k => new WindowGroupChip(k, LabourGroups.ByKey[k].Label, LabourGroups.ByKey[k].Color)),
        new("break", "Break", "#c7cbd1"),
    ];

    // Task-assignment priority groups, in the fixed reference order. The stored
    // labour_group_priority overrides this order at generate time.
    public static readonly IReadOnlyList<string> DefaultPriority = ["checkout", "load_running", "reset", "admin", "specials", "cleaning"];

    public static string PriorityLabel(string key) => key == "checkout" ? "Checkout" : LabourGroups.ByKey.TryGetValue(key, out var g) ? g.Label : key;

    /// <summary>The stored priority (known groups only), then any group it lacks.</summary>
    public static IReadOnlyList<PriorityGroup> PriorityGroups(IEnumerable<string> storedByPriority)
    {
        var stored = storedByPriority.Where(g => DefaultPriority.Contains(g)).ToList();
        return [.. stored.Concat(DefaultPriority.Where(g => !stored.Contains(g))).Select(k => new PriorityGroup(k, PriorityLabel(k)))];
    }

    /// <summary>Region code prefixes ("501".."509"), in region-name order, each once.</summary>
    public static IReadOnlyList<string> RegionPrefixes(IEnumerable<string?> regionNames) =>
        regionNames.Select(n => n is { Length: >= 3 } s && s[..3].All(char.IsAsciiDigit) ? s[..3] : null)
            .OfType<string>().Distinct().ToList();

    private static readonly string[] Nths = ["1st", "2nd", "3rd", "4th", "5th"];

    private static string Day(int? d) => Period.DayLabels[d ?? 0];

    public static string ScheduleLabel(AdminTaskInput t)
    {
        switch (t.ScheduleType)
        {
            case "daily":
                return "Every day";
            case "weekly":
                if (t.RegionDays is { } regionDays)
                {
                    var byDay = new JsMap<int, List<string>>();
                    foreach (var (prefix, d) in RegionDayEntries(regionDays)) byDay.Set(d, [.. byDay.GetOr(d, []), prefix]);
                    return string.Join(" · ", byDay.OrderBy(e => e.Key)
                        .Select(e => $"{Period.DayLabels[e.Key]} ({string.Join(", ", e.Value.Order(StringComparer.Ordinal))})"));
                }
                return $"Every {Day(t.DayOfWeek)}";
            case "monthly_nth":
                return $"{Nths[(t.NthWeek ?? 1) - 1]} {Day(t.DayOfWeek)} of the month";
            case "planning_week":
                return $"{Day(t.DayOfWeek)} of the planning week";
            default:
                return "";
        }
    }

    // Object.entries of a JSON object: integer-like keys ("501") come in ascending order.
    private static IEnumerable<(string Prefix, int Day)> RegionDayEntries(IReadOnlyDictionary<string, int> regionDays) =>
        regionDays.OrderBy(e => e.Key.Length).ThenBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value));

    public static string DurationLabel(AdminTaskInput t)
    {
        var floor = t.MinMinutes is { } m ? $" (min {Js.NumberToString(m)}m)" : "";
        return t.DurationType switch
        {
            "fixed" => $"{Js.NumberToString(t.FixedMinutes ?? 0)} min",
            "per_employee" => $"{Js.NumberToString(t.MinutesPerUnit ?? 0)} min × employees{floor}",
            "per_register" => $"{Js.NumberToString(t.MinutesPerUnit ?? 0)} min × registers{floor}",
            "till_calibration" => $"Registers + SCOs + 1 min{floor}",
            _ => "",
        };
    }

    /// <summary>toTaskRow: the task and its three summary columns.</summary>
    public static AdminTaskRow ToTaskRow(Guid id, AdminTaskInput t, double sortOrder) => new(id, t, ScheduleLabel(t), DurationLabel(t), AdminTasks.WindowLabelFor(new AdminTaskRule
    {
        Id = id.ToString(),
        Name = t.Name,
        ScheduleType = t.ScheduleType,
        DayOfWeek = t.DayOfWeek,
        NthWeek = t.NthWeek,
        RegionDays = t.RegionDays?.ToDictionary(e => e.Key, e => (double)e.Value),
        DurationType = t.DurationType,
        FixedMinutes = t.FixedMinutes,
        MinutesPerUnit = t.MinutesPerUnit,
        MinMinutes = t.MinMinutes,
        WindowStartKind = t.WindowStartKind,
        WindowStartTime = t.WindowStartTime is { } s ? Grid.TimeToMin(s) : null,
        WindowStartOffsetMin = t.WindowStartOffsetMin,
        WindowEndKind = t.WindowEndKind,
        WindowEndTime = t.WindowEndTime is { } e ? Grid.TimeToMin(e) : null,
        WindowEndOffsetMin = t.WindowEndOffsetMin,
        SortOrder = sortOrder,
    }));
}
