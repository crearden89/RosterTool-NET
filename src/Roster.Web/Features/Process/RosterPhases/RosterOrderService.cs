// Port of src/app/(app)/admin/process/roster/actions.ts and the read in its page.tsx.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Rostering.Painter;
using Roster.Web.Features.Process.Planning;
using Roster.Web.Features.Rules;

namespace Roster.Web.Features.Process.RosterPhases;

public sealed class RosterOrderService(IRosterDbContextFactory contexts, IStoreScope scope, TimeProvider clock) : IRosterOrderService
{
    public async Task<IReadOnlyList<OrderStep>> LoadAsync(CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        var rows = await db.RosterOrderSteps.AsNoTracking().OrderBy(r => r.Position).ToListAsync(cancellationToken);
        // The same normalisation the ENGINE applies before it runs: a stale step_type or
        // malformed day_order left in the table would otherwise break the page, while an
        // empty table still means "using the default order".
        return rows.Count == 0 ? [] : RosterOrder.NormaliseOrder(rows.Select(r => new OrderStep(r.StepType, r.DayOrder)).ToList());
    }

    public async Task<SaveResult> SaveAsync(IReadOnlyList<OrderStep> steps, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (RosterOrderChecks.CheckSteps(steps) is { } error) return SaveResult.Fail(error);
        await using var db = contexts.Create();
        db.RosterOrderSteps.RemoveRange(await db.RosterOrderSteps.ToListAsync(cancellationToken));
        if (await PlanningOrderService.SaveAsync(db, cancellationToken) is { Ok: false } deleted) return deleted;
        if (steps.Count > 0)
        {
            var now = clock.GetUtcNow();
            for (var i = 0; i < steps.Count; i++)
                db.RosterOrderSteps.Add(new RosterOrderStep
                {
                    Position = i,
                    StepType = steps[i].Type,
                    DayOrder = steps[i].DayOrder?.ToList(),
                    UpdatedAt = now,
                });
            if (await PlanningOrderService.SaveAsync(db, cancellationToken) is { Ok: false } inserted) return inserted;
        }
        return SaveResult.Success;
    }

    public Task<SaveResult> ResetAsync(CancellationToken cancellationToken = default) => SaveAsync([], cancellationToken);
}
