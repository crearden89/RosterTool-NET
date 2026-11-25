// Port of src/lib/plan/approval.ts.

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Held;
using Roster.Data.Services.Planning;
using Roster.Engine.Core;

namespace Roster.Data.Services.Approval;

// Approving and withdrawing a plan, day by day. Each write stands on its own,
// as in the TypeScript (no transaction around a day): a failed delete is
// reported before anything is inserted on top of stale rows.
public sealed class PlanApprovalService(
    IRosterDbContextFactory contexts,
    IHeldPlans heldPlans,
    IStage1Loader stage1Loader,
    TimeProvider clock) : IPlanApproval
{
    // Approve the HELD plan (what the store and EMSO looked at) for each day:
    // persist its Stage 1 planned_blocks and mark the day's plan approved. A day
    // with nothing to plan (closed, no data) is skipped, not failed.
    public async Task<PlanApprovalResult> ApprovePlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default)
    {
        if (dates.Count == 0) return new PlanApprovalResult(true, 0);
        var snap = await heldPlans.ReadPlanAsync(storeId, Period.PeriodStartFor(dates[0]), ct);
        await using var admin = contexts.CreateSystem("approve a plan (the caller authorised it)");
        var approved = 0;
        foreach (var date in dates)
        {
            // Falls back to a live solve only when no plan is held yet.
            var stage1 = snap?.Days.FirstOrDefault(d => d.Date == date)?.Result ?? await stage1Loader.ComputeStage1Async(storeId, date, ct);
            if (!stage1.HasData) continue;

            // Unchecked, a failed delete lets the insert below stack fresh blocks on
            // top of stale ones - doubled headcounts reported as success.
            try
            {
                await admin.PlannedBlocks.Where(b => b.StoreId == storeId && b.Date == date).ExecuteDeleteAsync(ct);
            }
            catch (Exception e) when (IsDatabaseError(e))
            {
                return new PlanApprovalResult(false, approved, e.Message);
            }
            var rows = stage1.Blocks.Select(b => new PlannedBlock
            {
                StoreId = storeId,
                Date = date,
                LabourGroup = b.Group,
                BlockTime = TimeOnly.ParseExact(Grid.MinToSqlTime(b.Time), "HH:mm:ss", CultureInfo.InvariantCulture),
                Headcount = (int)b.Headcount,
            }).ToList();
            try
            {
                if (rows.Count > 0)
                {
                    admin.PlannedBlocks.AddRange(rows);
                    await admin.SaveChangesAsync(ct);
                }
            }
            catch (Exception e) when (IsDatabaseError(e))
            {
                admin.ChangeTracker.Clear();
                return new PlanApprovalResult(false, approved, e.Message);
            }
            admin.ChangeTracker.Clear();
            try
            {
                var now = clock.GetUtcNow();
                await DatabaseFunctionsService.UpsertAsync(
                    admin,
                    () => admin.DayPlans.Where(d => d.StoreId == storeId && d.Date == date)
                        .ExecuteUpdateAsync(u => u.SetProperty(d => d.PlanStatus, DayStatuses.Approved).SetProperty(d => d.PlanGeneratedAt, now), ct),
                    () => new DayPlan { StoreId = storeId, Date = date, PlanStatus = DayStatuses.Approved, PlanGeneratedAt = now },
                    ct);
            }
            catch (Exception e) when (IsDatabaseError(e))
            {
                admin.ChangeTracker.Clear();
                return new PlanApprovalResult(false, approved, e.Message);
            }
            await AuditAsync(admin, storeId, date, userId, "approve_plan", new { blocks = rows.Count }, ct);
            approved++;
        }
        return new PlanApprovalResult(true, approved);
    }

    // Withdraw the plan approval for each day: clear the persisted planned_blocks
    // and reset its status so it no longer counts as approved.
    public async Task<ApprovalResult> WithdrawPlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default)
    {
        if (dates.Count == 0) return ApprovalResult.Success;
        await using var admin = contexts.CreateSystem("withdraw a plan approval (the caller authorised it)");
        var list = dates.ToList();
        try
        {
            await admin.PlannedBlocks.Where(b => b.StoreId == storeId && list.Contains(b.Date)).ExecuteDeleteAsync(ct);
        }
        catch (Exception e) when (IsDatabaseError(e))
        {
            return new ApprovalResult(false, e.Message);
        }
        try
        {
            foreach (var date in list)
            {
                await DatabaseFunctionsService.UpsertAsync(
                    admin,
                    () => admin.DayPlans.Where(d => d.StoreId == storeId && d.Date == date)
                        .ExecuteUpdateAsync(u => u.SetProperty(d => d.PlanStatus, DayStatuses.None).SetProperty(d => d.PlanGeneratedAt, (DateTimeOffset?)null), ct),
                    () => new DayPlan { StoreId = storeId, Date = date, PlanStatus = DayStatuses.None, PlanGeneratedAt = null },
                    ct);
            }
        }
        catch (Exception e) when (IsDatabaseError(e))
        {
            admin.ChangeTracker.Clear();
            return new ApprovalResult(false, e.Message);
        }
        await AuditAsync(admin, storeId, dates[0], userId, "withdraw_plan", new { days = dates.Count }, ct);
        return ApprovalResult.Success;
    }

    /// <summary>A roster_audit row; like the TypeScript, a failed audit write does not fail the step.</summary>
    internal static async Task AuditAsync(RosterDbContext admin, Guid storeId, DateOnly? date, Guid userId, string action, object detail, CancellationToken ct)
    {
        try
        {
            admin.RosterAudit.Add(new RosterAudit
            {
                Id = Guid.NewGuid(),
                StoreId = storeId,
                Date = date,
                UserId = userId,
                Action = action,
                Detail = JsonSerializer.Serialize(detail),
                CreatedAt = admin.Clock.GetUtcNow(),
            });
            await admin.SaveChangesAsync(ct);
        }
        catch (Exception e) when (IsDatabaseError(e))
        {
            admin.ChangeTracker.Clear();
        }
    }

    /// <summary>An error the database reported (what supabase-js returned as `error`), not a bug.</summary>
    internal static bool IsDatabaseError(Exception e) => e is DbUpdateException or System.Data.Common.DbException or InvalidOperationException;
}
