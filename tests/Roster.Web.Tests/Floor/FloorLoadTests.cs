// Port of src/app/floor/[token]/floorLoad.test.ts.

using System.Text.RegularExpressions;
using Bunit;
using Roster.Engine.Core;
using Roster.Engine.Load;
using Roster.Web.Features.Floor;

namespace Roster.Web.Tests.Floor;

public class FloorLoadTests
{
    private static LoadTypeRow Type(string id, string name, double minutes, double priority = 100) =>
        new(id, name, minutes, minutes + 2, minutes + 6, minutes + 10, $"#{id}{id}{id}", priority);

    private static readonly DateOnly Day = new(2026, 11, 20);

    private static readonly LoadInputs Base = new(
        Day,
        [
            new("s1", "e1", "Hannah Lee"),
            new("s2", "e2", "Liam Nguyen"),
            new("s3", "e3", "Mia Rossi"), // no Load Running
            new("s4", null, "Open shift"),
        ],
        new Dictionary<string, double> { ["s1"] = 7 * 60, ["s2"] = 7 * 60 + 15, ["s4"] = 7 * 60 },
        new Dictionary<string, DateOnly?>
        {
            ["e1"] = new DateOnly(2020, 1, 1),
            ["e2"] = new DateOnly(2026, 11, 1), // a new starter: slower pallets
        },
        [Type("1", "Ambient", 19, 5), Type("2", "Freezer", 22, 2), Type("3", "Bread", 10)],
        [new("1", 6), new("2", 2), new("3", 0)],
        []);

    private static readonly List<LoadCrewMember> PlanCrew =
    [
        new("e1", "Hannah Lee", 420, TenureBands.Gt90),
        new("e2", "Liam Nguyen", 435, TenureBands.Lt30),
    ];

    private static readonly List<LoadItem> PlanItems =
    [
        new() { Type = "Ambient", Color = "#111", Qty = 6, Priority = 5, Minutes = new PalletMinutes(19, 21, 25, 29) },
        new() { Type = "Freezer", Color = "#222", Qty = 2, Priority = 2, Minutes = new PalletMinutes(22, 24, 28, 32) },
    ];

    [Fact]
    public void Plans_the_truck_as_the_Load_page_does_same_crew_bands_and_allocator()
    {
        var s = FloorLoad.SummariseLoad(Base);
        var plan = LoadAllocator.AllocateLoad(PlanCrew, PlanItems);
        Assert.True(s.Entered);
        Assert.Equal(8, s.Total);
        Assert.Equal([new LoadStripItem("Ambient", "#111", 6), new LoadStripItem("Freezer", "#222", 2)], s.Items);
        Assert.Equal(2, s.Crew);
        Assert.Equal(plan.Crew.Count(c => c.Segments.Count > 0), s.OnLoad);
        Assert.Equal(plan.TeamFinish, s.TeamFinish);
    }

    [Fact]
    public void Honours_todays_pins_and_the_fortnights_rotation_as_the_Load_page_does()
    {
        var history = new List<LoadHistoryRow>
        {
            new(Day, "e2", "Freezer", true),
            new(Day, "e1", "Ambient", false), // today's own auto row: ignored
            new(new DateOnly(2026, 11, 19), "e1", "Freezer", false),
        };
        var s = FloorLoad.SummariseLoad(Base with { History = history });
        var plan = LoadAllocator.AllocateLoad(PlanCrew, PlanItems, new AllocateOptions
        {
            Pinned = new JsMap<string, string>([("e2", "Freezer")]),
            TypeCounts = new JsMap<string, double>([("e1:Freezer", 1.0)]),
        });
        Assert.Equal(plan.TeamFinish, s.TeamFinish);
    }

    [Fact]
    public void Says_no_load_has_been_entered_when_there_is_no_entry_for_the_day()
    {
        var s = FloorLoad.SummariseLoad(Base with { Loads = [] });
        Assert.Equal((false, 0.0, 0, 2, 0, (double?)null), (s.Entered, s.Total, s.Items.Count, s.Crew, s.OnLoad, s.TeamFinish));
    }

    [Fact]
    public void Counts_the_pallets_but_plans_no_finish_without_anyone_on_Load_Running()
    {
        var s = FloorLoad.SummariseLoad(Base with { Shifts = [] });
        Assert.True(s.Entered);
        Assert.Equal(8, s.Total);
        Assert.Equal(0, s.Crew);
        Assert.Null(s.TeamFinish);
    }

    [Fact]
    public void Has_no_finish_for_an_entry_of_all_zeros()
    {
        var s = FloorLoad.SummariseLoad(Base with { Loads = [new("1", 0)] });
        Assert.Equal((true, 0.0, 0, (double?)null), (s.Entered, s.Total, s.Items.Count, s.TeamFinish));
    }

    // ── the Load Planner strip with a new starter ──
    // A new starter's pallets are priced from their tenure band's minutes,
    // which carry a decimal (26.6 a pallet), so the plan's finish does too.
    private static readonly LoadInputs NewStarter = new(
        Day,
        [new("s1", "e1", "Hannah Lee"), new("s2", "e2", "Liam Nguyen")],
        new Dictionary<string, double> { ["s1"] = 7 * 60, ["s2"] = 7 * 60 },
        new Dictionary<string, DateOnly?>
        {
            ["e1"] = new DateOnly(2020, 1, 1),
            ["e2"] = new DateOnly(2026, 11, 1), // day 20: under 30 days
        },
        [new("1", "Ambient", 19, 20.9, 23.8, 26.6, "#2563eb", 5)],
        [new("1", 4)],
        []);

    [Fact]
    public void Keeps_the_plans_decimal_finish_but_prints_it_to_the_whole_minute()
    {
        var s = FloorLoad.SummariseLoad(NewStarter);
        // 07:05 after the stretch, then two pallets at 26.6 minutes each.
        Assert.Equal(478.2, s.TeamFinish!.Value, 5);
        Assert.NotEqual(Math.Floor(s.TeamFinish.Value), s.TeamFinish.Value);
        using var ctx = new BunitContext();
        var html = ctx.Render<LoadStrip>(p => p.Add(x => x.Load, s).Add(x => x.OtherDay, false).Add(x => x.Href, "/load/t")).Markup;
        Assert.Contains("<b>07:58</b>", html);
        Assert.DoesNotMatch(new Regex(@"\d:\d*\.\d"), html);
    }
}
