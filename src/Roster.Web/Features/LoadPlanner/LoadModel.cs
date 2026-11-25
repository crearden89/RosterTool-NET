// Port of src/app/load/[token]/loadModel.ts.
// ─── Load Planner: what the screen shows (pure) ─────────────────────────────
// The page's inputs as the server reads them, and the small decisions the
// screen makes about them: the plan for a set of pins or counts (through the
// same allocator the server uses), the order the truck is worked in, when each
// part of it is clear, why a person has their job, and where the timeline
// starts and ends. Nothing here plans anything itself.

using Roster.Engine.Core;
using Roster.Engine.Load;

namespace Roster.Web.Features.LoadPlanner;

public sealed record LoadTypeOption(
    string Id,
    string Name,
    double MinutesPerPallet,
    // Per-tenure-band minutes; the planner picks the band per person.
    PalletMinutes Minutes,
    string Color,
    double Qty, // the day's saved count (0 when none)
    // Allocation order (lower = worked first); 100 = unranked.
    double Priority);

public sealed record CrewOption(
    string Key, // employee id
    string Name,
    double StartMin, // their first Load Running tile
    string TenureBand,
    int? Days); // days of service today; null = no start date (treated as trained)

public sealed record PlanInputs(
    IReadOnlyList<CrewOption> Crew,
    IReadOnlyList<LoadTypeOption> Types,
    IReadOnlyDictionary<string, double> TypeCounts); // `${key}:${type}` -> days owned this fortnight

/// <summary>The timeline's span in minutes; <see cref="Now"/> is null when the time now is off it.</summary>
public sealed record TimelineSpan(double Lo, double Hi, double? Now);

/// <summary>A tag's place on the axis, as inline style values.</summary>
public sealed record TagPlace(string Left, string Transform)
{
    public string Style => $"left:{Left};transform:{Transform}";
}

public static class LoadModel
{
    private static double QtyOf(LoadTypeOption t, IReadOnlyDictionary<string, double>? qty) =>
        qty is not null ? qty.GetValueOrDefault(t.Id, 0) : t.Qty;

    /// <summary>The day's pallets for the allocator, in the page's order (sort order).</summary>
    public static List<LoadItem> LoadItemsFor(IEnumerable<LoadTypeOption> types, IReadOnlyDictionary<string, double>? qty = null) =>
        types
            .Select(t => new LoadItem
            {
                Type = t.Name,
                Color = t.Color,
                Minutes = t.Minutes,
                Qty = QtyOf(t, qty),
                Priority = t.Priority,
            })
            .Where(t => t.Qty > 0)
            .ToList();

    /// <summary>The plan for these pins (and these counts, while the truck is counted).</summary>
    public static LoadPlan PlanFor(PlanInputs i, IEnumerable<KeyValuePair<string, string>> pins, IReadOnlyDictionary<string, double>? qty = null) =>
        LoadAllocator.AllocateLoad(
            i.Crew.Select(c => new LoadCrewMember(c.Key, c.Name, c.StartMin, c.TenureBand)).ToList(),
            LoadItemsFor(i.Types, qty),
            new AllocateOptions
            {
                Pinned = new JsMap<string, string>(pins),
                TypeCounts = new JsMap<string, double>(i.TypeCounts),
            });

    /// <summary>
    /// The truck in the order the allocator works it: priority, then the biggest
    /// job (on the trained minutes), then by name. Types with no pallets drop out.
    /// </summary>
    public static List<LoadTypeOption> WorkOrder(IEnumerable<LoadTypeOption> types, IReadOnlyDictionary<string, double>? qty = null)
    {
        double Q(LoadTypeOption t) => Math.Max(0, Math.Floor(QtyOf(t, qty)));
        return Js.Sorted(
            types.Where(t => Q(t) > 0),
            (a, b) => (a.Priority - b.Priority)
                .Or(Q(b) * b.Minutes.Gt90 - Q(a) * a.Minutes.Gt90)
                .Or(Js.LocaleCompare(a.Name, b.Name)));
    }

    /// <summary>When the last pallet of a type comes off, or null when nobody has one.</summary>
    public static double? ClearBy(LoadPlan plan, string type)
    {
        double? end = null;
        foreach (var c in plan.Crew)
            foreach (var s in c.Segments)
                if (s.Type == type && (end is null || s.End > end)) end = s.End;
        return end;
    }

    public static double PalletsOf(CrewSchedule c) => c.Segments.Aggregate(0.0, (n, s) => n + s.Qty);

    /// <summary>The people the plan gives the warm-up or pallets to.</summary>
    public static List<CrewSchedule> WorkingCrew(LoadPlan plan) => plan.Crew.Where(c => c.Segments.Count > 0).ToList();

    /// <summary>Last to finish, on the minute the screen shows.</summary>
    public static bool IsLast(CrewSchedule c, LoadPlan plan) => Js.Round(c.FinishMin) == Js.Round(plan.TeamFinish);

    /// <summary>A pin the allocator honoured: the person owns the job they were pinned to.</summary>
    public static bool IsPinned(CrewSchedule c, IReadOnlyDictionary<string, string> pins) =>
        pins.TryGetValue(c.Key, out var pin) && !string.IsNullOrEmpty(pin) && pin == c.PrimaryType;

    /// <summary>One line on why this person has this job today.</summary>
    public static string WhyLine(CrewSchedule c, IReadOnlyDictionary<string, string> pins, IReadOnlyDictionary<string, double> typeCounts)
    {
        var pin = pins.GetValueOrDefault(c.Key);
        if (!string.IsNullOrEmpty(pin) && pin == c.PrimaryType) return "Pinned for today";
        // A pin to a type with nothing on today's truck is set aside by the planner.
        if (!string.IsNullOrEmpty(pin)) return $"Pinned to {pin}, none on the truck";
        if (string.IsNullOrEmpty(c.PrimaryType)) return "Helps where needed";
        var n = typeCounts.GetValueOrDefault($"{c.Key}:{c.PrimaryType}", 0);
        return n == 0 ? "First time this fortnight" : $"{Js.NumberToString(n)}× this fortnight";
    }

    /// <summary>
    /// Minutes between the plan's finish and when the load was marked complete,
    /// on the minutes the screen shows: positive = ahead of the plan.
    /// </summary>
    private static double MinutesAhead(double planFinish, double doneAt) => Js.Round(planFinish) - Js.Round(doneAt);

    /// <summary>"7 minutes ahead of the plan's 08:58" and the like.</summary>
    public static string AgainstPlan(double planFinish, double doneAt)
    {
        var d = MinutesAhead(planFinish, doneAt);
        var plan = $"the plan's {Grid.MinToTime(planFinish)}";
        if (d == 0) return $"right on {plan}";
        var n = Math.Abs(d);
        return $"{Js.NumberToString(n)} minute{(n == 1 ? "" : "s")} {(d > 0 ? "ahead of" : "behind")} {plan}";
    }

    /// <summary>"7 min ahead", "12 min behind", "on time".</summary>
    public static string AgainstPlanShort(double planFinish, double doneAt)
    {
        var d = MinutesAhead(planFinish, doneAt);
        return d == 0 ? "on time" : $"{Js.NumberToString(Math.Abs(d))} min {(d > 0 ? "ahead" : "behind")}";
    }

    // How far either side of the work the time now still belongs on the
    // timeline. Outside it (the page left open all day, or opened at 4 am) the
    // line would squeeze the bars into a corner, so it is left off.
    private const double NowBefore = 60;
    private const double NowAfter = 90;

    /// <summary>
    /// The timeline's span: whole quarter-hours around the work and the time now,
    /// with room after the finish for its line. Now is null when it is off it.
    /// </summary>
    public static TimelineSpan TimelineSpanFor(LoadPlan plan, double? now)
    {
        double? near = now is { } n && n >= plan.TeamStart - NowBefore && n <= plan.TeamFinish + NowAfter ? n : null;
        var from = near is null ? plan.TeamStart : Math.Min(plan.TeamStart, near.Value);
        var to = near is null ? plan.TeamFinish : Math.Max(plan.TeamFinish, near.Value);
        return new TimelineSpan(Math.Floor(from / 15) * 15, Math.Ceiling((to + 8) / 15) * 15, near);
    }

    /// <summary>Minutes between ticks: quarter-hours unless the span is long.</summary>
    public static double TickStep(double lo, double hi) => hi - lo <= 240 ? 15 : hi - lo <= 480 ? 30 : 60;

    /// <summary>"7:00" on the hour, ":15" between.</summary>
    public static string TickLabel(double t) =>
        t % 60 == 0 ? $"{Js.NumberToString(Math.Floor(t / 60))}:00" : $":{Js.PadStart(Js.NumberToString(t % 60), 2, '0')}";

    /// <summary>
    /// The now and finish tags share the axis: the finish tag gives way when they
    /// would overlap (the side column shows the finish in big type).
    /// </summary>
    public static bool FinishTagFits(double lo, double hi, double? now, double finish) =>
        now is null || Math.Abs(finish - now.Value) / (hi - lo) >= 0.17;

    /// <summary>A tag's position on the axis: centred on its time, kept inside the lane.</summary>
    public static TagPlace TagPlaceFor(double pct)
    {
        var shift = pct < 7 ? "0" : pct > 93 ? "-100%" : "-50%";
        return new TagPlace($"{Js.NumberToString(pct)}%", $"translateX({shift})");
    }

    private static readonly string[] Wd = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] Mon =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>"Tuesday 24 November", the header's date.</summary>
    public static string LongDay(DateOnly d) => $"{Wd[(int)d.DayOfWeek]} {d.Day} {Mon[d.Month - 1]}";

    /// <summary>"Tue 24 Nov", the phone header's date.</summary>
    public static string ShortDay(DateOnly d)
    {
        var parts = LongDay(d).Split(' ');
        return $"{parts[0][..3]} {parts[1]} {parts[2][..3]}";
    }
}
