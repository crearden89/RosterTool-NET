// New: the Rostering Rules actions (src/app/(app)/admin/rules/rostering/actions.ts) - their
// checks, and what they write, against the real model on SQLite.

using Roster.Data.Entities;
using Roster.Web.Features.Rules;
using Roster.Web.Features.Rules.Rostering;

namespace Roster.Web.Tests.Rules;

public class RosteringRulesTests
{
    [Fact]
    public void An_override_key_is_trimmed_and_at_most_64_characters()
    {
        var trimmed = RosteringRulesChecks.CheckOverride("  myRule\t", 1);
        Assert.Equal("myRule", trimmed.Key);
        Assert.Null(trimmed.Error);
        Assert.Equal("myRule", RosteringRulesChecks.CheckOverride("﻿myRule", 1).Key);
        Assert.Equal("Enter a rule key (max 64 chars).", RosteringRulesChecks.CheckOverride("   ", 1).Error);
        Assert.Null(RosteringRulesChecks.CheckOverride(new string('k', 64), 1).Error);
        Assert.Equal("Enter a rule key (max 64 chars).", RosteringRulesChecks.CheckOverride(new string('k', 65), 1).Error);
        Assert.Equal("Enter a numeric value.", RosteringRulesChecks.CheckOverride("k", double.NaN).Error);
        Assert.Equal("Enter a numeric value.", RosteringRulesChecks.CheckOverride("k", double.PositiveInfinity).Error);
    }

    [Fact]
    public void Pay_thresholds_are_positive_up_to_200_or_blank()
    {
        Assert.Null(RosteringRulesChecks.CheckPayRules([new("SM", null, null, null), new("SA", 9, 76, 10), new("RSM", 200, 0.5, 1)]));
        Assert.Equal("SA: thresholds must be positive (or blank for off).",
            RosteringRulesChecks.CheckPayRules([new("SM", null, null, null), new("SA", 9, 0, 10)]));
        Assert.Equal("JSA: thresholds must be positive (or blank for off).", RosteringRulesChecks.CheckPayRules([new("JSA", 200.5, null, null)]));
        Assert.Equal("DSM: thresholds must be positive (or blank for off).", RosteringRulesChecks.CheckPayRules([new("DSM", null, null, double.NaN)]));
    }

    [Fact]
    public async Task Load_lists_overrides_by_key_and_pay_rules_in_role_order()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db =>
        {
            db.RosteringRules.Add(new RosteringRule { Key = "paidBreakFactorPct", Value = 4.5, Note = "seeded" });
            db.RosteringRules.Add(new RosteringRule { Key = "minPieceMin", Value = 195 });
            foreach (var role in new[] { "SA", "JSA", "SM" })
                db.BreakRules.Add(new BreakRule { Role = role, MinShiftHours = 3, MaxShiftHours = 9, GapHours = 10, BreakType = "tiered", Ot15AfterHours = role == "SM" ? null : 9 });
        });
        var data = await new RosteringRulesService(t, t.Scope, t.Clock).LoadAsync();

        Assert.Equal(["minPieceMin", "paidBreakFactorPct"], data.Overrides.Select(o => o.Key));
        Assert.Equal(new RuleOverrideRow("paidBreakFactorPct", 4.5, "seeded"), data.Overrides[1]);
        Assert.Equal(["SM", "SA", "JSA"], data.PayRules.Select(p => p.Role));
        Assert.Equal(new PayRuleInput("SM", null, null, null), data.PayRules[0]);
        Assert.Equal(9, data.PayRules[1].OtShiftAfterHours);
    }

    [Fact]
    public async Task SetRuleOverride_upserts_by_the_trimmed_key_and_replaces_the_note()
    {
        using var t = new RulesTestDatabase();
        var service = new RosteringRulesService(t, t.Scope, t.Clock);

        Assert.Equal(SaveResult.Success, await service.SetRuleOverrideAsync(" myRule ", 3, "for later"));
        var row = t.Read(db => db.RosteringRules.Single());
        Assert.Equal("myRule", row.Key);
        Assert.Equal(3, row.Value);
        Assert.Equal("for later", row.Note);
        Assert.Equal(RulesTestDatabase.Now, row.UpdatedAt);

        // The built-in rows save with no note, which clears one.
        Assert.True((await service.SetRuleOverrideAsync("myRule", 4)).Ok);
        row = t.Read(db => db.RosteringRules.Single());
        Assert.Equal(4, row.Value);
        Assert.Null(row.Note);

        Assert.Equal(SaveResult.Fail("Enter a rule key (max 64 chars)."), await service.SetRuleOverrideAsync("", 1));
        Assert.Equal(1, t.Read(db => db.RosteringRules.Count()));
    }

    [Fact]
    public async Task ClearRuleOverride_removes_the_row()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db => db.RosteringRules.Add(new RosteringRule { Key = "restGapMin", Value = 600 }));
        var service = new RosteringRulesService(t, t.Scope, t.Clock);

        Assert.True((await service.ClearRuleOverrideAsync("restGapMin")).Ok);
        Assert.Equal(0, t.Read(db => db.RosteringRules.Count()));
        // Nothing to remove is not an error.
        Assert.True((await service.ClearRuleOverrideAsync("restGapMin")).Ok);
    }

    [Fact]
    public async Task SavePayRules_writes_only_the_three_thresholds()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db => db.BreakRules.Add(new BreakRule
        {
            Role = "SA", MinShiftHours = 3, MaxShiftHours = 9, GapHours = 10, BreakType = "tiered",
            WeeklyCap = 38, Ot15AfterHours = 9, Ot19AfterHours = 11, OtPeriodAfterHours = 76, DoubleTimeUnderGapHours = 10,
        }));
        var service = new RosteringRulesService(t, t.Scope, t.Clock);

        // A role with no row is skipped, as an update by role would be.
        Assert.True((await service.SavePayRulesAsync([new("SA", 9.5, null, 8), new("RSM", 9, 76, 10)])).Ok);
        var sa = t.Read(db => db.BreakRules.Single());
        Assert.Equal(9.5, sa.Ot15AfterHours);
        Assert.Null(sa.OtPeriodAfterHours);
        Assert.Equal(8, sa.DoubleTimeUnderGapHours);
        Assert.Equal(38, sa.WeeklyCap);
        Assert.Equal(11, sa.Ot19AfterHours);
        Assert.Equal(9, sa.MaxShiftHours);
        Assert.Equal(1, t.Read(db => db.BreakRules.Count()));

        Assert.Equal(SaveResult.Fail("SA: thresholds must be positive (or blank for off)."), await service.SavePayRulesAsync([new("SA", -1, null, null)]));
        Assert.Equal(9.5, t.Read(db => db.BreakRules.Single().Ot15AfterHours));
    }

    [Fact]
    public async Task Only_a_super_user_may_read_or_change_the_rules()
    {
        using var t = new RulesTestDatabase(Tier.Store);
        var service = new RosteringRulesService(t, t.Scope, t.Clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetRuleOverrideAsync("k", 1));
    }
}
