// Port of src/lib/roster/approval.ts (publishRoster, unpublishRoster).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Functions;
using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;
using RosterOps = Roster.Engine.RosterOps;

namespace Roster.Data.Services.Approval;

// Publishing and withdrawing a fortnight's roster - the work behind the Roster
// page's Approve/Withdraw and the EMSO's decision on the review page. Callers
// authorise first and refresh the pages after; these only do the writes. Which
// days need a cover note, and each day's published shifts, are the engine's
// RosterOps.Approval.
public sealed class RosterApprovalService(
    IRosterDbContextFactory contexts,
    IDatabaseFunctions functions,
    IHeldRosters heldRosters,
    TimeProvider clock) : IRosterApproval
{
    // Every reference read is made once for the whole fortnight (one memo scope).
    public Task<ApprovalResult> PublishRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, string? comment = null, CancellationToken ct = default) =>
        RequestMemo.Run(() => PublishAsync(storeId, dates, userId, comment, ct));

    private async Task<ApprovalResult> PublishAsync(Guid storeId, IReadOnlyList<DateOnly> dates, Guid userId, string? comment, CancellationToken ct)
    {
        var approvedDays = 0;
        // Approve the HELD roster - what the manager was looking at. It is never
        // solved here: the roster is built only from an approved plan, by Generate
        // on the Roster page.
        var fortnight = new JsMap<DateOnly, RosterComputation>();
        if (dates.Count > 0)
        {
            var roster = await heldRosters.ReadRosterAsync(storeId, Period.PeriodStartFor(dates[0]), ct);
            if (roster is null) return new ApprovalResult(false, "Generate the roster first.");
            fortnight = roster.ByDate;
        }

        // Soft block (spec §6.4): a day with an open shift or a cover gap - an ERROR,
        // graded as the Review grades them - may be published, with a note on the
        // attempts to cover it. Warnings and notes alone never ask for one.
        RosterDay? DayOf(DateOnly d) => fortnight.TryGet(d, out var r) ? RosterDay.Of(r) : null;
        var noteError = RosterOps.Approval.CoverNoteError(DayOf, dates, comment);
        if (noteError is not null) return new ApprovalResult(false, noteError);

        await using var admin = contexts.CreateSystem("publish a roster (the caller authorised it)");
        foreach (var date in dates)
        {
            var day = fortnight.TryGet(date, out var r) ? r : null;
            switch (RosterOps.Approval.StepFor(day is null ? null : RosterDay.Of(day)))
            {
                case PublishStep.ApproveClosed:
                    // A closed day is signed off with the rest of the fortnight - zero
                    // shifts is its correct roster, and leaving it un-approved would
                    // hold the whole period's approved state hostage.
                    try
                    {
                        var now = clock.GetUtcNow();
                        await DatabaseFunctionsService.UpsertAsync(
                            admin,
                            () => admin.DayPlans.Where(d => d.StoreId == storeId && d.Date == date)
                                .ExecuteUpdateAsync(u => u.SetProperty(d => d.RosterStatus, DayStatuses.Approved).SetProperty(d => d.RosterGeneratedAt, now), ct),
                            () => new DayPlan { StoreId = storeId, Date = date, RosterStatus = DayStatuses.Approved, RosterGeneratedAt = now },
                            ct);
                    }
                    catch (Exception e) when (PlanApprovalService.IsDatabaseError(e))
                    {
                        admin.ChangeTracker.Clear();
                        return new ApprovalResult(false, e.Message);
                    }
                    continue;
                case PublishStep.Skip:
                    continue;
            }

            // ATOMIC persist: the whole day happens inside one transaction
            // (approve_roster_day). A failure can no longer leave a day half-written.
            List<RosterDayShift> payload;
            try
            {
                payload = RosterOps.Approval.ShiftPayload(day!.Shifts).Select(ToRosterDayShift).ToList();
            }
            catch (FormatException e)
            {
                return new ApprovalResult(false, $"{date.ToIso()}: {e.Message}");
            }
            try
            {
                await functions.ApproveRosterDayAsync(storeId, date, payload, ct);
            }
            catch (Exception e) when (PlanApprovalService.IsDatabaseError(e))
            {
                return new ApprovalResult(false, $"{date.ToIso()}: {e.Message}");
            }
            approvedDays++;
        }

        await PlanApprovalService.AuditAsync(admin, storeId, dates.Count > 0 ? dates[0] : null, userId, "approve_roster", new
        {
            fortnightStart = dates.Count > 0 ? dates[0].ToIso() : null,
            fortnightEnd = dates.Count > 0 ? dates[^1].ToIso() : null,
            approvedDays,
            coverageComment = RosterOps.Approval.CoverageComment(comment),
        }, ct);
        if (approvedDays == 0) return new ApprovalResult(false, "No roster could be generated for any day in this fortnight");
        return ApprovalResult.Success;
    }

    /// <summary>
    /// One p_shifts entry as approve_roster_day casts it: (s->>'start_time')::time,
    /// (s->>'break_minutes')::int - a value those casts would refuse is refused here.
    /// </summary>
    private static RosterDayShift ToRosterDayShift(ApprovedShift s)
    {
        static TimeOnly Time(string t) => TimeOnly.ParseExact(t, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (s.BreakMinutes != Math.Floor(s.BreakMinutes))
            throw new FormatException($"invalid input syntax for type integer: \"{Js.NumberToString(s.BreakMinutes)}\"");
        return new RosterDayShift(
            Guid.Parse(s.EmployeeId), s.EmployeeName, s.Role, Time(s.StartTime), Time(s.EndTime), (int)s.BreakMinutes, s.Locked,
            s.Blocks.Select(b => new RosterDayTile(Time(b.BlockTime), b.LabourGroup, b.SubTask)).ToList());
    }

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
