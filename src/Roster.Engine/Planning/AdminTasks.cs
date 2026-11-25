// Port of src/lib/adminTasks/resolve.ts.
// ─── Admin task catalogue resolver ───────────────────────────────────────────
// Pure: given the catalogue rules and one store-day's context, decides which
// named administration tasks are DUE that day, how many minutes each takes
// (fixed or formula), and the concrete time window it must sit in.
//
// PLACEMENT-ONLY: the benchmark Admin hours already fund these tasks. The
// resolver never adds hours to the plan - it tells the store WHEN each task
// belongs within the Admin labour it already has.

using Roster.Engine.Core;

namespace Roster.Engine.Planning;

/// <summary>
/// One catalogue task. ScheduleType: daily, weekly, monthly_nth, planning_week.
/// DurationType: fixed, per_employee, per_register, till_calibration. Window
/// kinds: none, clock, open, close.
/// </summary>
public sealed record AdminTaskRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ScheduleType { get; init; } = "daily";
    public double? DayOfWeek { get; init; } // Mon=0..Sun=6
    public double? NthWeek { get; init; } // 1..5 (monthly_nth)
    public Dictionary<string, double>? RegionDays { get; init; } // region code prefix → dow
    public string DurationType { get; init; } = "fixed";
    public double? FixedMinutes { get; init; }
    public double? MinutesPerUnit { get; init; }
    public double? MinMinutes { get; init; }
    public string WindowStartKind { get; init; } = "none";
    public double? WindowStartTime { get; init; } // minutes from midnight (clock kind)
    public double WindowStartOffsetMin { get; init; }
    public string WindowEndKind { get; init; } = "none";
    public double? WindowEndTime { get; init; }
    public double WindowEndOffsetMin { get; init; }
    public double SortOrder { get; init; }
}

public sealed record AdminTaskContext
{
    public DateOnly Date { get; init; }
    public string? RegionPrefix { get; init; } // e.g. "501"
    public double EmployeeCount { get; init; } // benchmark file EmployeeCount for this store/day
    public double RegisterCount { get; init; } // store attributes MCO registers
    public double ScoCount { get; init; } // store attributes SCO lanes
    public double TradingOpen { get; init; } // minutes from midnight
    public double TradingClose { get; init; }
    public double StaffOpen { get; init; } // staffed-day bounds - the fallback for open-ended sides
    public double StaffClose { get; init; }
}

public sealed record ResolvedAdminTask
{
    public string Name { get; init; } = "";
    public double Minutes { get; init; }
    public double WindowStart { get; init; } // minutes from midnight
    public double WindowEnd { get; init; }
    public string WindowLabel { get; init; } = ""; // human wording, e.g. "Before open" / "13:00–16:00"
}

public static class AdminTasks
{
    // Which weekday-of-month occurrence a date is: the 15th is the 3rd of its
    // weekday (1st..7th → 1, 8th..14th → 2, ...).
    private static double NthWeekdayOfMonth(DateOnly date) => Math.Floor((date.Day - 1) / 7.0) + 1;

    // True when `date` falls in WEEK 1 of its fortnight (the planning week: the
    // week the store plans the NEXT fortnight and closes the pay period).
    private static bool InPlanningWeek(DateOnly date)
    {
        var start = Period.PeriodStartFor(date);
        for (var i = 0; i < 7; i++)
            if (Period.AddDaysIso(start, i) == date) return true;
        return false;
    }

    // The weekly day this rule wants for the given region, honouring a
    // region-varying map (e.g. Till Calibration) over the flat day_of_week.
    private static double? RuleDow(AdminTaskRule rule, string? regionPrefix)
    {
        if (rule.RegionDays is not null && regionPrefix is not null && rule.RegionDays.TryGetValue(regionPrefix, out var mapped)) return mapped;
        return rule.DayOfWeek;
    }

    public static bool IsTaskDue(AdminTaskRule rule, DateOnly date, string? regionPrefix)
    {
        var dow = Period.DowMon0(date);
        switch (rule.ScheduleType)
        {
            case "daily":
                return true;
            case "weekly":
            {
                var want = RuleDow(rule, regionPrefix);
                return want is not null && want == dow;
            }
            case "monthly_nth":
                return rule.DayOfWeek == dow && rule.NthWeek == NthWeekdayOfMonth(date);
            case "planning_week":
                return rule.DayOfWeek == dow && InPlanningWeek(date);
            default:
                // An unknown schedule: the TypeScript switch returns undefined, which
                // reads as false.
                return false;
        }
    }

    public static double TaskMinutes(AdminTaskRule rule, AdminTaskContext ctx)
    {
        double raw = rule.DurationType switch
        {
            "fixed" => rule.FixedMinutes ?? 0,
            "per_employee" => (rule.MinutesPerUnit ?? 0) * ctx.EmployeeCount,
            "per_register" => (rule.MinutesPerUnit ?? 0) * ctx.RegisterCount,
            // One minute per till plus one: Registers + SCOs + 1, from the store's
            // till configuration.
            "till_calibration" => ctx.RegisterCount + ctx.ScoCount + 1,
            // An unknown duration type leaves `raw` undefined in the TypeScript:
            // Math.max(undefined, ...) is NaN.
            _ => double.NaN,
        };
        return Math.Max(raw, rule.MinMinutes ?? 0);
    }

    private static double SideMinutes(string kind, double? time, double offset, AdminTaskContext ctx, double fallback) => kind switch
    {
        "none" => fallback,
        "clock" => time ?? fallback,
        "open" => ctx.TradingOpen + offset,
        "close" => ctx.TradingClose + offset,
        _ => double.NaN,
    };

    // Short human phrase for one window side, e.g. "open", "close +17m", "09:00".
    private static string? SideLabel(string kind, double? time, double offset)
    {
        if (kind == "none") return null;
        if (kind == "clock") return time is null ? null : Grid.MinToTime(time.Value);
        var anchor = kind == "open" ? "open" : "close";
        if (offset == 0) return anchor;
        var h = Math.Floor(Math.Abs(offset) / 60);
        var m = Math.Abs(offset) % 60;
        var span = h > 0 ? $"{Js.NumberToString(h)}h{(m > 0 ? Js.PadStart(Js.NumberToString(m), 2, '0') : "")}" : $"{Js.NumberToString(m)}m";
        return $"{span} {(offset < 0 ? "before" : "after")} {anchor}";
    }

    public static string WindowLabelFor(AdminTaskRule rule)
    {
        var s = SideLabel(rule.WindowStartKind, rule.WindowStartTime, rule.WindowStartOffsetMin);
        var e = SideLabel(rule.WindowEndKind, rule.WindowEndTime, rule.WindowEndOffsetMin);
        if (string.IsNullOrEmpty(s) && string.IsNullOrEmpty(e)) return "Anytime";
        if (!string.IsNullOrEmpty(s) && string.IsNullOrEmpty(e)) return $"After {s}";
        if (string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(e)) return $"Before {e}";
        return $"{s} – {e}";
    }

    // Resolve every due task for the day to concrete minutes + a concrete window.
    // Open-ended sides fall back to the staffed-day bounds; a window that ends up
    // inverted (bad config for this store's hours) is widened to the staffed day
    // rather than dropped - the task is still due, someone has to place it.
    public static List<ResolvedAdminTask> ResolveAdminTasks(IEnumerable<AdminTaskRule> rules, AdminTaskContext ctx)
    {
        var out_ = new List<ResolvedAdminTask>();
        foreach (var rule in rules)
        {
            if (!IsTaskDue(rule, ctx.Date, ctx.RegionPrefix)) continue;
            var minutes = TaskMinutes(rule, ctx);
            if (minutes <= 0) continue;
            var windowStart = SideMinutes(rule.WindowStartKind, rule.WindowStartTime, rule.WindowStartOffsetMin, ctx, ctx.StaffOpen);
            var windowEnd = SideMinutes(rule.WindowEndKind, rule.WindowEndTime, rule.WindowEndOffsetMin, ctx, ctx.StaffClose);
            // A clock start follows the close (owner decision 2): "after 17:00" on a
            // 17:00 close would leave the date checks only the close-down, where the
            // End of Day work already fills the one manager's time - they start
            // FollowCloseMin before close instead (15:00 on a 17:00 close).
            if (rule.WindowStartKind == "clock") windowStart = CloseRules.FollowClose(windowStart, ctx.TradingClose);
            if (windowEnd <= windowStart)
            {
                windowStart = ctx.StaffOpen;
                windowEnd = ctx.StaffClose;
            }
            out_.Add(new ResolvedAdminTask
            {
                Name = rule.Name,
                Minutes = Js.Round(minutes * 100) / 100,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                WindowLabel = WindowLabelFor(rule),
            });
        }
        return Js.Sort(out_, (a, b) => (a.WindowStart - b.WindowStart).Or(a.WindowEnd - b.WindowEnd).Or(Js.LocaleCompare(a.Name, b.Name)));
    }
}
