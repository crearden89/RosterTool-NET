// Port of src/app/(app)/admin/rules/rostering/actions.ts and the reads in its page.tsx.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Rules.Breaks;

namespace Roster.Web.Features.Rules.Rostering;

public sealed class RosteringRulesService(IRosterDbContextFactory contexts, IStoreScope scope, TimeProvider clock) : IRosteringRulesService
{
    public async Task<RosteringRulesData> LoadAsync(CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        var overrides = await db.RosteringRules.AsNoTracking().OrderBy(o => o.Key)
            .Select(o => new RuleOverrideRow(o.Key, o.Value, o.Note))
            .ToListAsync(cancellationToken);
        var pay = await db.BreakRules.AsNoTracking().OrderBy(r => r.Role).ToListAsync(cancellationToken);
        var payRules = BreakRulesOrder.Sort(pay, r => r.Role)
            .Select(r => new PayRuleInput(r.Role, r.Ot15AfterHours, r.OtPeriodAfterHours, r.DoubleTimeUnderGapHours))
            .ToList();
        return new RosteringRulesData(overrides, payRules);
    }

    public async Task<SaveResult> SetRuleOverrideAsync(string key, double value, string? note = null, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        var (k, error) = RosteringRulesChecks.CheckOverride(key, value);
        if (error is not null) return SaveResult.Fail(error);
        await using var db = contexts.Create();
        var row = await db.RosteringRules.FirstOrDefaultAsync(r => r.Key == k, cancellationToken);
        if (row is null)
        {
            row = new RosteringRule { Key = k! };
            db.RosteringRules.Add(row);
        }
        row.Value = value;
        row.Note = note;
        row.UpdatedAt = clock.GetUtcNow();
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> ClearRuleOverrideAsync(string key, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        db.RosteringRules.RemoveRange(await db.RosteringRules.Where(r => r.Key == key).ToListAsync(cancellationToken));
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> SavePayRulesAsync(IReadOnlyList<PayRuleInput> rules, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (RosteringRulesChecks.CheckPayRules(rules) is { } error) return SaveResult.Fail(error);
        await using var db = contexts.Create();
        // One update per role, in turn, as the TypeScript did: a role with no row is
        // left alone, and a refusal stops at that role.
        foreach (var r in rules)
        {
            var row = await db.BreakRules.FirstOrDefaultAsync(b => b.Role == r.Role, cancellationToken);
            if (row is null) continue;
            row.Ot15AfterHours = r.OtShiftAfterHours;
            row.OtPeriodAfterHours = r.OtPeriodAfterHours;
            row.DoubleTimeUnderGapHours = r.DoubleTimeUnderGapHours;
            var saved = await SaveAsync(db, cancellationToken);
            if (!saved.Ok) return saved;
        }
        return SaveResult.Success;
    }

    private static async Task<SaveResult> SaveAsync(RosterDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return SaveResult.Success;
        }
        catch (Exception ex) when (SuperUserGuard.IsWriteFailure(ex))
        {
            return SaveResult.Fail(SuperUserGuard.MessageOf(ex));
        }
    }
}
