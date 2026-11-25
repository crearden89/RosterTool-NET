// Port of src/app/floor/[token]/floorModel.ts.
// ─── Floor Display: the pure parts ───────────────────────────────────────────
// What the wall screen shows for each shift (task bars, what they are on now,
// what changes next) and how big a row can be for the number of people on.
// No data access and no components, so every rule here is unit-tested.

using Roster.Engine.Core;

namespace Roster.Web.Features.Floor;

// The tasks the screen colours, keyed as the roster grid's tiles and legend
// key them, so a task has the same colour on every grid in the app.
public static class FloorGroups
{
    public const string Checkout = "checkout";
    public const string LoadRunning = "load_running";
    public const string Reset = "reset";
    public const string Admin = "admin";
    public const string Specials = "specials";
    public const string Cleaning = "cleaning";
    public const string NonOp = "non_op";
    public const string Break = "break";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Checkout, LoadRunning, Reset, Admin, Specials, Cleaning, NonOp, Break,
    };
}

/// <summary>One bar: an unbroken run of one task, [From, To) in minutes from midnight.</summary>
public sealed record TaskRun(string Group, double From, double To);

/// <summary>A change inside the look-ahead window: a task (a FloorGroups key) or "finish".</summary>
public sealed record NextChange(double Time, string What)
{
    public const string Finish = "finish";
}

/// <summary>
/// Where one shift stands at the time now: not started, on shift (the task of the
/// current quarter-hour, null when none is painted), or finished.
/// </summary>
public abstract record NowState
{
    public sealed record Before(double Starts, IReadOnlyList<NextChange> Next) : NowState;
    public sealed record On(string? Group, IReadOnlyList<NextChange> Next) : NowState;
    public sealed record After(double Finished) : NowState;
}

/// <summary>The staffed day in whole hours: the timeline's left and right edges.</summary>
public sealed record DayWindow(double Lo, double Hi);

public sealed record RowScale(
    double Row, // the row height these sizes are drawn for
    bool Scrolls, // even at the floor the rows do not fit: they scroll
    double Name,
    double Tag,
    double Time,
    double Pill,
    double PillFont,
    double PillPad,
    double Gap, // between the pill and the next-changes line
    double Next,
    double Bar);

/// <summary>The load strip's pallet squares: their size in canvas pixels and the lines they take.</summary>
public sealed record PalletSquares(double Size, int Lines);

public static class FloorModel
{
    // Registers and SCOs are one Checkout tile on every grid, so they are one bar
    // here. Flex (an unassigned quarter-hour) and anything unknown draw nothing,
    // as the grid draws them.
    public static string? FloorGroup(string group)
    {
        if (group == "registers" || group == "scos") return FloorGroups.Checkout;
        return FloorGroups.All.Contains(group) ? group : null;
    }

    // A shift's quarter-hour tiles joined into bars: one bar per unbroken run of
    // the same task.
    public static List<TaskRun> TaskRuns(IEnumerable<DisplayBlock> blocks)
    {
        var runs = new List<TaskRun>();
        foreach (var b in Js.Sorted(blocks, (a, c) => a.Time - c.Time))
        {
            var group = FloorGroup(b.Group);
            if (group is null) continue;
            var last = runs.Count > 0 ? runs[^1] : null;
            if (last is not null && last.Group == group && last.To == b.Time) runs[^1] = last with { To = b.Time + Grid.BlockMin };
            else runs.Add(new TaskRun(group, b.Time, b.Time + Grid.BlockMin));
        }
        return runs;
    }

    // The "next 2 hours" window the screen looks ahead over.
    public const double LookAhead = 120;

    /// <summary>
    /// Where one shift stands at <paramref name="now"/>: not started (and when it
    /// starts), on shift (the task of the current quarter-hour), or finished. With
    /// it, the next changes inside the look-ahead window, at most
    /// <paramref name="max"/>: a different task, a break, or the end of the shift.
    /// A shift that starts inside the window lists its first task as its first
    /// change.
    /// </summary>
    public static NowState NowNext(double start, double end, IEnumerable<DisplayBlock> blocks, double now, int max = 2)
    {
        var at = new Dictionary<double, string?>();
        foreach (var b in blocks) at[b.Time] = FloorGroup(b.Group);
        var horizon = now + LookAhead;
        if (now >= end) return new NowState.After(end);

        // Changes from `from` on, against the task running just before it.
        List<NextChange> ChangesFrom(double from, string? current)
        {
            var next = new List<NextChange>();
            var prev = current;
            for (var m = from; m < end && m < horizon; m += Grid.BlockMin)
            {
                var g = at.GetValueOrDefault(m);
                if (g is not null && g != prev) next.Add(new NextChange(m, g));
                prev = g;
            }
            if (end <= horizon) next.Add(new NextChange(end, NextChange.Finish));
            return next.Take(max).ToList();
        }

        if (now < start)
        {
            var next = start < horizon ? ChangesFrom(start, null) : new List<NextChange>();
            return new NowState.Before(start, next);
        }
        var quarter = Math.Floor(now / Grid.BlockMin) * Grid.BlockMin;
        var group = at.GetValueOrDefault(quarter);
        return new NowState.On(group, ChangesFrom(quarter + Grid.BlockMin, group));
    }

    /// <inheritdoc cref="NowNext(double, double, IEnumerable{DisplayBlock}, double, int)"/>
    public static NowState NowNext(RosterRow shift, double now, int max = 2) =>
        NowNext(shift.Start, shift.End, shift.Blocks, now, max);

    // The staffed day in whole hours: the timeline's left and right edges.
    public static DayWindow? DaySpan(IReadOnlyCollection<(double Start, double End)> shifts)
    {
        if (shifts.Count == 0) return null;
        var lo = Math.Floor(Js.Min(shifts.Select(s => s.Start)) / 60) * 60;
        var hi = Math.Ceiling(Js.Max(shifts.Select(s => s.End)) / 60) * 60;
        return new DayWindow(lo, Math.Max(hi, lo + 60));
    }

    // ─── Row size for the number of people ───────────────────────────────────
    // The screen is laid out on a 1920 x 1080 canvas and scaled to the display, so
    // these are canvas pixels. The rows share what the header, the axis, the
    // legend and the load strip leave; a quiet day gets roomy rows, a busy one
    // smaller rows and smaller row text, down to a floor. Below the floor the rows
    // scroll and everything else stays put.

    public const double CanvasH = 1080;
    // Header 80, gap 12, axis 48, legend 38, load strip 88 with 12 above and 14 below.
    public const double ChromeH = 80 + 12 + 48 + 38 + 12 + 88 + 14;
    // The out-of-date banner and the gap under it.
    public const double BannerH = 86;
    // The floor leaves room for the two-line cells (name over shift times, the
    // task pill over the next changes) with clear space above and below them.
    public const double MinRow = 33;
    public const double MaxRow = 66;
    // Line height of the next-changes line (the stylesheet sets the same).
    public const double NextLine = 1.15;

    private static double Round1(double n) => Js.Round(n * 10) / 10;
    private static double Lerp(double a, double b, double t) => Round1(a + (b - a) * t);

    public static RowScale RowScaleFor(int count, bool banner = false)
    {
        var area = CanvasH - ChromeH - (banner ? BannerH : 0);
        var fair = area / Math.Max(1, count);
        var row = Math.Max(MinRow, Math.Min(MaxRow, fair));
        // 0 at the floor, 1 at the roomiest row.
        var t = (row - MinRow) / (MaxRow - MinRow);
        return new RowScale(
            Row: Round1(row),
            Scrolls: fair < MinRow,
            Name: Lerp(14, 20, t),
            Tag: Lerp(10, 12, t),
            Time: Lerp(11.5, 15, t),
            // The pill, the gap under it and the next-changes line grow together,
            // and more slowly than the row, so the space above and below them grows
            // too: about 2 px at the floor, after the row's 1 px rule.
            Pill: Lerp(14, 28, t),
            PillFont: Lerp(11, 15, t),
            PillPad: Lerp(8, 13, t),
            Gap: Lerp(1.5, 5, t),
            Next: Lerp(11, 14, t),
            // Bars stay thick on a busy day: they are what the screen is read by.
            Bar: Round1(Math.Max(12, Math.Min(20, row * 0.45))));
    }

    // ─── Words for times ─────────────────────────────────────────────────────

    private static string Pad(double n) => Js.PadStart(Js.NumberToString(n), 2, '0');

    // "9:30 am", the header clock.
    public static string Clock12(double minutes)
    {
        var h = Math.Floor(minutes / 60) % 24;
        var m = minutes % 60;
        var h12 = h % 12;
        return $"{Js.NumberToString(Js.Truthy(h12) ? h12 : 12)}:{Pad(m)} {(h >= 12 ? "pm" : "am")}";
    }

    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    // "Fri 20 Nov", the header date.
    public static string ShortDate(DateOnly d) => $"{Days[(int)d.DayOfWeek]} {d.Day} {Months[d.Month - 1]}";

    // ─── The load strip's pallet squares ─────────────────────────────────────
    // One square per pallet across the strip, as big as fits: one line for a
    // normal truck, two for a big one rather than squares too small to count.
    // Canvas pixels, as RowScaleFor.
    public const double SquaresW = 1180;

    public static PalletSquares SquareSize(double pallets, double types)
    {
        if (pallets <= 0) return new PalletSquares(22, 1);
        // Each square carries a 4px gap; each change of type a 12px one.
        var gaps = Math.Max(0, types - 1) * 12;
        var one = Math.Floor((SquaresW - gaps) / pallets) - 4;
        if (one >= 12) return new PalletSquares(Math.Min(22, one), 1);
        // Two lines: a square of slack per line for where the wrap falls.
        var two = Math.Floor((2 * SquaresW - gaps) / (pallets + 2)) - 4;
        return new PalletSquares(Math.Max(5, Math.Min(16, two)), 2);
    }
}
