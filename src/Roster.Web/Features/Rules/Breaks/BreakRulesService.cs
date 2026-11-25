// Port of src/app/(app)/admin/rules/breaks/actions.ts and the read in its page.tsx.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Js = Roster.Engine.Core.Js;

namespace Roster.Web.Features.Rules.Breaks;

public sealed class BreakRulesService(IRosterDbContextFactory contexts, IStoreScope scope) : IBreakRulesService
{
    public async Task<IReadOnlyList<BreakRuleInput>> LoadAsync(CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        var rows = await db.BreakRules.AsNoTracking().OrderBy(r => r.Role).ToListAsync(cancellationToken);
        return BreakRulesOrder.Sort(rows, r => r.Role)
            .Select(r => new BreakRuleInput(r.Role, r.MinShiftHours, r.MaxShiftHours, r.GapHours, r.WeeklyCap, r.ConsecDayCap, r.BreakType))
            .ToList();
    }

    public async Task<SaveResult> SaveAsync(IReadOnlyList<BreakRuleInput> rules, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        // consec_day_cap is a whole number of days in this schema; the database
        // refused anything else, in its own words.
        foreach (var r in rules)
            if (r.ConsecDayCap is { } cap && !(double.IsInteger(cap) && Math.Abs(cap) <= int.MaxValue))
                return SaveResult.Fail($"invalid input syntax for type integer: \"{Js.NumberToString(cap)}\"");

        await using var db = contexts.Create();
        var existing = await db.BreakRules.ToDictionaryAsync(r => r.Role, cancellationToken);
        foreach (var r in rules)
        {
            // Upsert by role. The pay-rule thresholds (OT / double time) are deliberately
            // NOT written here - they are edited on the Rostering Rules page, and only the
            // columns given are touched, so the two saves cannot clobber each other.
            if (!existing.TryGetValue(r.Role, out var row))
            {
                row = new BreakRule { Role = r.Role };
                db.BreakRules.Add(row);
                existing[r.Role] = row;
            }
            row.MinShiftHours = r.MinShiftHours;
            row.MaxShiftHours = r.MaxShiftHours;
            row.GapHours = r.GapHours;
            row.WeeklyCap = r.WeeklyCap;
            row.ConsecDayCap = r.ConsecDayCap is { } c ? (int)c : null;
            row.BreakType = r.BreakType;
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (SuperUserGuard.IsWriteFailure(ex))
        {
            return SaveResult.Fail(SuperUserGuard.MessageOf(ex));
        }
        return SaveResult.Success;
    }
}
