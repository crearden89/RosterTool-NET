// Port of src/app/load/[token]/loadModel.test.ts.

using System.Text.Json;
using Roster.Engine.Core;
using Roster.Engine.Load;
using Roster.Web.Features.LoadPlanner;
using static Roster.Web.Features.LoadPlanner.LoadModel;

namespace Roster.Web.Tests.LoadPlanner;

public class LoadModelTests
{
    private static LoadTypeOption Type(string id, string name, double minutes, double qty, double priority = 100) =>
        new(id, name, minutes, new PalletMinutes(minutes, minutes * 1.1, minutes * 1.25, minutes * 1.4), $"#{id}{id}{id}", qty, priority);

    // In sort order, as the page reads them.
    private static readonly List<LoadTypeOption> Types =
    [
        Type("1", "Ambient", 19, 9, 6),
        Type("2", "Long Life", 28, 4, 5),
        Type("3", "Short Life", 20, 5, 2),
        Type("4", "Freezer", 28, 3, 6),
        Type("5", "Bulky", 5, 0, 100),
        Type("6", "Freeze to Thaw", 11, 2, 1),
    ];

    private static readonly List<CrewOption> Crew =
    [
        new("e1", "Chloe Park", 390, TenureBands.Gt90, null),
        new("e2", "Grace Murphy", 390, TenureBands.Lt30, 26),
        new("e3", "Hannah Lee", 405, TenureBands.Gt90, 400),
    ];

    private static readonly Dictionary<string, double> TypeCounts = new() { ["e1:Short Life"] = 3, ["e3:Ambient"] = 2 };
    private static readonly PlanInputs Inputs = new(Crew, Types, TypeCounts);
    private static readonly Dictionary<string, string> NoPins = new();

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void Hands_the_allocator_the_days_pallets_in_sort_order_as_before()
    {
        Assert.Equal(
            new (string, double, double?)[] { ("Ambient", 9, 6), ("Long Life", 4, 5), ("Short Life", 5, 2), ("Freezer", 3, 6), ("Freeze to Thaw", 2, 1) },
            LoadItemsFor(Types).Select(i => (i.Type, i.Qty, i.Priority)));
        Assert.Equal(
            new (string, double)[] { ("Ambient", 2), ("Bulky", 1) },
            LoadItemsFor(Types, new Dictionary<string, double> { ["1"] = 2, ["5"] = 1 }).Select(i => (i.Type, i.Qty)));
    }

    [Fact]
    public void Is_the_allocators_own_plan_for_the_same_crew_pins_and_history()
    {
        var pins = new Dictionary<string, string> { ["e2"] = "Short Life" };
        var plan = PlanFor(Inputs, pins);
        var direct = Allocate.AllocateLoad(
            Crew.Select(c => new LoadCrewMember(c.Key, c.Name, c.StartMin, c.TenureBand)).ToList(),
            LoadItemsFor(Types),
            new AllocateOptions { Pinned = new JsMap<string, string>(pins), TypeCounts = new JsMap<string, double>(TypeCounts) });
        Assert.Equal(Json(direct), Json(plan));
        Assert.Equal("Short Life", plan.Crew.First(c => c.Key == "e2").PrimaryType);
    }

    [Fact]
    public void Lists_the_truck_in_the_order_it_is_worked_priority_then_the_biggest_job()
    {
        Assert.Equal(
            ["Freeze to Thaw", "Short Life", "Long Life", "Ambient" /* 9 x 19 = 171 trained minutes */, "Freezer" /* 3 x 28 = 84 */],
            WorkOrder(Types).Select(t => t.Name));
        Assert.Equal(["Freezer", "Ambient"], WorkOrder(Types, new Dictionary<string, double> { ["4"] = 7, ["1"] = 1 }).Select(t => t.Name));
    }

    [Fact]
    public void Says_when_each_part_of_the_truck_is_clear()
    {
        var plan = PlanFor(Inputs, NoPins);
        var ends = plan.Crew.SelectMany(c => c.Segments.Where(s => s.Type == "Ambient").Select(s => s.End));
        Assert.Equal((double?)ends.Max(), ClearBy(plan, "Ambient"));
        Assert.Null(ClearBy(plan, "Bulky"));
        Assert.Equal(plan.TotalPallets, plan.Crew.Sum(PalletsOf));
    }

    private static CrewSchedule Person(Action<CrewSchedule>? over = null)
    {
        var p = new CrewSchedule
        {
            Key = "e1",
            Name = "Chloe Park",
            StartMin = 390,
            TenureBand = TenureBands.Gt90,
            FinishMin = 500,
            Segments = [],
            PrimaryType = "Short Life",
        };
        over?.Invoke(p);
        return p;
    }

    [Fact]
    public void Gives_the_reason_for_the_job_a_pin_or_how_often_lately()
    {
        Assert.Equal("Pinned for today", WhyLine(Person(), new Dictionary<string, string> { ["e1"] = "Short Life" }, TypeCounts));
        Assert.Equal("3× this fortnight", WhyLine(Person(), NoPins, TypeCounts));
        Assert.Equal("First time this fortnight", WhyLine(Person(p => p.PrimaryType = "Produce"), NoPins, TypeCounts));
        Assert.Equal("Helps where needed", WhyLine(Person(p => p.PrimaryType = null), NoPins, TypeCounts));
        // A pin to a type with nothing on today's truck is set aside by the planner.
        Assert.Equal("Pinned to Bulky, none on the truck", WhyLine(Person(), new Dictionary<string, string> { ["e1"] = "Bulky" }, TypeCounts));
        Assert.False(IsPinned(Person(), new Dictionary<string, string> { ["e1"] = "Bulky" }));
        Assert.True(IsPinned(Person(), new Dictionary<string, string> { ["e1"] = "Short Life" }));
    }

    [Fact]
    public void Marks_the_last_to_finish_on_the_minute_shown()
    {
        var plan = new LoadPlan([], 390, 538.8, 1);
        Assert.True(IsLast(Person(p => p.FinishMin = 538.8), plan));
        Assert.True(IsLast(Person(p => p.FinishMin = 538.6), plan)); // both 08:59
        Assert.False(IsLast(Person(p => p.FinishMin = 537), plan));
    }

    [Fact]
    public void Says_how_far_ahead_of_or_behind_the_plan_it_was_in_whole_minutes()
    {
        Assert.Equal("7 minutes ahead of the plan's 08:58", AgainstPlan(538, 531));
        Assert.Equal("12 minutes behind the plan's 08:59", AgainstPlan(538.8, 551));
        Assert.Equal("1 minute ahead of the plan's 08:58", AgainstPlan(538, 537));
        Assert.Equal("right on the plan's 08:58", AgainstPlan(538.2, 538));
        Assert.Equal("7 min ahead", AgainstPlanShort(538, 531));
        Assert.Equal("12 min behind", AgainstPlanShort(538, 550));
        Assert.Equal("on time", AgainstPlanShort(538, 538));
    }

    private static readonly LoadPlan Plan29 = new([], 390, 538.8, 29);

    [Fact]
    public void Spans_whole_quarter_hours_around_the_work_and_the_time_now()
    {
        Assert.Equal(new TimelineSpan(390, 555, 425), TimelineSpanFor(Plan29, 425));
        // Before the unload starts, the span opens at the time now.
        Assert.Equal(new TimelineSpan(375, 555, 380), TimelineSpanFor(Plan29, 380));
        // Running late: the span stretches to the time now.
        Assert.Equal(new TimelineSpan(390, 570, 560), TimelineSpanFor(Plan29, 560));
    }

    [Fact]
    public void Leaves_the_time_now_off_when_it_is_far_from_the_work()
    {
        Assert.Equal(new TimelineSpan(390, 555, null), TimelineSpanFor(Plan29, 4 * 60));
        Assert.Equal(new TimelineSpan(390, 555, null), TimelineSpanFor(Plan29, 15 * 60));
        Assert.Equal(new TimelineSpan(390, 555, null), TimelineSpanFor(Plan29, null));
    }

    [Fact]
    public void Labels_quarter_hours_or_half_hours_and_hours_on_a_long_day()
    {
        Assert.Equal(15, TickStep(390, 555));
        Assert.Equal(30, TickStep(390, 750));
        Assert.Equal(60, TickStep(300, 900));
        Assert.Equal("7:00", TickLabel(420));
        Assert.Equal(":15", TickLabel(435));
    }

    [Fact]
    public void Keeps_the_axis_tags_apart_and_inside_the_lane()
    {
        Assert.True(FinishTagFits(390, 555, 425, 538.8));
        Assert.False(FinishTagFits(390, 555, 530, 538.8));
        Assert.True(FinishTagFits(390, 555, null, 538.8));
        Assert.Equal(new TagPlace("50%", "translateX(-50%)"), TagPlaceFor(50));
        Assert.Equal(new TagPlace("2%", "translateX(0)"), TagPlaceFor(2));
        Assert.Equal(new TagPlace("97%", "translateX(-100%)"), TagPlaceFor(97));
    }

    [Fact]
    public void Writes_the_day_in_full_on_the_kiosk_and_short_on_a_phone()
    {
        Assert.Equal("Tuesday 24 November", LongDay(new DateOnly(2026, 11, 24)));
        Assert.Equal("Tue 24 Nov", ShortDay(new DateOnly(2026, 11, 24)));
    }
}
