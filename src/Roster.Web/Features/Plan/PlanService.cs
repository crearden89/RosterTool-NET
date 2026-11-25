// Port of src/app/(app)/plan/generate/actions.ts and the reads of
// src/app/(app)/plan/generate/page.tsx, on the data layer's services.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Feeds;
using Roster.Data.Scope;
using Roster.Data.Services.Approval;
using Roster.Data.Services.Held;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Data.Services.Review;
using Roster.Data.Services.SubTasks;
using Roster.Data.Snapshots;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.SubTasks;
using Roster.Web.Features.Review;

namespace Roster.Web.Features.Plan;

/// <summary>
/// Builds the fortnight's roster from its held plan and holds it (lib/planSnapshots'
/// computeAndStoreRosterSnapshot): false when there is no plan yet. The data layer's
/// roster loader; until it is on main, <see cref="PendingRosterRebuild"/> stands in.
/// </summary>
public interface IRosterRebuild
{
    Task<bool> ComputeAndStoreRosterSnapshotAsync(Guid storeId, DateOnly periodStart, Guid userId, CancellationToken ct = default);
}

/// <summary>PENDING: the roster loader (stage2/compute + planSnapshots' roster half) is not on main yet.</summary>
public sealed class PendingRosterRebuild : IRosterRebuild
{
    public Task<bool> ComputeAndStoreRosterSnapshotAsync(Guid storeId, DateOnly periodStart, Guid userId, CancellationToken ct = default) =>
        throw new NotImplementedException("Building the roster waits for the data layer's roster loader (computeAndStoreRosterSnapshot).");
}

public sealed class PlanService(
    IStoreScope scope,
    IStoreDirectory directory,
    IRememberedStore remembered,
    IPlanPeriods periods,
    IHeldPlans held,
    IPlanSnapshotStore snapshots,
    IRefData refData,
    IDayCheckOptionsLoader checkOptions,
    ISubtaskLabels labels,
    ICurveSource curves,
    IReviewGate gate,
    IPlanApproval planApproval,
    IRosterApproval rosterApproval,
    IRosterRebuild rosterRebuild,
    IRosterDbContextFactory contexts,
    PlanPartKey partKey,
    TimeProvider clock) : IPlanService
{
    private const string DeniedAuth = "You are not signed in.";

    private ScopedUser? User => scope.User;

    private static DateOnly? ParseIso(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    // ─── The page ────────────────────────────────────────────────────────────────

    public async Task<PlanPageModel> LoadPageAsync(PlanPageQuery query, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var user = User ?? throw new InvalidOperationException(DeniedAuth);
        // The roster half of this page moved to its own route. Old bookmarks and links
        // with ?view=roster land there, keeping store/date intact.
        if (query.View == "roster")
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(query.Store)) qs.Add($"store={Uri.EscapeDataString(query.Store)}");
            if (!string.IsNullOrEmpty(query.Date)) qs.Add($"date={Uri.EscapeDataString(query.Date)}");
            return new PlanPageModel { RedirectTo = qs.Count > 0 ? $"/plan/roster?{string.Join('&', qs)}" : "/plan/roster" };
        }

        // Plan is a PEER-VISIBLE page: the switcher offers every store in the network to
        // every login. What the viewer can DO with the selected store comes from the
        // access matrix below.
        var stores = (await directory.ListAsync(ct)).Select(s => new PlanStore(s.Id, s.Code, s.Name, s.AreaId)).ToList();
        Guid? selectedStore = user.StoreId is { } own && string.IsNullOrEmpty(query.Store)
            ? own
            : StoreScope.ResolveStoreId(Guid.TryParse(query.Store, out var asked) ? asked : null, [.. stores.Select(s => s.Id)], remembered.StoreId);
        // peer = another store's manager looking in: read-only AND trimmed (shift grid +
        // benchmark hours only). PA/DSO ("read") see the full page read-only.
        var access = selectedStore is { } sel ? await scope.AccessForStoreAsync(sel, ct) : null;
        var peer = access == StoreAccess.Peer;

        // An explicit ?period= (fortnight nav) or a ?date= outside the default period pins
        // the fortnight - so history stays reachable after time moves on. With neither,
        // open on the earliest un-approved fortnight (§5.2) so a behind store sees the gap.
        var date = ParseIso(query.Date);
        var requestedPeriod = ParseIso(query.Period) is { } p ? Period.PeriodStartFor(p) : date is { } d0 ? Period.PeriodStartFor(d0) : (DateOnly?)null;
        var planning = selectedStore is { } s1 && requestedPeriod is null
            ? await periods.ResolvePlanningPeriodAsync(s1, peer, ct)
            : new PlanningPeriod(requestedPeriod ?? Period.PlanningPeriodStart(Dates.TodayAu(clock.GetUtcNow())), []);
        var planDates = selectedStore is not null ? periods.GetPlanPeriodDates(planning.PeriodStart) : [];
        var today = Dates.TodayAu(clock.GetUtcNow());
        var periodStart = planDates.Count > 0 ? planDates[0] : today;
        var selectedDate = date ?? periodStart;

        // NO solving on render: the page reads the HELD snapshot written by Generate. Peer
        // view goes through the dedicated plan-only reader (never the roster snapshot).
        var snapshot = selectedStore is not { } store
            ? null
            : peer ? await held.ReadPeerPlanAsync(store, periodStart, ct) : await held.ReadPlanAsync(store, periodStart, ct);

        // Approval state and the PH flag. Peer view needs neither.
        var phReallocated = false;
        var isPublicHolidayDay = false;
        string? reviewStatus = null, reviewKey = null;
        string? generatorName = null;
        bool nightFill;
        await using (var db = contexts.CreateSystem("Plan is peer-visible: night fill and the generator's name of any store"))
        {
            if (!peer && selectedStore is { } st)
            {
                await using var mine = contexts.Create();
                phReallocated = await mine.DayPlans.Where(x => x.StoreId == st && x.Date == selectedDate)
                    .Select(x => x.PhLoadReallocated).FirstOrDefaultAsync(ct);
                isPublicHolidayDay = await refData.IsPublicHolidayAsync(st, selectedDate, ct);
                // The plan is signed off through the fortnight's review (Plan -> Review ->
                // Roster), not day by day here.
                var review = await mine.RosterReviews.Where(x => x.StoreId == st && x.PeriodStart == periodStart)
                    .Select(x => new { x.Status, x.SnapshotKey }).FirstOrDefaultAsync(ct);
                (reviewStatus, reviewKey) = (review?.Status, review?.SnapshotKey);
            }
            nightFill = selectedStore is { } nf && await db.StoreAttributes.Where(x => x.StoreId == nf).Select(x => x.NightFill).FirstOrDefaultAsync(ct);
            // Who generated the held plan, for the generate bar.
            if (!peer && snapshot?.GeneratedBy is { } by)
                generatorName = await db.AppUsers.Where(x => x.Id == by).Select(x => x.Username).FirstOrDefaultAsync(ct);
        }

        // A plan held from before sub-tasks is labelled as it is shown (labels only: its
        // shifts, tiles and checks are exactly as held).
        var heldDay = date is null && query.Date is { Length: > 0 } ? null : snapshot?.Days.FirstOrDefault(x => x.Date == selectedDate);
        var selectedDay = heldDay is { Plan: { } hp } && heldDay.Result.HasData && selectedStore is { } ls && Label.NeedsLabels(hp.Shifts)
            ? heldDay with { Plan = hp with { Shifts = await labels.LabelPlanDayAsync(ls, selectedDate, ResetSource.From(heldDay.Result), hp.Shifts, ct) } }
            : heldDay;
        // The store's cover rules, for the day's live hours check while editing.
        var cover = !peer && selectedStore is { } cs && selectedDay is not null ? (await checkOptions.DayCheckOptionsAsync(cs, selectedDate, ct)).Cover : null;
        // The store's weekly trade pattern - the SAME derived curve the engines plan with.
        var curveRows = selectedStore is { } cr
            ? (await curves.PeerTradeCurveRowsAsync(cr, ct)).Select(r => new CurveRow(r.DayOfWeek, r.BlockTime, r.Allocation)).ToList()
            : [];

        return PlanPageComposer.Compose(new PlanPageReads
        {
            Stores = stores,
            SelectedStore = selectedStore,
            Peer = peer,
            ReadOnlyTier = AccessMatrix.IsReadOnlyTier(user.Tier),
            IsStoreTier = user.Tier == Tier.Store,
            PeriodStart = periodStart,
            SelectedDate = selectedDate,
            // A finished fortnight is a record, not a plan: it opens read-only.
            IsPastPeriod = periodStart < Period.CurrentPeriodStart(today),
            OverduePeriods = peer ? [] : planning.OverduePeriods,
            Snapshot = snapshot,
            SelectedDay = selectedDay,
            PhReallocated = phReallocated,
            IsPublicHolidayDay = isPublicHolidayDay,
            ReviewState = peer ? PlanReviewState.NotSubmitted : PlanReviewStatus.PlanReviewState(reviewStatus, reviewKey, snapshot?.Version),
            Cover = cover,
            CurveRows = curveRows,
            NightFill = nightFill,
            GeneratorName = generatorName,
        });
    }

    // ─── Generate the plan, in steps ─────────────────────────────────────────────

    private async Task<string?> PlanGenerationRefusalAsync(Guid storeId, DateOnly periodStart)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return denied;
        if (await gate.PlanLockedAsync(storeId, periodStart)) return RosterGates.PlanLocked;
        return null;
    }

    private ScopedUser RequireUser() => User ?? throw new InvalidOperationException(DeniedAuth);

    private ReviewActor Actor(ScopedUser u) => new(u.Id, u.Username, u.Tier.Key());

    public async Task<PlanStartResult> StartPlanGenerationAsync(Guid storeId, DateOnly periodStart)
    {
        if (await PlanGenerationRefusalAsync(storeId, periodStart) is { } refused) return new PlanStartResult(false, refused);
        RequireUser();
        var dates = periods.GetPlanPeriodDates(periodStart);
        if (dates.Count == 0) return new PlanStartResult(false, "This fortnight has no days to plan.");
        return new PlanStartResult(true, RunId: Guid.NewGuid().ToString(), Steps: GenerateParts.PlanSteps(dates));
    }

    public async Task<PlanStepResult> PlanDaysStepAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<DateOnly> dates)
    {
        if (await PlanGenerationRefusalAsync(storeId, periodStart) is { } refused) return new PlanStepResult(false, refused);
        var user = RequireUser();
        var fortnight = periods.GetPlanPeriodDates(periodStart).ToHashSet();
        if (dates.Count == 0 || dates.Count > GenerateParts.DaysPerStep || dates.Any(d => !fortnight.Contains(d)))
            return new PlanStepResult(false, "Those days are not part of this fortnight.");
        try
        {
            var days = await held.ComputePlanDaysAsync(storeId, dates);
            var stamp = new PartStamp(storeId, periodStart, runId, user.Id);
            return new PlanStepResult(true, Part: GenerateParts.SealPlanPart(partKey.Key, stamp, days, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        }
        catch (Exception e)
        {
            return new PlanStepResult(false, string.IsNullOrEmpty(e.Message) ? "Generation failed" : e.Message);
        }
    }

    public async Task<PlanResult> FinishPlanGenerationAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<string> parts, PlanChangeReason? reason = null)
    {
        if (await PlanGenerationRefusalAsync(storeId, periodStart) is { } refused) return PlanResult.Refused(refused);
        var user = RequireUser();
        var dates = periods.GetPlanPeriodDates(periodStart);
        if (parts is null) return PlanResult.Refused("Nothing to save. Press Generate again.");
        var opened = GenerateParts.OpenPlanParts<PlanSnapshotDay>(
            partKey.Key, [.. parts], new PartStamp(storeId, periodStart, runId, user.Id), dates, d => d.Date, clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (!opened.Ok) return PlanResult.Refused(opened.Error!);
        try
        {
            await held.StorePlanSnapshotAsync(storeId, periodStart, opened.Days!, user.Id);
        }
        catch (Exception e)
        {
            return PlanResult.Refused(string.IsNullOrEmpty(e.Message) ? "Generation failed" : e.Message);
        }
        DateOnly? ph = reason is { } r && dates.Contains(r.PhDate) ? r.PhDate : null;
        await gate.LapseReviewOnPlanChangeAsync(
            storeId,
            periodStart,
            Actor(user),
            ph is { } phDate ? $"The public-holiday load setting for {phDate.ToIso()} changed the plan" : "The plan was regenerated");
        if (ph is null) await AuditAsync(storeId, periodStart, user, "generate_plan", new { periodStart = periodStart.ToIso() });
        return PlanResult.Done;
    }

    public async Task<PlanResult> GenerateRosterAsync(Guid storeId, DateOnly periodStart)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        var user = RequireUser();
        if (!(await gate.RosterGateAsync(storeId, periodStart)).Unlocked) return PlanResult.Refused(RosterGates.RosterLocked);
        try
        {
            if (!await rosterRebuild.ComputeAndStoreRosterSnapshotAsync(storeId, periodStart, user.Id))
                return PlanResult.Refused("Generate the plan first.");
        }
        catch (Exception e)
        {
            return PlanResult.Refused(string.IsNullOrEmpty(e.Message) ? "Generation failed" : e.Message);
        }
        await AuditAsync(storeId, periodStart, user, "generate_roster", new { periodStart = periodStart.ToIso() });
        return PlanResult.Done;
    }

    public async Task<DateTimeOffset?> HeldRosterAtAsync(Guid storeId, DateOnly periodStart) =>
        (await snapshots.ReadAsync(storeId, periodStart)).Roster?.GeneratedAt;

    // ─── Edit the held plan ──────────────────────────────────────────────────────

    public async Task<PlanResult> EditPlanDayAsync(Guid storeId, DateOnly periodStart, DateOnly date, IReadOnlyList<PlanShiftEdit> edits)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        if (await gate.PlanLockedAsync(storeId, periodStart)) return PlanResult.Refused(RosterGates.PlanLocked);
        var user = RequireUser();
        var plan = await held.ReadPlanAsync(storeId, periodStart);
        if (plan is null) return PlanResult.Refused("No held plan to edit - generate first.");
        var day = plan.Days.FirstOrDefault(d => d.Date == date);
        if (day?.Plan is null) return PlanResult.Refused("No plan for this date.");

        var (error, rebuilt) = PlanDayEdits.Rebuild(day, edits);
        if (error is not null) return PlanResult.Refused(error);
        // Every tile of the four sub-task groups carries a label: one chosen in the edit is
        // kept, the rest are labelled by the normal rules.
        var labelled = await labels.LabelPlanDayAsync(storeId, date, ResetSource.From(day.Result), rebuilt!.Shifts);
        if (PlanDayEdits.BreakError(labelled) is { } breaks) return PlanResult.Refused(breaks);
        var edited = PlanDayEdits.Recheck(day, labelled, await checkOptions.DayCheckOptionsAsync(storeId, date));

        // Compare-and-set on the snapshot version: if someone else edited or regenerated
        // this fortnight while these tiles were being painted, the write is refused rather
        // than silently overwriting their version.
        var written = await held.WritePlanSnapshotAsync(storeId, periodStart, [.. plan.Days.Select(d => d.Date == date ? edited : d)], plan.Version);
        if (!written.Ok) return PlanResult.Refused(written.Error ?? "The plan could not be saved.");
        await gate.LapseReviewOnPlanChangeAsync(storeId, periodStart, Actor(user), $"The plan for {date.ToIso()} was edited");
        await AuditAsync(storeId, date, user, "edit_plan_day", new { changes = rebuilt.Changed, shifts = labelled.Count });
        return PlanResult.Done;
    }

    // ─── Approval ────────────────────────────────────────────────────────────────

    public async Task<PlanResult> ApprovePlanAsync(Guid storeId, DateOnly date)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        if (await gate.PlanLockedAsync(storeId, Period.PeriodStartFor(date))) return PlanResult.Refused(RosterGates.PlanLocked);
        var user = RequireUser();
        var result = await planApproval.ApprovePlanDaysAsync(storeId, [date], user.Id);
        if (!result.Ok) return PlanResult.Refused(result.Error ?? "");
        return result.Approved > 0 ? PlanResult.Done : PlanResult.Refused("No plan to approve");
    }

    public async Task<PlanResult> WithdrawPlanAsync(Guid storeId, DateOnly date)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        if (await gate.PlanLockedAsync(storeId, Period.PeriodStartFor(date))) return PlanResult.Refused(RosterGates.PlanLocked);
        var user = RequireUser();
        var result = await planApproval.WithdrawPlanDaysAsync(storeId, [date], user.Id);
        return new PlanResult(result.Ok, result.Error);
    }

    public async Task<PlanResult> SetPhReallocationAsync(Guid storeId, DateOnly date, bool on)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        // The toggle rebuilds the held plan - not while it is approved and locked.
        if (await gate.PlanLockedAsync(storeId, Period.PeriodStartFor(date))) return PlanResult.Refused(RosterGates.PlanLocked);
        var user = RequireUser();
        await using (var db = contexts.CreateSystem("setPhReallocation: an upsert of one day_plans flag (the admin client)"))
        {
            // Upsert touches only this flag, so a day's plan/roster status is preserved.
            var row = await db.DayPlans.FirstOrDefaultAsync(x => x.StoreId == storeId && x.Date == date);
            if (row is null) db.DayPlans.Add(new DayPlan { StoreId = storeId, Date = date, PhLoadReallocated = on });
            else row.PhLoadReallocated = on;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException e)
            {
                return PlanResult.Refused(e.InnerException?.Message ?? e.Message);
            }
        }
        await AuditAsync(storeId, date, user, "set_ph_reallocation", new { on });
        // The toggle changes Stage 1's inputs: the page now regenerates the held plan in
        // steps, as Generate does (which sends a submitted or approved review back round).
        return PlanResult.Done;
    }

    // ─── The roster's actions (the Roster page) ──────────────────────────────────

    private static TimeOnly SqlTime(double min) => TimeOnly.ParseExact(Grid.MinToSqlTime(min), "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private static int BreakFor(string role, double startMin, double endMin) =>
        (int)Breaks.BreakForRole(role, (endMin - startMin) / 60).TotalMinutes;

    private async Task<(string Name, string Role)?> EmployeeAsync(RosterDbContext db, Guid storeId, Guid employeeId) =>
        await db.Employees.Where(e => e.Id == employeeId && e.StoreId == storeId).Select(e => new { e.Name, e.Role }).FirstOrDefaultAsync() is { } e
            ? (e.Name, e.Role)
            : null;

    private async Task<PlanResult?> RosterRefusalAsync(Guid storeId, DateOnly date)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        RequireUser();
        // The roster exists only once the plan is approved (Plan -> Review -> Roster).
        return await gate.RosterChangeRefusalAsync(storeId, Period.PeriodStartFor(date)) is { } refused ? PlanResult.Refused(refused) : null;
    }

    public async Task<PlanResult> SetShiftLockAsync(Guid storeId, DateOnly date, Guid employeeId, bool locked, double? startMin = null, double? endMin = null)
    {
        if (await RosterRefusalAsync(storeId, date) is { } refused) return refused;
        var user = RequireUser();
        var now = clock.GetUtcNow();
        await using (var db = contexts.CreateSystem("setShiftLock: generated_shifts writes (the admin client)"))
        {
            var rows = await db.GeneratedShifts.Where(s => s.StoreId == storeId && s.Date == date && s.EmployeeId == employeeId).ToListAsync();
            foreach (var r in rows)
            {
                r.Locked = locked;
                r.LockedBy = locked ? user.Id : null;
                r.LockedAt = locked ? now : null;
            }
            if (rows.Count == 0)
            {
                if (!locked) return PlanResult.Done; // never pinned - nothing to unlock
                if (startMin is not { } start || endMin is not { } end)
                    return PlanResult.Refused("No persisted shift to lock and no times supplied.");
                if (await EmployeeAsync(db, storeId, employeeId) is not { } emp) return PlanResult.Refused("Employee not found for this store.");
                db.GeneratedShifts.Add(new GeneratedShift
                {
                    StoreId = storeId, Date = date, EmployeeId = employeeId, EmployeeName = emp.Name, Role = emp.Role,
                    StartTime = SqlTime(start), EndTime = SqlTime(end), BreakMinutes = BreakFor(emp.Role, start, end),
                    Locked = true, LockedBy = user.Id, LockedAt = now,
                });
            }
            await db.SaveChangesAsync();
        }
        // The roster changed under the held snapshot - refresh it so the edit shows
        // without an explicit regenerate.
        await rosterRebuild.ComputeAndStoreRosterSnapshotAsync(storeId, Period.PeriodStartFor(date), user.Id);
        await AuditAsync(storeId, date, user, locked ? "lock_shift" : "unlock_shift", new { employeeId });
        return PlanResult.Done;
    }

    public Task<PlanResult> UpdateShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin) =>
        SaveShiftAsync(storeId, date, employeeId, employeeId, startMin, endMin, "edit_shift", new { employeeId, startMin, endMin }, swap: false);

    public Task<PlanResult> SaveShiftRowAsync(Guid storeId, DateOnly date, Guid originalEmployeeId, Guid employeeId, double startMin, double endMin) =>
        SaveShiftAsync(storeId, date, originalEmployeeId, employeeId, startMin, endMin, "edit_shift", new { originalEmployeeId, employeeId, startMin, endMin }, swap: true);

    // updateShift and saveShiftRow: the row's person and/or times in one write; an
    // unapproved day with no persisted row yet gets a new locked row pinning the edit
    // (the engine reads locked rows as anchors). The tiles described the OLD person/times.
    private async Task<PlanResult> SaveShiftAsync(
        Guid storeId, DateOnly date, Guid originalEmployeeId, Guid employeeId, double startMin, double endMin, string action, object detail, bool swap)
    {
        if (await RosterRefusalAsync(storeId, date) is { } refused) return refused;
        var user = RequireUser();
        if (startMin >= endMin) return PlanResult.Refused("Start time must be before end time.");
        var now = clock.GetUtcNow();
        await using (var db = contexts.CreateSystem("updateShift/saveShiftRow: generated_shifts writes (the admin client)"))
        {
            if (await EmployeeAsync(db, storeId, employeeId) is not { } emp) return PlanResult.Refused("Employee not found for this store.");
            // Swapping in a different person: they must not already be on this day.
            if (swap && employeeId != originalEmployeeId
                && await db.GeneratedShifts.AnyAsync(s => s.StoreId == storeId && s.Date == date && s.EmployeeId == employeeId))
                return PlanResult.Refused($"{emp.Name} already has a shift this day - edit that row instead.");
            var brk = BreakFor(emp.Role, startMin, endMin);
            var rows = await db.GeneratedShifts.Where(s => s.StoreId == storeId && s.Date == date && s.EmployeeId == originalEmployeeId).ToListAsync();
            foreach (var r in rows)
            {
                r.EmployeeId = employeeId;
                r.EmployeeName = emp.Name;
                r.Role = emp.Role;
                r.StartTime = SqlTime(startMin);
                r.EndTime = SqlTime(endMin);
                r.BreakMinutes = brk;
                r.Locked = true;
                r.LockedBy = user.Id;
                r.LockedAt = now;
            }
            if (rows.Count == 0)
            {
                db.GeneratedShifts.Add(new GeneratedShift
                {
                    StoreId = storeId, Date = date, EmployeeId = employeeId, EmployeeName = emp.Name, Role = emp.Role,
                    StartTime = SqlTime(startMin), EndTime = SqlTime(endMin), BreakMinutes = brk, Locked = true, LockedBy = user.Id, LockedAt = now,
                });
            }
            else
            {
                var ids = rows.Select(r => r.Id).ToList();
                db.ShiftTaskBlocks.RemoveRange(await db.ShiftTaskBlocks.Where(b => ids.Contains(b.ShiftId)).ToListAsync());
            }
            await db.SaveChangesAsync();
        }
        await rosterRebuild.ComputeAndStoreRosterSnapshotAsync(storeId, Period.PeriodStartFor(date), user.Id);
        await AuditAsync(storeId, date, user, action, detail);
        return PlanResult.Done;
    }

    public async Task<PlanResult> RemoveShiftAsync(Guid storeId, DateOnly date, Guid employeeId)
    {
        if (await RosterRefusalAsync(storeId, date) is { } refused) return refused;
        var user = RequireUser();
        await using (var db = contexts.CreateSystem("removeShift: generated_shifts delete (the admin client)"))
        {
            var rows = await db.GeneratedShifts.Where(s => s.StoreId == storeId && s.Date == date && s.EmployeeId == employeeId).ToListAsync();
            if (rows.Count == 0)
            {
                return PlanResult.Refused(
                    "This shift was placed by the engine and the day is not approved - regenerating would just re-place it. Edit its times instead, or mark the person unavailable / grant a day off and regenerate.");
            }
            db.GeneratedShifts.RemoveRange(rows);
            await db.SaveChangesAsync();
        }
        await rosterRebuild.ComputeAndStoreRosterSnapshotAsync(storeId, Period.PeriodStartFor(date), user.Id);
        await AuditAsync(storeId, date, user, "remove_shift", new { employeeId });
        return PlanResult.Done;
    }

    public async Task<PlanResult> AddShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin)
    {
        if (await RosterRefusalAsync(storeId, date) is { } refused) return refused;
        var user = RequireUser();
        if (startMin >= endMin) return PlanResult.Refused("Start time must be before end time.");
        await using (var db = contexts.CreateSystem("addShift: generated_shifts insert (the admin client)"))
        {
            if (await EmployeeAsync(db, storeId, employeeId) is not { } emp) return PlanResult.Refused("Employee not found for this store.");
            if (await db.GeneratedShifts.AnyAsync(s => s.StoreId == storeId && s.Date == date && s.EmployeeId == employeeId))
                return PlanResult.Refused("That person already has a shift this day - edit it instead.");
            db.GeneratedShifts.Add(new GeneratedShift
            {
                StoreId = storeId, Date = date, EmployeeId = employeeId, EmployeeName = emp.Name, Role = emp.Role,
                StartTime = SqlTime(startMin), EndTime = SqlTime(endMin), BreakMinutes = BreakFor(emp.Role, startMin, endMin),
                Locked = true, LockedBy = user.Id, LockedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }
        await rosterRebuild.ComputeAndStoreRosterSnapshotAsync(storeId, Period.PeriodStartFor(date), user.Id);
        await AuditAsync(storeId, date, user, "add_shift", new { employeeId, startMin, endMin });
        return PlanResult.Done;
    }

    public async Task<PlanResult> ApproveRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, string? comment = null)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        // Published only from the approved plan, and only once rebuilt from it (owner
        // decision R5; review RC-17: approving a roster had no gate check).
        if (dates.Count > 0 && await gate.RosterChangeRefusalAsync(storeId, Period.PeriodStartFor(dates[0])) is { } refused)
            return PlanResult.Refused(refused);
        var user = RequireUser();
        var result = await rosterApproval.PublishRosterAsync(storeId, dates, user.Id, comment);
        return new PlanResult(result.Ok, result.Error);
    }

    public async Task<PlanResult> WithdrawRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates)
    {
        if (await scope.DenyStoreWriteAsync(storeId) is { } denied) return PlanResult.Refused(denied);
        var user = RequireUser();
        var result = await rosterApproval.UnpublishRosterAsync(storeId, dates, user.Id);
        return new PlanResult(result.Ok, result.Error);
    }

    // roster_audit, written as the TypeScript admin client writes it.
    private async Task AuditAsync(Guid storeId, DateOnly date, ScopedUser user, string action, object detail)
    {
        await using var db = contexts.CreateSystem("roster_audit insert (the admin client)");
        db.RosterAudit.Add(new RosterAudit
        {
            StoreId = storeId,
            Date = date,
            UserId = user.Id,
            Action = action,
            Detail = System.Text.Json.JsonSerializer.Serialize(detail),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}
