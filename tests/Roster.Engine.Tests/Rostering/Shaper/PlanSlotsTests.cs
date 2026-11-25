// New: planSlots.ts has no test file of its own; these pin its pure helpers.

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Shaper;

public class PlanSlotsTests
{
    private static BreakRule Rule(double min, double max, string type) => new() { MinShiftHours = min, MaxShiftHours = max, BreakType = type };

    [Fact]
    public void Demand_from_stage1_sums_each_quarter_hour_and_counts_non_op_as_cover_only()
    {
        var demand = PlanSlots.DemandFromStage1(
        [
            new PlannedBlock("checkout", 495, 2),
            new PlannedBlock("load_running", 480, 3),
            new PlannedBlock("non_op", 480, 1),
            new PlannedBlock("admin", 480, 1),
            new PlannedBlock("load_running", 480, 1),
        ]);
        Assert.Equal([480.0, 495.0], demand.Select(d => d.Time));
        Assert.Equal(6, demand[0].Total);
        Assert.Equal(["load_running", "admin"], demand[0].Groups.Keys());
        Assert.Equal(4, demand[0].GroupCount("load_running"));
        Assert.Equal(0, demand[0].GroupCount("non_op"));
        Assert.Equal(2, demand[1].Total);
    }

    [Fact]
    public void A_manager_slot_fits_every_manager_who_might_fill_it()
    {
        var rules = new Dictionary<string, BreakRule>
        {
            ["SM"] = Rule(3, 12, "flat_30_unpaid"),
            ["ASM"] = Rule(4, 12, "flat_30_unpaid"),
            ["DSM"] = Rule(3, 9, "flat_30_unpaid"),
            ["RSM"] = Rule(6, 5, "tiered"), // emergency cover: never shapes the slot
        };
        Assert.Equal(new ShaperRule(4, 9, "flat_30_unpaid"), PlanSlots.ManagerSlotRule(rules));
        Assert.Equal(new ShaperRule(3, 12), PlanSlots.ManagerSlotRule(new Dictionary<string, BreakRule>()));
    }

    [Fact]
    public void The_day_check_holds_each_slot_type_to_its_longest_legal_shift()
    {
        var rules = new Dictionary<string, BreakRule>
        {
            ["ASM"] = Rule(3, 9, "flat_30_unpaid"),
            ["SA"] = Rule(3, 9, "tiered"),
            ["JSA"] = Rule(3, 6, "tiered"),
        };
        var opts = PlanSlots.DayCheckOptions(rules, schoolDay: false, new Dictionary<string, double> { ["minPeopleOnFloor"] = 3 });
        Assert.Equal(570, opts.MaxClock!["MGR"]);
        Assert.Equal(570, opts.MaxClock["SA"]);
        Assert.Equal(360, opts.MaxClock["JSA"]); // 6.5h on the clock, capped at the junior's 6
        Assert.Equal(3, opts.Cover!.MinPeopleOnFloor);
        Assert.Equal(180, opts.MinShiftMin);
        Assert.Equal(360, opts.ManagerMinShiftMin);
        Assert.False(opts.IsSchoolDay);
        Assert.Equal(300, PlanSlots.DayCheckOptions(rules, schoolDay: true, null).MaxClock!["JSA"]); // 5h on a school day
    }

    [Fact]
    public void Fixed_shifts_take_the_slot_type_of_the_persons_role()
    {
        Assert.Equal("JSA", PlanSlots.SlotRoleForEmployee("JSA"));
        foreach (var r in new[] { "SM", "ASM", "DSM", "RSM" }) Assert.Equal("MGR", PlanSlots.SlotRoleForEmployee(r));
        Assert.Equal("SA", PlanSlots.SlotRoleForEmployee("SA"));
    }

    [Fact]
    public void A_tiered_manager_is_an_active_RSM()
    {
        Assert.True(PlanSlots.HasTieredManager([("SM", true), ("RSM", true)]));
        Assert.False(PlanSlots.HasTieredManager([("SM", true), ("RSM", false), ("SA", true)]));
    }

    [Fact]
    public void A_day_with_no_data_has_no_slots()
    {
        var s1 = new Stage1Computation { HasData = false };
        var day = PlanSlots.ComputeDaySlots(new DaySlotsSources { Stage1 = s1 });
        Assert.Null(day.Shaped);
        Assert.Same(s1, day.Stage1);
        Assert.Null(day.Painted);
    }

    [Fact]
    public void Paints_the_slots_as_their_engine_roles_and_keeps_fixed_shifts_fixed()
    {
        var s1 = new Stage1Computation
        {
            HasData = true,
            StaffOpen = 8 * 60,
            StaffClose = 17 * 60,
            TradingOpen = 8 * 60,
            TradingClose = 17 * 60,
            Windows = [new WindowRule("cleaning", 8 * 60, 17 * 60)],
        };
        var shaped = new ShapedDay
        {
            Slots =
            [
                new PlanSlot("mgr-open", "MGR", 8 * 60, 14 * 60),
                new PlanSlot("fixed-1", "SA", 8 * 60, 17 * 60),
                new PlanSlot("jsa-1", "JSA", 13 * 60, 17 * 60),
            ],
            DemandByBlock = ShaperTests.Flat(8 * 60, 17 * 60, 2),
        };
        var p = PlanSlots.PaintSlots(shaped, s1, TaskGroups.TaskPriority);
        Assert.Equal(["ASM", "SA", "JSA"], p.Painted.Select(x => x.Role));
        Assert.Equal([false, true, false], p.Painted.Select(x => x.Fixed));
        Assert.Same(shaped.Slots[1], p.Painted[1].Slot);
        Assert.Equal("RSM", PlanSlots.PaintSlots(shaped, s1, TaskGroups.TaskPriority, null, "RSM").Painted[0].Role);
    }
}
