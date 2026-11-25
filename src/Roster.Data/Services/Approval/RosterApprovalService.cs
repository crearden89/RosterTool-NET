// Port of src/lib/roster/approval.ts (publishRoster, unpublishRoster).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Functions;

namespace Roster.Data.Services.Approval;

// Publishing and withdrawing a fortnight's roster. publishRoster reads the held
// roster (the roster engine's RosterComputation) and grades its days (the
// roster's day status), which arrive with E2's and E3's halves; unpublishRoster
// needs neither.
public sealed class RosterApprovalService(IRosterDbContextFactory contexts, IDatabaseFunctions functions) : IRosterApproval
{
    public Task<ApprovalResult> PublishRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, string? comment = null, CancellationToken ct = default) =>
        throw new NotImplementedException($"publishRoster needs the held roster (E2's RosterComputation) and its day status (E3) - {functions.GetType().Name} is ready.");

    // Withdraw the roster approval for every day in the fortnight: removes the
    // persisted shifts (so the Floor Display stops showing them) and resets each
    // day's status.
    public async Task<ApprovalResult> UnpublishRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, CancellationToken ct = default)
    {
        await using var admin = contexts.CreateSystem("withdraw a roster approval (the caller authorised it)");
        var list = dates.ToList();
        // A failed delete here would leave the withdrawn roster live on the Floor
        // Display while the manager is told the approval was withdrawn.
        try
        {
            var shifts = admin.GeneratedShifts.Where(s => s.StoreId == storeId && list.Contains(s.Date));
            await admin.ShiftTaskBlocks.Where(b => shifts.Select(s => s.Id).Contains(b.ShiftId)).ExecuteDeleteAsync(ct);
            await shifts.ExecuteDeleteAsync(ct);
        }
        catch (Exception e) when (PlanApprovalService.IsDatabaseError(e))
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
                        .ExecuteUpdateAsync(u => u.SetProperty(d => d.RosterStatus, DayStatuses.None).SetProperty(d => d.RosterGeneratedAt, (DateTimeOffset?)null), ct),
                    () => new DayPlan { StoreId = storeId, Date = date, RosterStatus = DayStatuses.None, RosterGeneratedAt = null },
                    ct);
            }
        }
        catch (Exception e) when (PlanApprovalService.IsDatabaseError(e))
        {
            admin.ChangeTracker.Clear();
            return new ApprovalResult(false, e.Message);
        }
        await PlanApprovalService.AuditAsync(admin, storeId, list.Count > 0 ? list[0] : null, userId, "withdraw_roster",
            new { fortnightStart = list.Count > 0 ? list[0].ToString("yyyy-MM-dd") : null, fortnightEnd = list.Count > 0 ? list[^1].ToString("yyyy-MM-dd") : null }, ct);
        return ApprovalResult.Success;
    }
}
