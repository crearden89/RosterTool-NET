// Port of src/lib/stage2/heldPlan.test.ts (its "held plan -> roster input"
// cases; the two "roster from the held plan" cases run the production
// pipeline over the in-memory store and belong to the data layer's harness).

using Roster.Engine.Core;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class HeldPlanTests
{
    private static List<string> Shape(IEnumerable<DemandBlock> demand) =>
        demand.Select(d => $"{d.Time} {d.Total} {{{string.Join(",", d.Groups.Entries().Select(g => $"{g.Key}:{g.Value}"))}}}").ToList();

    [Fact]
    public void Counts_the_plans_painted_work_as_demand_checkouts_display_split_rejoins_NonOp_is_headcount_only_breaks_arent_work()
    {
        var demand = HeldPlan.DemandFromPlanShifts(new List<List<ITile>>
        {
            new() { new DisplayBlock(600, "registers"), new DisplayBlock(615, "break"), new DisplayBlock(630, "admin") },
            new() { new DisplayBlock(600, "scos"), new DisplayBlock(615, "non_op"), new DisplayBlock(630, "flex") },
        });
        Assert.Equal(["600 2 {checkout:2}", "615 1 {}", "630 1 {admin:1}"], Shape(demand));
    }

    [Fact]
    public void Turns_each_planned_shift_into_a_slot_to_fill_and_a_day_without_a_plan_into_nothing_to_fill()
    {
        var result = new Stage1Computation { HasData = true };
        var plan = new PlanShiftComputation
        {
            HasData = true,
            TradingOpen = 480,
            TradingClose = 1200,
            Flags = ["Very short day - both manager shifts cover the whole span"],
            Shifts = [new PlanShift { SlotId = "mgr-open", PlanRole = "MGR", Start = 390, End = 870, BreakMinutes = 30, UnpaidBreakMinutes = 30, Blocks = [] }],
        };
        var date = new DateOnly(2026, 8, 3);
        var shaped = HeldPlan.SlotsFromHeldPlan(new HeldPlanDay(date, result, plan)).Shaped;
        Assert.NotNull(shaped);
        Assert.Equal([("mgr-open", "MGR", 390.0, 870.0)], shaped.Slots.Select(s => (s.Id, s.Role, s.Start, s.End)));
        Assert.Empty(shaped.DemandByBlock);
        Assert.Equal(["Very short day - both manager shifts cover the whole span"], shaped.Flags);
        Assert.Null(shaped.BreakSlack);
        Assert.Null(HeldPlan.SlotsFromHeldPlan(new HeldPlanDay(date, result, null)).Shaped);
        Assert.Null(HeldPlan.SlotsFromHeldPlan(new HeldPlanDay(date, new Stage1Computation { HasData = false }, plan)).Shaped);
    }
}
