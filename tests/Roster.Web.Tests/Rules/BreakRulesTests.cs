// New: the Break & Work Rules action (src/app/(app)/admin/rules/breaks/actions.ts) and the
// page's role order, against the real model on SQLite.

using Roster.Data.Entities;
using Roster.Web.Features.Rules;
using Roster.Web.Features.Rules.Breaks;

namespace Roster.Web.Tests.Rules;

public class BreakRulesTests
{
    private static BreakRule Row(string role, string breakType = "tiered") =>
        new() { Role = role, MinShiftHours = 3, MaxShiftHours = 9, GapHours = 10, WeeklyCap = 38, ConsecDayCap = 6, BreakType = breakType };

    [Fact]
    public void Roles_sort_SM_ASM_DSM_RSM_SA_JSA_with_any_other_role_first()
    {
        var byName = new[] { "ASM", "DSM", "JSA", "RSM", "SA", "SM", "XYZ" };
        Assert.Equal(["XYZ", "SM", "ASM", "DSM", "RSM", "SA", "JSA"], BreakRulesOrder.Sort(byName, r => r));
    }

    [Fact]
    public async Task Load_reads_every_role_in_the_page_order()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db => db.BreakRules.AddRange(Row("JSA"), Row("SM", "flat_30_unpaid"), Row("SA")));
        var rows = await new BreakRulesService(t, t.Scope).LoadAsync();
        Assert.Equal(["SM", "SA", "JSA"], rows.Select(r => r.Role));
        Assert.Equal(new BreakRuleInput("SM", 3, 9, 10, 38, 6, "flat_30_unpaid"), rows[0]);
    }

    [Fact]
    public async Task Save_upserts_by_role_and_leaves_the_pay_thresholds_alone()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db =>
        {
            var sa = Row("SA");
            sa.Ot15AfterHours = 9;
            sa.OtPeriodAfterHours = 76;
            sa.DoubleTimeUnderGapHours = 10;
            db.BreakRules.Add(sa);
        });
        var service = new BreakRulesService(t, t.Scope);

        var result = await service.SaveAsync([new("SA", 3.5, 9.5, 11, 40, 5, "tiered"), new("JSA", 3, 6, 10, 15, null, "tiered")]);

        Assert.Equal(SaveResult.Success, result);
        var rows = t.Read(db => db.BreakRules.OrderBy(r => r.Role).ToList());
        Assert.Equal(["JSA", "SA"], rows.Select(r => r.Role));
        var saved = rows[1];
        Assert.Equal(new BreakRuleInput("SA", 3.5, 9.5, 11, 40, 5, "tiered"),
            new BreakRuleInput(saved.Role, saved.MinShiftHours, saved.MaxShiftHours, saved.GapHours, saved.WeeklyCap, saved.ConsecDayCap, saved.BreakType));
        Assert.Equal(9, saved.Ot15AfterHours);
        Assert.Equal(76, saved.OtPeriodAfterHours);
        Assert.Equal(10, saved.DoubleTimeUnderGapHours);
        Assert.Null(rows[0].ConsecDayCap);
    }

    [Fact]
    public async Task A_part_day_consecutive_cap_is_refused_as_the_database_refused_it()
    {
        using var t = new RulesTestDatabase();
        t.Seed(db => db.BreakRules.Add(Row("SA")));
        var result = await new BreakRulesService(t, t.Scope).SaveAsync([new("SA", 3, 9, 10, 38, 6.5, "tiered")]);
        Assert.Equal(SaveResult.Fail("invalid input syntax for type integer: \"6.5\""), result);
        Assert.Equal(6, t.Read(db => db.BreakRules.Single().ConsecDayCap));
    }

    [Fact]
    public async Task Only_a_super_user_may_save()
    {
        using var t = new RulesTestDatabase(Tier.Emso);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BreakRulesService(t, t.Scope).SaveAsync([]));
    }
}
