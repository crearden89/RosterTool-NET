// Port of src/lib/resetTasks.ts.
// ─── Reset task catalogue resolver (pure) ────────────────────────────────────
// Daily reset hours are not one job: they are re-running stock, the light
// "box declared" pass, and the full decard - each with its own deadline. This
// resolver turns the catalogue into concrete named tasks for one store-day,
// each sized as a SHARE of the day's benchmark Reset hours (or fixed minutes),
// with the remainder task (the full decard) taking whatever is left.
//
// PLACEMENT-ONLY, exactly like the admin task catalogue: the benchmark Reset
// hours already fund these tasks; the resolver never adds hours to the plan.

using Roster.Engine.Core;

namespace Roster.Engine.Planning;

/// <summary>
/// How a task's blocks sit INSIDE its own window. Placement used to be a
/// per-GROUP setting on the Planning Order page, which could only say one thing
/// about the whole of Reset - and, because Stage 1 places Reset task-first, was
/// never actually read. Per task it can say the useful thing: a re-run keeps out
/// of the lunchtime rush, the decard just runs as late as its window allows.
/// </summary>
public static class ResetPlacements
{
    public static readonly IReadOnlyList<(string Value, string Label, string Help)> All =
    [
        ("spread", "Spread evenly",
            "Level the task across its whole window: each quarter-hour goes where the day has the least work planned, around everything already on the canvas. The default."),
        ("avoid_peak", "Avoid the busy period",
            "Outside the store's busy period first (as defined on Process → Planning Order), then outside premium time, quietest blocks first - keeps the aisle work out of the rush. Works whether the Planning Order switch is on or off."),
        ("follow_demand", "Follow the trade curve",
            "Busiest blocks first, by the store's own trade curve. For work that has to happen while the stock is moving. Works whether the Planning Order switch is on or off."),
        ("late", "As late as the window allows",
            "Fill from the end of the window backwards - the natural shape for a full decard before close."),
        ("early", "As early as the window allows",
            "Fill from the start of the window forwards, for a job the rest of the day depends on."),
    ];
}

public sealed record ResetTaskRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Share of the day's benchmark Reset hours; null with FixedMinutes null = remainder task.</summary>
    public double? SharePct { get; init; }
    public double? FixedMinutes { get; init; }
    public string WindowStartKind { get; init; } = "none";
    public double? WindowStartTime { get; init; } // minutes from midnight (clock kind)
    public double WindowStartOffsetMin { get; init; }
    public string WindowEndKind { get; init; } = "none";
    public double? WindowEndTime { get; init; }
    public double WindowEndOffsetMin { get; init; }
    public string Placement { get; init; } = "spread";
    public double SortOrder { get; init; }
    /// <summary>The Reset sub-task the task counts under (labels only); null = by its name.</summary>
    public string? SubTask { get; init; }
}

public sealed record ResetTaskContext(double TradingOpen, double TradingClose, double StaffOpen, double StaffClose);

public sealed record ResolvedResetTask
{
    public string Name { get; init; } = "";
    public double Minutes { get; init; }
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public string Placement { get; init; } = "spread";
}

public static class ResetTasks
{
    private static double SideMinutes(string kind, double? time, double offset, ResetTaskContext ctx, double fallback) => kind switch
    {
        "none" => fallback,
        "clock" => time ?? fallback,
        "open" => ctx.TradingOpen + offset,
        "close" => ctx.TradingClose + offset,
        _ => double.NaN,
    };

    /// <summary>
    /// Size and window every active task for a day carrying <paramref name="resetHours"/>
    /// of benchmark Reset. Shares and fixed minutes are taken first; the remainder
    /// task(s) split whatever is left. A remainder that comes out at zero drops its
    /// task for the day - there is nothing left to place, not a shortfall. A
    /// back-to-front window (bad config for this store's hours) widens to the
    /// staffed day rather than dropping the task: the work is still due, someone
    /// has to place it. A window merely outside staffed hours clamps instead, so
    /// the engine can flag the unplaceable minutes honestly.
    /// </summary>
    public static List<ResolvedResetTask> ResolveResetTasks(IReadOnlyList<ResetTaskRule> rules, double resetHours, ResetTaskContext ctx)
    {
        if (resetHours <= 0 || rules.Count == 0) return [];
        var totalMin = resetHours * 60;

        var sized = new JsMap<string, double>();
        double claimed = 0;
        var remainderTasks = new List<ResetTaskRule>();
        foreach (var r in rules)
        {
            if (r.SharePct is not null)
            {
                var m = r.SharePct.Value / 100 * totalMin;
                sized.Set(r.Id, m);
                claimed += m;
            }
            else if (r.FixedMinutes is not null)
            {
                sized.Set(r.Id, r.FixedMinutes.Value);
                claimed += r.FixedMinutes.Value;
            }
            else
            {
                remainderTasks.Add(r);
            }
        }
        var remainder = Math.Max(0, totalMin - claimed);
        foreach (var r in remainderTasks) sized.Set(r.Id, remainder / remainderTasks.Count);

        var out_ = new List<ResolvedResetTask>();
        foreach (var r in Js.Sorted(rules, (a, b) => a.SortOrder - b.SortOrder))
        {
            var minutes = sized.GetOr(r.Id, 0);
            if (minutes < 1) continue;
            var windowStart = SideMinutes(r.WindowStartKind, r.WindowStartTime, r.WindowStartOffsetMin, ctx, ctx.StaffOpen);
            var windowEnd = SideMinutes(r.WindowEndKind, r.WindowEndTime, r.WindowEndOffsetMin, ctx, ctx.StaffClose);
            // Inversion is judged on the RAW window (matching ResolveAdminTasks): a
            // genuinely back-to-front config widens to the staffed day. A window that
            // is merely OUTSIDE staffed hours must NOT widen - it clamps (possibly to
            // nothing) and the engine's per-task shortfall flag reports it, instead of
            // the task silently spreading across the whole trading day.
            if (windowEnd <= windowStart)
            {
                windowStart = ctx.StaffOpen;
                windowEnd = ctx.StaffClose;
            }
            else
            {
                windowStart = Math.Max(windowStart, ctx.StaffOpen);
                windowEnd = Math.Min(windowEnd, ctx.StaffClose);
            }
            out_.Add(new ResolvedResetTask { Name = r.Name, Minutes = minutes, WindowStart = windowStart, WindowEnd = windowEnd, Placement = r.Placement });
        }
        return out_;
    }
}
