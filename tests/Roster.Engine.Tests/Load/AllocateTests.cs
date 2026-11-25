// Port of src/lib/load/allocate.test.ts.

using System.Text.Json;
using Roster.Engine.Core;
using Roster.Engine.Load;
using static Roster.Engine.Tests.Till.TestNumbers;

namespace Roster.Engine.Tests.Load;

public class AllocateTests
{
    private const double S = Allocate.StretchMinutes;

    // Pallets only - the mandatory warm-up leads every person's bar and is
    // asserted on its own below rather than repeated in every expectation.
    private static List<LoadSegment> Pallets(CrewSchedule c) => c.Segments.Where(s => !Allocate.IsStretch(s)).ToList();

    private static List<LoadCrewMember> Crew(params (string Name, double StartMin)[] members) =>
        members.Select(m => new LoadCrewMember(m.Name, m.Name, m.StartMin)).ToList();

    // Same minutes for every band unless a test says otherwise.
    private static LoadItem Load(string type, double minutes, double qty, double? priority = null) =>
        new(type, "#000", new PalletMinutes(minutes), qty, priority);

    [Fact]
    public void Splits_an_even_load_evenly()
    {
        var plan = Allocate.AllocateLoad(Crew(("Amy", 370), ("Ben", 370)), [Load("Ambient", 19, 4)]);
        Assert.Equal(370 + S + 38, plan.Crew[0].FinishMin);
        Assert.Equal(370 + S + 38, plan.Crew[1].FinishMin);
        Assert.Equal(370 + S + 38, plan.TeamFinish);
        Assert.Equal(4, plan.TotalPallets);
    }

    [Fact]
    public void Gives_a_later_starter_less_work_so_the_team_still_finishes_early()
    {
        // Amy starts 30min before Ben; 6x 10-min pallets: Amy should carry more.
        var plan = Allocate.AllocateLoad(Crew(("Amy", 360), ("Ben", 390)), [Load("Produce", 10, 6)]);
        var amy = plan.Crew.First(c => c.Name == "Amy");
        var ben = plan.Crew.First(c => c.Name == "Ben");
        Assert.True(amy.FinishMin - amy.StartMin > ben.FinishMin - ben.StartMin);
        Assert.True(Math.Abs(amy.FinishMin - ben.FinishMin) <= 10);
    }

    [Fact]
    public void Assigns_longest_pallets_first_for_a_tight_makespan()
    {
        // 1x 28min + 2x 14min across 2 people: one person takes the 28 and the
        // other both 14s - finish together at +28.
        var plan = Allocate.AllocateLoad(Crew(("A", 0), ("B", 0)), [Load("Long Life", 28, 1), Load("Half", 14, 2)]);
        Assert.Equal(S + 28, plan.TeamFinish);
    }

    [Fact]
    public void Merges_consecutive_pallets_of_the_same_type_into_one_segment()
    {
        var plan = Allocate.AllocateLoad(Crew(("Solo", 0)), [Load("Bread", 17, 3)]);
        var work = Pallets(plan.Crew[0]);
        Assert.Single(work);
        Assert.Equal(3, work[0].Qty);
        Assert.Equal(S + 51, work[0].End);
    }

    [Fact]
    public void Is_deterministic_regardless_of_input_order()
    {
        List<LoadItem> loads = [Load("Ambient", 19, 2), Load("Freezer", 28, 2), Load("Bulky", 5, 3)];
        var a = Allocate.AllocateLoad(Crew(("A", 0), ("B", 0)), loads);
        var b = Allocate.AllocateLoad(Crew(("B", 0), ("A", 0)), Enumerable.Reverse(loads).ToList());
        Assert.Equal(JsonSerializer.Serialize(a, RosterJson.Options), JsonSerializer.Serialize(b, RosterJson.Options));
    }

    [Fact]
    public void Handles_an_empty_crew_and_zero_quantities_gracefully()
    {
        Assert.Equal(0, Allocate.AllocateLoad([], [Load("Ambient", 19, 3)]).TotalPallets);
        var plan = Allocate.AllocateLoad(Crew(("A", 0)), [Load("Ambient", 19, 0)]);
        Assert.Equal(0, plan.TotalPallets);
        // No pallets, but they still warmed up - and the plan reports that window
        // rather than 00:00-00:00, which would read as broken.
        Assert.Empty(Pallets(plan.Crew[0]));
        Assert.Single(plan.Crew[0].Segments);
        Assert.Equal(S, plan.TeamFinish);
    }

    [Fact]
    public void Gives_the_biggest_category_the_extra_body()
    {
        var plan = Allocate.AllocateLoad(
            Crew(("A", 0), ("B", 0), ("C", 0), ("D", 0), ("E", 0)),
            [Load("Chiller", 10, 4), Load("Freezer", 10, 4), Load("Ambient", 10, 12), Load("Produce", 10, 4)]);
        var owners = plan.Crew.Where(c => c.PrimaryType is not null).GroupBy(c => c.PrimaryType!).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, owners["Ambient"]);
        Assert.Equal(1, owners["Chiller"]);
        Assert.Equal(1, owners["Freezer"]);
        Assert.Equal(1, owners["Produce"]);
    }

    [Fact]
    public void Works_a_category_end_to_end_before_starting_another()
    {
        // One person, two categories: they finish the first entirely, then the
        // second - never ping-ponging between the two.
        var solo = Allocate.AllocateLoad(Crew(("Solo", 0)), [Load("Freezer", 20, 2), Load("Ambient", 10, 2)]).Crew[0];
        var types = Pallets(solo).Select(s => s.Type).ToList();
        Assert.Equal(types.Count, types.Distinct().Count());
        Assert.All(Pallets(solo), s => Assert.Equal(2, s.Qty));
    }

    [Fact]
    public void Honours_a_manual_pin()
    {
        var plan = Allocate.AllocateLoad(
            Crew(("A", 0), ("B", 0)),
            [Load("Ambient", 10, 6), Load("Freezer", 10, 2)],
            new AllocateOptions { Pinned = new JsMap<string, string>().Set("B", "Ambient") });
        Assert.Equal("Ambient", plan.Crew.First(c => c.Key == "B").PrimaryType);
        Assert.Equal("Freezer", plan.Crew.First(c => c.Key == "A").PrimaryType);
    }

    [Fact]
    public void Rotates_the_person_who_owned_a_category_recently_loses_it()
    {
        // Without history A (earliest alphabetically, same start) would own Ambient.
        // A owned Ambient yesterday, so B gets it today.
        var plan = Allocate.AllocateLoad(
            Crew(("A", 0), ("B", 0)),
            [Load("Ambient", 10, 6), Load("Freezer", 10, 2)],
            new AllocateOptions { TypeCounts = new JsMap<string, double>().Set("A:Ambient", 1) });
        Assert.Equal("Ambient", plan.Crew.First(c => c.Key == "B").PrimaryType);
        Assert.Equal("Freezer", plan.Crew.First(c => c.Key == "A").PrimaryType);
    }

    [Fact]
    public void Sends_a_finished_owner_to_help_the_category_with_the_most_left()
    {
        var plan = Allocate.AllocateLoad(Crew(("A", 0), ("B", 0)), [Load("Ambient", 10, 8), Load("Freezer", 10, 2)]);
        Assert.Equal(10, plan.TotalPallets);
        var finishes = plan.Crew.Select(c => c.FinishMin).ToList();
        Assert.True(finishes.Max() - finishes.Min() <= 10);
        Assert.Contains(plan.Crew, c => Pallets(c).Select(s => s.Type).Distinct().Count() >= 2);
    }

    [Fact]
    public void Sends_a_finished_owner_to_the_highest_priority_outstanding_category_first()
    {
        // B clears Liquor while Freeze-to-Thaw (the time-critical pile) still has
        // pallets down: B must join Freeze-to-Thaw, not drift to Ambient.
        var plan = Allocate.AllocateLoad(Crew(("A", 0), ("B", 0)),
            [Load("Freeze to Thaw", 10, 4, 1), Load("Liquor", 10, 2, 2), Load("Ambient", 10, 8, 5)]);
        Assert.Contains("Freeze to Thaw", Pallets(plan.Crew.First(c => c.Name == "B")).Select(s => s.Type));
    }

    // 20 minutes trained, stretching to 28 in the first month - the figures a
    // manager types into Admin -> Load Types.
    private static readonly List<LoadItem> Banded = [new("Ambient", "#888", new PalletMinutes(20, 22, 25, 28), 4)];

    [Fact]
    public void Prices_a_new_starters_pallets_from_their_band()
    {
        var plan = Allocate.AllocateLoad(
            [new LoadCrewMember("vet", "Veteran", 6 * 60), new LoadCrewMember("new", "Newbie", 6 * 60, "lt30")], Banded);
        static double PerPallet(CrewSchedule c) => (c.FinishMin - c.StartMin - S) / c.Segments.Sum(s => s.Qty);
        Close(20, PerPallet(plan.Crew.First(c => c.Key == "vet")));
        Close(28, PerPallet(plan.Crew.First(c => c.Key == "new")));
    }

    [Fact]
    public void Costs_each_band_its_own_figure()
    {
        double Per(string? band)
        {
            var p = Allocate.AllocateLoad([new LoadCrewMember("a", "A", 360, band)], Banded);
            return (p.TeamFinish - p.TeamStart - S) / 4;
        }
        Close(20, Per("gt90"));
        Close(22, Per("lt90"));
        Close(25, Per("lt60"));
        Close(28, Per("lt30"));
    }

    [Fact]
    public void Pushes_the_team_finish_later()
    {
        var fast = Allocate.AllocateLoad([new LoadCrewMember("a", "A", 360)], Banded);
        var slow = Allocate.AllocateLoad([new LoadCrewMember("a", "A", 360, "lt30")], Banded);
        Assert.True(slow.TeamFinish > fast.TeamFinish);
    }

    [Fact]
    public void Treats_a_missing_band_as_fully_trained()
    {
        var b = Allocate.AllocateLoad([new LoadCrewMember("a", "A", 360)], Banded);
        var p = Allocate.AllocateLoad([new LoadCrewMember("a", "A", 360, null)], Banded);
        Assert.Equal(b.TeamFinish, p.TeamFinish);
    }

    [Fact]
    public void Falls_back_to_the_trained_figure_when_a_band_is_left_blank()
    {
        var p = Allocate.AllocateLoad([new LoadCrewMember("a", "A", 360, "lt30")], [new LoadItem("Ambient", "#888", new PalletMinutes(20), 4)]);
        Close(80, p.TeamFinish - p.TeamStart - S);
    }

    [Fact]
    public void Opens_every_persons_bar_with_the_warm_up()
    {
        var plan = Allocate.AllocateLoad(Crew(("Amy", 360), ("Ben", 390)), [Load("Ambient", 10, 4)]);
        foreach (var c in plan.Crew)
        {
            var first = c.Segments[0];
            Assert.True(Allocate.IsStretch(first));
            Assert.Equal(c.StartMin, first.Start);
            Assert.Equal(c.StartMin + Allocate.StretchMinutes, first.End);
            // It is time, not product: counting it as a pallet would inflate the load.
            Assert.Equal(0, first.Qty);
        }
    }

    [Fact]
    public void Starts_pallets_only_once_the_warm_up_is_done()
    {
        var plan = Allocate.AllocateLoad(Crew(("Solo", 360)), [Load("Ambient", 10, 2)]);
        Assert.Equal(360 + Allocate.StretchMinutes, Pallets(plan.Crew[0])[0].Start);
    }

    [Fact]
    public void Pushes_the_truck_away_later_by_the_warm_up()
    {
        var plan = Allocate.AllocateLoad(Crew(("Solo", 0)), [Load("Ambient", 10, 3)]);
        Assert.Equal(Allocate.StretchMinutes + 30, plan.TeamFinish);
    }

    [Fact]
    public void Carries_no_pallets_into_the_totals()
    {
        var plan = Allocate.AllocateLoad(Crew(("A", 0), ("B", 0)), [Load("Ambient", 10, 4)]);
        Assert.Equal(4, plan.TotalPallets);
        Assert.Equal(0, plan.Crew.SelectMany(c => c.Segments).Where(Allocate.IsStretch).Sum(s => s.Qty));
    }

    [Fact]
    public void Is_labelled_so_the_chart_can_tell_it_apart_from_a_load_type()
    {
        var plan = Allocate.AllocateLoad(Crew(("A", 0)), [Load("Ambient", 10, 1)]);
        Assert.Equal(Allocate.StretchType, plan.Crew[0].Segments[0].Type);
        Assert.False(Allocate.IsStretch("Ambient"));
    }
}
