// Port of src/app/(app)/plan/generate/actions.ts (the server actions, as methods) and the
// data half of src/app/(app)/plan/generate/page.tsx (LoadPageAsync).

namespace Roster.Web.Features.Plan;

/// <summary>
/// The Plan page's service: the page's reads and every action of the TypeScript app's
/// plan/generate/actions.ts, with the same permission checks in the same places. The Roster
/// page (P2) calls the roster actions here too, as the TypeScript Roster page imports them
/// from the Plan folder.
/// </summary>
/// <remarks>
/// Plan and Roster are separate pages reading the same held snapshot: any change to it must
/// show on both (the TypeScript app's <c>revalidatePlanPages</c>; here the pages read afresh
/// after every action).
/// </remarks>
public interface IPlanService
{
    // ─── The page ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything /plan/generate shows for the signed-in user and the page's query
    /// (<c>?store=&amp;date=&amp;period=&amp;view=</c>). Viewing the page never solves: it reads
    /// the HELD snapshot written by Generate.
    /// </summary>
    Task<PlanPageModel> LoadPageAsync(PlanPageQuery query, CancellationToken cancellationToken = default);

    // ─── Generate the plan, in steps ─────────────────────────────────────────────
    // Generate (or regenerate) the held PLAN for a fortnight, persisted as the snapshot the
    // page then reads on every visit. The plan only ever changes when this is pressed -
    // never just from viewing the page. The roster is built separately, from this plan, once
    // its review is approved (GenerateRosterAsync); a new plan sends a submitted or approved
    // review back round.
    //
    // It runs in steps so no request comes near the server's time limit:
    // StartPlanGenerationAsync says which days each step plans, PlanDaysStepAsync plans them
    // and hands them back sealed, and FinishPlanGenerationAsync checks every part and holds
    // the whole fortnight in one write (GenerateParts). Nothing is held until the last step,
    // so a run that stops part-way leaves the held plan exactly as it was.

    /// <summary>startPlanGeneration: refused when the store can't be written or the plan is locked.</summary>
    Task<PlanStartResult> StartPlanGenerationAsync(Guid storeId, DateOnly periodStart);

    /// <summary>planDaysStep: plans some days of the fortnight and hands them back as a sealed part.</summary>
    Task<PlanStepResult> PlanDaysStepAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<DateOnly> dates);

    /// <summary>
    /// finishPlanGeneration: opens every part and holds the fortnight in one write.
    /// <paramref name="reason"/> says why the plan changed, for the review's history: a
    /// regenerate (null), or the public-holiday load setting of one day.
    /// </summary>
    Task<PlanResult> FinishPlanGenerationAsync(
        Guid storeId,
        DateOnly periodStart,
        string runId,
        IReadOnlyList<string> parts,
        PlanChangeReason? reason = null);

    /// <summary>
    /// generateRoster: build the fortnight's roster from its HELD, approved plan - the Roster
    /// page's Generate. Locked until the plan's review is approved, so the roster is always the
    /// plan the EMSO signed off (owner decision R5).
    /// </summary>
    Task<PlanResult> GenerateRosterAsync(Guid storeId, DateOnly periodStart);

    /// <summary>
    /// When the fortnight's held roster was last saved, or null (the TypeScript app's
    /// <c>fetchHeldRosterAt</c>, which reads /api/roster/held): Generate Roster's safety net
    /// asks it when the build's own answer is slow to come.
    /// </summary>
    Task<DateTimeOffset?> HeldRosterAtAsync(Guid storeId, DateOnly periodStart);

    // ─── Edit the held plan ──────────────────────────────────────────────────────

    /// <summary>
    /// editPlanDay: manual plan edits (the Plan page's edit mode) on the HELD snapshot for one
    /// day - both the shift structure (times, added shifts, removed shifts) and the painted
    /// tiles, in one write so a resize and a repaint can't half-apply. Every shift must sit on
    /// the 15-minute grid, clear the 3-hour minimum and stay inside the staffed day; the day is
    /// rejected unless every shift still carries exactly its break entitlement. The day is then
    /// re-checked and the review lapses.
    /// </summary>
    Task<PlanResult> EditPlanDayAsync(Guid storeId, DateOnly periodStart, DateOnly date, IReadOnlyList<PlanShiftEdit> edits);

    // ─── Approval ────────────────────────────────────────────────────────────────

    /// <summary>approvePlan: persist the day's Stage 1 planned blocks and mark its plan approved.</summary>
    Task<PlanResult> ApprovePlanAsync(Guid storeId, DateOnly date);

    /// <summary>withdrawPlan: clear the day's planned blocks so it no longer counts as approved.</summary>
    Task<PlanResult> WithdrawPlanAsync(Guid storeId, DateOnly date);

    /// <summary>
    /// setPhReallocation: switch the public-holiday Load Running reallocation for a date
    /// (spec §4.6). Saves only the flag; the page then regenerates the held plan in steps, as
    /// Generate does.
    /// </summary>
    Task<PlanResult> SetPhReallocationAsync(Guid storeId, DateOnly date, bool on);

    // ─── The roster's actions (the Roster page) ──────────────────────────────────

    /// <summary>
    /// setShiftLock: lock or unlock a roster shift (spec §6.6). Locking an engine-placed shift
    /// with no persisted row pins it at <paramref name="startMin"/>-<paramref name="endMin"/>.
    /// </summary>
    Task<PlanResult> SetShiftLockAsync(Guid storeId, DateOnly date, Guid employeeId, bool locked, double? startMin = null, double? endMin = null);

    /// <summary>updateShift: edit a shift's times (minutes from midnight); the shift is locked.</summary>
    Task<PlanResult> UpdateShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin);

    /// <summary>saveShiftRow: save one roster row - the person and/or the times - in one write.</summary>
    Task<PlanResult> SaveShiftRowAsync(Guid storeId, DateOnly date, Guid originalEmployeeId, Guid employeeId, double startMin, double endMin);

    /// <summary>removeShift: remove a persisted shift from the roster.</summary>
    Task<PlanResult> RemoveShiftAsync(Guid storeId, DateOnly date, Guid employeeId);

    /// <summary>addShift: add a manual, locked shift for a team member (one per person per day).</summary>
    Task<PlanResult> AddShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin);

    /// <summary>approveRoster: approve (publish) the roster for every day given, with an optional cover note.</summary>
    Task<PlanResult> ApproveRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, string? comment = null);

    /// <summary>withdrawRoster: withdraw the roster approval for every day given.</summary>
    Task<PlanResult> WithdrawRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates);
}
