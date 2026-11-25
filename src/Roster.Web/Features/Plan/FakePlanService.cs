// New: a stand-in for the Plan page's service, fed by invented data and held in memory,
// so the page can be built and looked at before the data services it reads land. The
// real service (PlanService) replaces it in PlanModule.

using System.Collections.Concurrent;
using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;

namespace Roster.Web.Features.Plan;

/// <summary>
/// FAKE: invented stores, an invented signed-in store manager (of the first store) and an
/// in-memory held plan per store and fortnight (FakePlanDays). Generate runs in steps, as
/// the real one does, sealing invented days; an edit is checked and re-checked by the same
/// code as the real one (PlanDayEdits). Nothing is saved anywhere.
/// </summary>
public sealed class FakePlanService(PlanPartKey partKey, TimeProvider time) : IPlanService
{
    // The refusals worded as src/lib/review/gate.ts and src/lib/authz.ts word them.
    public const string PlanLocked =
        "This fortnight's plan is approved and locked. To change it, ask for it to be unlocked on the Review page.";
    public const string RosterLocked =
        "The roster can't be built until this fortnight's plan is approved: the store submits it on the Review page and its EMSO approves it.";
    public const string DeniedScope = "You don't have access to that store.";

    public static readonly IReadOnlyList<PlanStore> Stores =
    [
        new(Guid.Parse("0b9c1f52-6c1e-4b8e-9a51-2f6a3e1d0101"), "G101", "Northside", Guid.Parse("a0000000-0000-4000-8000-000000000001")),
        new(Guid.Parse("0b9c1f52-6c1e-4b8e-9a51-2f6a3e1d0102"), "G102", "Riverbend", Guid.Parse("a0000000-0000-4000-8000-000000000001")),
        new(Guid.Parse("0b9c1f52-6c1e-4b8e-9a51-2f6a3e1d0205"), "G205", "Hillcrest", Guid.Parse("a0000000-0000-4000-8000-000000000002")),
    ];

    /// <summary>The invented viewer: a store login for the first store.</summary>
    public static readonly Guid ViewerId = Guid.Parse("c0000000-0000-4000-8000-0000000000aa");

    public const string ViewerName = "northside.manager";

    private static readonly Guid ViewerStore = Stores[0].Id;

    // Invented public holidays, for the reallocation toggle.
    private static readonly HashSet<DateOnly> Holidays =
        [new(2026, 12, 25), new(2026, 12, 26), new(2026, 12, 28), new(2027, 1, 1), new(2027, 1, 26)];

    private sealed class Held
    {
        public required PlanSnapshot Plan { get; set; }
        public DateTimeOffset? RosterAt { get; set; }
    }

    private readonly ConcurrentDictionary<(Guid, DateOnly), Held> _held = new();
    private readonly ConcurrentDictionary<(Guid, DateOnly), bool> _phOn = new();
    private readonly ConcurrentDictionary<(Guid, DateOnly), bool> _planApproved = new();

    private static string? Deny(Guid storeId) => storeId == ViewerStore ? null : DeniedScope;

    // An invented trade curve: a morning rise, a lunchtime peak and a late-afternoon one.
    private static readonly IReadOnlyList<CurveRow> Curve = Enumerable.Range(0, 7).SelectMany(d =>
        Enumerable.Range(0, (20 * 60 - 8 * 60 - 30) / 15).Select(i =>
        {
            var t = 8 * 60 + 30 + i * 15;
            var h = t / 60.0;
            var share = 1 + 2.2 * Math.Exp(-Math.Pow(h - 12.5, 2) / 2.5) + 1.6 * Math.Exp(-Math.Pow(h - 17, 2) / 1.6) + (d >= 5 ? 0.4 : 0);
            return new CurveRow(d, Grid.MinToTime(t), share);
        })).ToList();

    public Task<PlanPageModel> LoadPageAsync(PlanPageQuery query, CancellationToken cancellationToken = default)
    {
        // The roster half of this page moved to its own route.
        if (query.View == "roster")
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(query.Store)) qs.Add($"store={Uri.EscapeDataString(query.Store)}");
            if (!string.IsNullOrEmpty(query.Date)) qs.Add($"date={Uri.EscapeDataString(query.Date)}");
            return Task.FromResult(new PlanPageModel { RedirectTo = qs.Count > 0 ? $"/plan/roster?{string.Join('&', qs)}" : "/plan/roster" });
        }

        var store = Stores.FirstOrDefault(s => s.Id.ToString() == query.Store) ?? Stores[0];
        var peer = store.Id != ViewerStore;
        var today = Dates.TodayAu(time.GetUtcNow());
        DateOnly? Parse(string? iso) => DateOnly.TryParseExact(iso, "yyyy-MM-dd", out var d) ? d : null;
        var requested = Parse(query.Period) ?? Parse(query.Date);
        var periodStart = requested is { } r ? Period.PeriodStartFor(r) : Period.PlanningPeriodStart(today);
        var selectedDate = Parse(query.Date) ?? periodStart;
        _held.TryGetValue((store.Id, periodStart), out var held);
        var snapshot = held?.Plan;
        var selectedDay = snapshot?.Days.FirstOrDefault(d => d.Date == selectedDate);

        return Task.FromResult(PlanPageComposer.Compose(new PlanPageReads
        {
            Stores = Stores,
            SelectedStore = store.Id,
            Peer = peer,
            ReadOnlyTier = false,
            IsStoreTier = true,
            PeriodStart = periodStart,
            SelectedDate = selectedDate,
            IsPastPeriod = periodStart < Period.CurrentPeriodStart(today),
            OverduePeriods = [],
            Snapshot = snapshot,
            SelectedDay = selectedDay,
            PhReallocated = _phOn.GetValueOrDefault((store.Id, selectedDate)),
            IsPublicHolidayDay = Holidays.Contains(selectedDate),
            ReviewState = _planApproved.GetValueOrDefault((store.Id, periodStart)) ? Review.PlanReviewState.Approved : Review.PlanReviewState.NotSubmitted,
            Cover = FakePlanDays.Cover,
            CurveRows = Curve,
            NightFill = store.Code == "G102",
            GeneratorName = snapshot is not null ? ViewerName : null,
        }));
    }

    private string? GenerationRefusal(Guid storeId, DateOnly periodStart) =>
        Deny(storeId) ?? (_planApproved.GetValueOrDefault((storeId, periodStart)) ? PlanLocked : null);

    public Task<PlanStartResult> StartPlanGenerationAsync(Guid storeId, DateOnly periodStart)
    {
        if (GenerationRefusal(storeId, periodStart) is { } refused) return Task.FromResult(new PlanStartResult(false, refused));
        var dates = Period.PeriodDatesFrom(Period.PeriodStartFor(periodStart));
        return Task.FromResult(new PlanStartResult(true, RunId: Guid.NewGuid().ToString(), Steps: GenerateParts.PlanSteps(dates)));
    }

    public async Task<PlanStepResult> PlanDaysStepAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<DateOnly> dates)
    {
        if (GenerationRefusal(storeId, periodStart) is { } refused) return new PlanStepResult(false, refused);
        var fortnight = Period.PeriodDatesFrom(periodStart).ToHashSet();
        if (dates.Count == 0 || dates.Count > GenerateParts.DaysPerStep || dates.Any(d => !fortnight.Contains(d)))
            return new PlanStepResult(false, "Those days are not part of this fortnight.");
        // A moment per day, so the progress shows as it would.
        await Task.Delay(TimeSpan.FromMilliseconds(250 * dates.Count), time);
        var days = dates.Select(d => FakePlanDays.Invent(storeId, d, _phOn.GetValueOrDefault((storeId, d)))).ToList();
        var stamp = new PartStamp(storeId, periodStart, runId, ViewerId);
        return new PlanStepResult(true, Part: GenerateParts.SealPlanPart(partKey.Key, stamp, days, time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    public Task<PlanResult> FinishPlanGenerationAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<string> parts, PlanChangeReason? reason = null)
    {
        if (GenerationRefusal(storeId, periodStart) is { } refused) return Task.FromResult(PlanResult.Refused(refused));
        var dates = Period.PeriodDatesFrom(periodStart);
        var opened = GenerateParts.OpenPlanParts<PlanSnapshotDay>(
            partKey.Key, [.. parts], new PartStamp(storeId, periodStart, runId, ViewerId), dates, d => d.Date, time.GetUtcNow().ToUnixTimeMilliseconds());
        if (!opened.Ok) return Task.FromResult(PlanResult.Refused(opened.Error!));
        PlanSnapshot Snapshot(int version) =>
            new() { Days = [.. opened.Days!], GeneratedAt = time.GetUtcNow(), GeneratedBy = ViewerId, Version = version };
        _held.AddOrUpdate(
            (storeId, periodStart),
            _ => new Held { Plan = Snapshot(1) },
            (_, prior) => new Held { Plan = Snapshot(prior.Plan.Version + 1), RosterAt = prior.RosterAt });
        return Task.FromResult(PlanResult.Done);
    }

    public Task<PlanResult> GenerateRosterAsync(Guid storeId, DateOnly periodStart)
    {
        if (Deny(storeId) is { } denied) return Task.FromResult(PlanResult.Refused(denied));
        if (!_planApproved.GetValueOrDefault((storeId, periodStart))) return Task.FromResult(PlanResult.Refused(RosterLocked));
        if (!_held.TryGetValue((storeId, periodStart), out var held)) return Task.FromResult(PlanResult.Refused("Generate the plan first."));
        held.RosterAt = time.GetUtcNow();
        return Task.FromResult(PlanResult.Done);
    }

    public Task<DateTimeOffset?> HeldRosterAtAsync(Guid storeId, DateOnly periodStart) =>
        Task.FromResult(_held.TryGetValue((storeId, periodStart), out var held) ? held.RosterAt : null);

    public Task<PlanResult> EditPlanDayAsync(Guid storeId, DateOnly periodStart, DateOnly date, IReadOnlyList<PlanShiftEdit> edits)
    {
        if (Deny(storeId) is { } denied) return Task.FromResult(PlanResult.Refused(denied));
        if (_planApproved.GetValueOrDefault((storeId, periodStart))) return Task.FromResult(PlanResult.Refused(PlanLocked));
        if (!_held.TryGetValue((storeId, periodStart), out var held)) return Task.FromResult(PlanResult.Refused("No held plan to edit - generate first."));
        var day = held.Plan.Days.FirstOrDefault(d => d.Date == date);
        if (day?.Plan is null) return Task.FromResult(PlanResult.Refused("No plan for this date."));
        var (error, rebuilt) = PlanDayEdits.Rebuild(day, edits);
        if (error is not null) return Task.FromResult(PlanResult.Refused(error));
        // (The real service labels the tiles here; the fake keeps the labels it was sent.)
        if (PlanDayEdits.BreakError(rebuilt!.Shifts) is { } breaks) return Task.FromResult(PlanResult.Refused(breaks));
        var edited = PlanDayEdits.Recheck(day, rebuilt.Shifts, new DayCheckOptions { Cover = FakePlanDays.Cover });
        held.Plan = held.Plan with
        {
            Days = held.Plan.Days.Select(d => d.Date == date ? edited : d).ToList(),
            Version = held.Plan.Version + 1,
        };
        return Task.FromResult(PlanResult.Done);
    }

    public Task<PlanResult> ApprovePlanAsync(Guid storeId, DateOnly date) => Task.FromResult(Deny(storeId) is { } d ? PlanResult.Refused(d) : PlanResult.Done);

    public Task<PlanResult> WithdrawPlanAsync(Guid storeId, DateOnly date) => Task.FromResult(Deny(storeId) is { } d ? PlanResult.Refused(d) : PlanResult.Done);

    public Task<PlanResult> SetPhReallocationAsync(Guid storeId, DateOnly date, bool on)
    {
        if (Deny(storeId) is { } denied) return Task.FromResult(PlanResult.Refused(denied));
        if (_planApproved.GetValueOrDefault((storeId, Period.PeriodStartFor(date)))) return Task.FromResult(PlanResult.Refused(PlanLocked));
        _phOn[(storeId, date)] = on;
        return Task.FromResult(PlanResult.Done);
    }

    public Task<PlanResult> SetShiftLockAsync(Guid storeId, DateOnly date, Guid employeeId, bool locked, double? startMin = null, double? endMin = null) =>
        RosterChange(storeId, date);

    public Task<PlanResult> UpdateShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin) =>
        startMin >= endMin ? Task.FromResult(PlanResult.Refused("Start time must be before end time.")) : RosterChange(storeId, date);

    public Task<PlanResult> SaveShiftRowAsync(Guid storeId, DateOnly date, Guid originalEmployeeId, Guid employeeId, double startMin, double endMin) =>
        startMin >= endMin ? Task.FromResult(PlanResult.Refused("Start time must be before end time.")) : RosterChange(storeId, date);

    public Task<PlanResult> RemoveShiftAsync(Guid storeId, DateOnly date, Guid employeeId) => RosterChange(storeId, date);

    public Task<PlanResult> AddShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin) =>
        startMin >= endMin ? Task.FromResult(PlanResult.Refused("Start time must be before end time.")) : RosterChange(storeId, date);

    public Task<PlanResult> ApproveRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, string? comment = null) =>
        dates.Count == 0 ? Task.FromResult(Deny(storeId) is { } d ? PlanResult.Refused(d) : PlanResult.Done) : RosterChange(storeId, dates[0]);

    public Task<PlanResult> WithdrawRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates) =>
        Task.FromResult(Deny(storeId) is { } d ? PlanResult.Refused(d) : PlanResult.Done);

    // The roster exists only once the plan is approved (Plan -> Review -> Roster).
    private Task<PlanResult> RosterChange(Guid storeId, DateOnly date)
    {
        if (Deny(storeId) is { } denied) return Task.FromResult(PlanResult.Refused(denied));
        return Task.FromResult(_planApproved.GetValueOrDefault((storeId, Period.PeriodStartFor(date))) ? PlanResult.Done : PlanResult.Refused(RosterLocked));
    }
}
