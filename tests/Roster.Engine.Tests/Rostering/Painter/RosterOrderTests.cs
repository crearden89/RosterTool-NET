// New: order.ts and priority.ts have no TypeScript tests; these pin their
// documented behaviour (normalisation, warnings, the priority fallback).

using Roster.Engine.Rostering.Painter;

namespace Roster.Engine.Tests.Rostering.Painter;

public class RosterOrderTests
{
    [Fact]
    public void An_empty_or_missing_order_is_the_default()
    {
        Assert.Same(RosterOrder.DefaultOrder, RosterOrder.NormaliseOrder(null));
        Assert.Same(RosterOrder.DefaultOrder, RosterOrder.NormaliseOrder([]));
        Assert.Equal(
            ["ALLOCATE_SM", "ALLOCATE_ASM", "ALLOCATE_DSM", "FILL_FLOOR", "SHUFFLE_JSA", "CONTRACT_CHECK"],
            RosterOrder.DefaultOrder.Select(s => s.Type));
        Assert.Equal([5, 6, 1, 4, 0, 2, 3], RosterOrder.DefaultOrder[3].DayOrder!);
    }

    [Fact]
    public void Drops_unknown_steps_and_keeps_only_a_permutation_of_the_week()
    {
        var steps = RosterOrder.NormaliseOrder(
        [
            new("NOPE"),
            new("FILL_FLOOR", [6, 5, 4, 3, 2, 1, 0]),
            new("FILL_FLOOR", [0, 1, 2, 3, 4, 5, 5]),
            new("ALLOCATE_SM", [0, 1, 2]),
        ]);
        Assert.Equal(["FILL_FLOOR", "FILL_FLOOR", "ALLOCATE_SM"], steps.Select(s => s.Type));
        Assert.Equal([6, 5, 4, 3, 2, 1, 0], steps[0].DayOrder!);
        Assert.Null(steps[1].DayOrder);
        Assert.Null(steps[2].DayOrder);
        Assert.Same(RosterOrder.DefaultOrder, RosterOrder.NormaliseOrder([new("NOPE")]));
    }

    [Fact]
    public void The_default_order_has_no_warnings()
    {
        Assert.Empty(RosterOrder.ValidateOrder(RosterOrder.DefaultOrder));
    }

    [Fact]
    public void Warns_about_missing_and_misplaced_steps()
    {
        Assert.Equal(
            [
                "No 'Fill the floor' step: store-assistant and junior shifts will be left unfilled.",
                "No 'Shuffle + juniors' step: the fortnight repair/rescue pass will not run, leaving contract shortfalls and open shifts the shuffle would have fixed.",
                "No 'Contract check' step: under-contract flags will not be raised on the roster.",
            ],
            RosterOrder.ValidateOrder([new("ALLOCATE_SM")]));
        Assert.Equal(
            [
                "'ASM opposite / covering the SM' runs AFTER the floor fill - the floor may consume the days that cohort needed for manager cover.",
                "'Shuffle + juniors' runs BEFORE the floor fill - it will have nothing to shuffle.",
            ],
            RosterOrder.ValidateOrder(
                [new("ALLOCATE_SM"), new("SHUFFLE_JSA"), new("FILL_FLOOR"), new("ALLOCATE_ASM"), new("CONTRACT_CHECK")]));
        Assert.Contains(
            "No manager allocation steps: manager shifts are only picked up during the floor fill, so open/close cover may land poorly.",
            RosterOrder.ValidateOrder([new("FILL_FLOOR"), new("SHUFFLE_JSA"), new("CONTRACT_CHECK")]));
    }

    [Fact]
    public void Every_step_type_has_its_meta()
    {
        Assert.Equal(OrderStepTypes.All, RosterOrder.StepMeta.Select(m => m.Type));
        Assert.Equal("Fill the floor", RosterOrder.StepMetaFor("FILL_FLOOR")!.Label);
        Assert.Null(RosterOrder.StepMetaFor("NOPE"));
    }

    [Fact]
    public void Task_priority_keeps_the_configured_order_and_appends_what_is_missing()
    {
        Assert.Equal(
            ["cleaning", "admin", "checkout", "load_running", "reset", "specials"],
            TaskPriority.FromConfig(["cleaning", "bogus", null, "admin"]));
        Assert.Equal(["checkout", "load_running", "reset", "admin", "specials", "cleaning"], TaskPriority.FromConfig(null));
        Assert.Equal(["checkout", "load_running", "reset", "admin", "specials", "cleaning"], TaskPriority.FromConfig([]));
    }
}
