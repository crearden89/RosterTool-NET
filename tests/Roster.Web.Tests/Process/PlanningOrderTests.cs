// New: the Planning Order actions (src/app/(app)/admin/process/planning/actions.ts) and the
// reads in its page.tsx, against the real model on SQLite.

using Roster.Data.Entities;
using Roster.Engine.Planning.Plan;
using Roster.Web.Features.Process.Planning;
using Roster.Web.Features.Rules;
using Roster.Web.Tests.Rules;

namespace Roster.Web.Tests.Process;

public class PlanningOrderTests
{
    private static PlacementRule Rule(string group, string strategy = "spread") => new() { Group = group, Strategy = strategy };

    private static void SeedSettings(RulesTestDatabase t, Action<PlanPlacementSettings>? change = null) => t.Seed(db =>
    {
        var s = new PlanPlacementSettings();
        change?.Invoke(s);
        db.PlanPlacementSettings.Add(s);
    });

    [Fact]
    public void CheckRules_refuses_unknown_values_and_a_group_twice()
    {
        Assert.Null(PlanningOrderChecks.CheckRules(Placement.DefaultRules));
        Assert.Equal("Unknown labour group reset", PlanningOrderChecks.CheckRules([Rule("reset")]));
        Assert.Equal("Unknown strategy sideways", PlanningOrderChecks.CheckRules([Rule("admin", "sideways")]));
        Assert.Equal("Unknown anchor kind noon", PlanningOrderChecks.CheckRules([Rule("admin") with { AnchorKind = "noon" }]));
        Assert.Equal("Unknown night-trader strategy late", PlanningOrderChecks.CheckRules([Rule("admin") with { NightTraderStrategy = "late" }]));
        Assert.Null(PlanningOrderChecks.CheckRules([Rule("admin") with { NightTraderStrategy = "" }]));
        Assert.Equal("cleaning appears more than once - each group needs one rule.",
            PlanningOrderChecks.CheckRules([Rule("cleaning"), Rule("admin"), Rule("cleaning", "backfill")]));
    }

    [Fact]
    public void Stored_values_are_clamped_as_the_action_clamps_them()
    {
        Assert.Null(PlanningOrderChecks.Pct(null));
        Assert.Null(PlanningOrderChecks.Pct(double.NaN));
        Assert.Equal(100, PlanningOrderChecks.Pct(140));
        Assert.Equal(0, PlanningOrderChecks.Pct(-5));
        Assert.Equal(1, PlanningOrderChecks.SmoothingBlocks(0));
        Assert.Equal(16, PlanningOrderChecks.SmoothingBlocks(40));
        Assert.Equal(3, PlanningOrderChecks.SmoothingBlocks(2.5));
        Assert.Equal(1, PlanningOrderChecks.ThresholdPct(0));
        Assert.Equal(new TimeOnly(17, 0), PlanningOrderChecks.SqlTime(1020).Time);
        Assert.Equal("invalid input syntax for type time: \"NaN:NaN:00\"", PlanningOrderChecks.SqlTime(double.NaN).Error);
    }

    [Fact]
    public void The_step_order_has_every_step_once()
    {
        Assert.Equal(["cleaning", "admin", "load_running", "reset", "specials"],
            PlanningOrderChecks.CheckStepOrder(["cleaning", "checkout", "admin", "load_running", "reset", "specials"]).Order);
        Assert.Equal("A group appears twice in the order.", PlanningOrderChecks.CheckStepOrder(["admin", "admin", "reset", "specials", "cleaning"]).Error);
        Assert.Equal("Every step must appear exactly once.", PlanningOrderChecks.CheckStepOrder(["admin", "reset"]).Error);
        // The page reads the saved order, then appends anything it lacks.
        Assert.Equal(["cleaning", "specials", "admin", "load_running", "reset"], PlanningOrderChecks.StepOrderFrom(["cleaning", "bogus", "specials"]));
        Assert.Equal(["admin", "load_running", "reset", "specials", "cleaning"], PlanningOrderChecks.StepOrderFrom(null));
    }

    [Fact]
    public async Task Load_reads_rules_by_position_settings_and_step_order()
    {
        using var t = new RulesTestDatabase();
        SeedSettings(t, s =>
        {
            s.Enabled = true;
            s.NightTraderFrom = new TimeOnly(18, 15);
            s.StepOrder = ["cleaning", "admin"];
        });
        t.Seed(db =>
        {
            db.PlanPlacementRules.Add(new PlanPlacementRule { Position = 1, LabourGroup = "cleaning", Strategy = "backfill", AnchorKind = "clock", AnchorTime = new TimeOnly(20, 0) });
            db.PlanPlacementRules.Add(new PlanPlacementRule { Position = 0, LabourGroup = "checkout", Strategy = "follow_demand", PrePeakPct = 40 });
        });

        var data = await new PlanningOrderService(t, t.Scope, t.Clock).LoadAsync();

        Assert.Equal(["checkout", "cleaning"], data.Saved.Select(r => r.Group));
        Assert.Equal(1200, data.Saved[1].AnchorTime);
        Assert.Equal(40, data.Saved[0].PrePeakPct);
        Assert.True(data.Settings.Enabled);
        Assert.Equal(1095, data.Settings.NightTraderFromMin);
        Assert.Equal(["cleaning", "admin", "load_running", "reset", "specials"], data.StepOrder);
    }

    [Fact]
    public async Task Load_without_a_settings_row_shows_the_defaults()
    {
        using var t = new RulesTestDatabase();
        var data = await new PlanningOrderService(t, t.Scope, t.Clock).LoadAsync();
        Assert.Empty(data.Saved);
        Assert.Equal(Placement.DefaultSettings, data.Settings);
        Assert.Equal(["admin", "load_running", "reset", "specials", "cleaning"], data.StepOrder);
    }

    [Fact]
    public async Task SavePlacement_replaces_the_rules_and_never_touches_the_switch()
    {
        using var t = new RulesTestDatabase();
        SeedSettings(t);
        t.Seed(db => db.PlanPlacementRules.Add(new PlanPlacementRule { Position = 0, LabourGroup = "specials", Strategy = "spread" }));
        var service = new PlanningOrderService(t, t.Scope, t.Clock);
        PlacementRule[] rules =
        [
            Rule("cleaning", "backfill") with { AnchorKind = "clock", AnchorTime = 1230.4, AnchorOffsetMin = 14.5 },
            Rule("admin", "peak_split") with { PrePeakPct = 120, PostPeakPct = 30 },
        ];
        var settings = Placement.DefaultSettings with { Enabled = true, PeakBasis = "rolling", PeakSmoothingBlocks = 40, PeakThresholdPct = 0, NightTraderFromMin = 1050, NightTraderSharePct = 30 };

        Assert.Equal(SaveResult.Success, await service.SavePlacementAsync(rules, settings));

        var saved = t.Read(db => db.PlanPlacementRules.OrderBy(r => r.Position).ToList());
        Assert.Equal(["cleaning", "admin"], saved.Select(r => r.LabourGroup));
        Assert.Equal(new TimeOnly(20, 30), saved[0].AnchorTime);
        Assert.Equal(15, saved[0].AnchorOffsetMin);
        Assert.Equal(100, saved[1].PrePeakPct);
        Assert.Null(saved[1].AnchorTime);
        var s = t.Read(db => db.PlanPlacementSettings.Single());
        Assert.False(s.Enabled);
        Assert.Equal(("rolling", 16, 1.0, new TimeOnly(17, 30), 30.0), (s.PeakBasis, s.PeakSmoothingBlocks, s.PeakThresholdPct, s.NightTraderFrom, s.NightTraderSharePct));

        Assert.Equal(SaveResult.Fail("Unknown labour group reset"), await service.SavePlacementAsync([Rule("reset")], settings));
        Assert.True((await service.ResetPlacementAsync(settings)).Ok);
        Assert.Equal(0, t.Read(db => db.PlanPlacementRules.Count()));
    }

    [Fact]
    public async Task The_switch_and_the_step_order_save_on_their_own()
    {
        using var t = new RulesTestDatabase();
        SeedSettings(t);
        var service = new PlanningOrderService(t, t.Scope, t.Clock);

        Assert.True((await service.SetPlacementEnabledAsync(true)).Ok);
        Assert.True(t.Read(db => db.PlanPlacementSettings.Single().Enabled));

        Assert.True((await service.SaveStepOrderAsync(["reset", "admin", "load_running", "specials", "cleaning"])).Ok);
        Assert.Equal(["reset", "admin", "load_running", "specials", "cleaning"], t.Read(db => db.PlanPlacementSettings.Single().StepOrder));
        Assert.Equal(SaveResult.Fail("Every step must appear exactly once."), await service.SaveStepOrderAsync(["reset"]));
    }
}
