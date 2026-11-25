// New: a stand-in for the Plan page's service, fed by invented data and held in memory,
// so the page can be built and looked at before the data services it reads land. The
// real service (PlanService) replaces it in PlanModule.

using System.Collections.Concurrent;
using Roster.Engine.Core;

namespace Roster.Web.Features.Plan;

/// <summary>
/// FAKE: invented stores, an invented signed-in store manager (of the first store) and an
/// in-memory held plan per store and fortnight. Generate runs in steps, as the real one
/// does, sealing invented days. Nothing is saved anywhere.
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

    private sealed record FakeDay(DateOnly Date, bool Closed, double Benchmark, double Worked, int Shifts, int Errors, int Warnings, double NonOp);

    private sealed class Held
    {
        public required List<FakeDay> Days { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public int Version { get; set; }
        public DateTimeOffset? RosterAt { get; set; }
    }

    private readonly ConcurrentDictionary<(Guid, DateOnly), Held> _held = new();
    private readonly ConcurrentDictionary<(Guid, DateOnly), bool> _phOn = new();
    private readonly ConcurrentDictionary<(Guid, DateOnly), bool> _planApproved = new();

    private static string? Deny(Guid storeId) => storeId == ViewerStore ? null : DeniedScope;

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
        var isPast = periodStart < Period.CurrentPeriodStart(today);
        _held.TryGetValue((store.Id, periodStart), out var held);
        var locked = !peer && _planApproved.GetValueOrDefault((store.Id, periodStart));

        PlanFortnightFigures? figures = null;
        var chips = new List<PlanDayChip>();
        if (held is not null)
        {
            var days = held.Days.Select(d => new PlanDayFigures
            {
                Date = d.Date,
                Closed = d.Closed,
                Planned = !d.Closed,
                Ok = d.Closed || (d.Errors == 0 && Math.Abs(d.Worked - d.Benchmark) <= 1 + 1e-9),
                Benchmark = d.Benchmark,
                Worked = d.Worked,
                Variance = d.Worked - d.Benchmark,
                Shifts = d.Shifts,
                Errors = d.Errors,
                Warnings = d.Warnings,
            }).ToList();
            var planned = days.Where(d => d.Planned).ToList();
            if (!peer)
            {
                figures = new PlanFortnightFigures
                {
                    Days = days,
                    OkDays = days.Count(d => d.Ok),
                    ErrorDays = days.Count(d => !d.Ok),
                    PlannedDays = planned.Count,
                    WithinDays = planned.Count(d => Math.Abs(d.Variance) <= 1 + 1e-9),
                    Benchmark = days.Sum(d => d.Benchmark),
                    Worked = days.Sum(d => d.Worked),
                    NonOp = held.Days.Sum(d => d.NonOp),
                    Shifts = days.Sum(d => d.Shifts),
                    Errors = days.Sum(d => d.Errors),
                    Warnings = days.Sum(d => d.Warnings),
                };
            }
            foreach (var d in days)
            {
                var f = figures?.Days.FirstOrDefault(x => x.Date == d.Date);
                var tone = f is null || f.Closed ? PlanChipTone.Neutral : d.Ok ? PlanChipTone.Ok : PlanChipTone.Error;
                var note = f is null ? null : f.Closed ? "Closed" : f.Planned ? PageFigures.Signed(f.Variance) : "No plan";
                var status = f is null
                    ? null
                    : f.Closed
                        ? "closed"
                        : $"{(d.Ok ? "OK" : "has an error")}{(f.Planned ? $", {PageFigures.Signed(f.Variance)} h against the benchmark" : ", no plan")}";
                chips.Add(new PlanDayChip(d.Date, $"/plan/generate?store={store.Id}&date={d.Date.ToIso()}", tone, note, status));
            }
        }

        return Task.FromResult(new PlanPageModel
        {
            Stores = Stores,
            SelectedStore = store.Id,
            StoreRow = store,
            Peer = peer,
            IsReadOnly = peer,
            IsStoreTier = true,
            PeriodStart = periodStart,
            SelectedDate = selectedDate,
            IsPastPeriod = isPast,
            OverduePeriods = [],
            Locked = locked,
            HasSnapshot = held is not null,
            GeneratedAt = held?.GeneratedAt,
            GeneratorName = held is not null && !peer ? ViewerName : null,
            Figures = figures,
            DayChips = chips,
            IsPublicHolidayDay = !peer && Holidays.Contains(selectedDate),
            PhReallocated = _phOn.GetValueOrDefault((store.Id, selectedDate)),
            NightFill = store.Code == "G102",
        });
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
        var days = dates.Select(d => Invent(storeId, d)).ToList();
        var stamp = new PartStamp(storeId, periodStart, runId, ViewerId);
        return new PlanStepResult(true, Part: GenerateParts.SealPlanPart(partKey.Key, stamp, days, time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    public Task<PlanResult> FinishPlanGenerationAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<string> parts, PlanChangeReason? reason = null)
    {
        if (GenerationRefusal(storeId, periodStart) is { } refused) return Task.FromResult(PlanResult.Refused(refused));
        var dates = Period.PeriodDatesFrom(periodStart);
        var opened = GenerateParts.OpenPlanParts<FakeDay>(
            partKey.Key, [.. parts], new PartStamp(storeId, periodStart, runId, ViewerId), dates, d => d.Date, time.GetUtcNow().ToUnixTimeMilliseconds());
        if (!opened.Ok) return Task.FromResult(PlanResult.Refused(opened.Error!));
        _held.AddOrUpdate(
            (storeId, periodStart),
            _ => new Held { Days = [.. opened.Days!], GeneratedAt = time.GetUtcNow(), Version = 1 },
            (_, prior) => new Held { Days = [.. opened.Days!], GeneratedAt = time.GetUtcNow(), Version = prior.Version + 1, RosterAt = prior.RosterAt });
        return Task.FromResult(PlanResult.Done);
    }

    // Invented figures, the same for a store and date every time.
    private FakeDay Invent(Guid storeId, DateOnly date)
    {
        var seed = HashCode.Combine(storeId, date.DayNumber) & 0x7fffffff;
        var rng = new Random(seed);
        var closed = date is { Month: 12, Day: 25 };
        if (closed) return new FakeDay(date, true, 0, 0, 0, 0, 0, 0);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var benchmark = Math.Round((weekend ? 58 : 46) + rng.NextDouble() * 8, 2);
        var gap = (rng.Next(0, 9) - 4) * 0.25; // -1.00 .. +1.00 h
        if (rng.Next(0, 6) == 0) gap += 1.5; // now and then, outside the tolerance
        var worked = benchmark + gap;
        var ph = _phOn.GetValueOrDefault((storeId, date));
        if (ph) worked -= 2;
        var errors = Math.Abs(worked - benchmark) > 1 ? 1 : 0;
        return new FakeDay(date, false, benchmark, worked, weekend ? 12 : 10, errors, rng.Next(0, 3), rng.Next(0, 3) * 0.75);
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
        if (edits.Count == 0) return Task.FromResult(PlanResult.Refused("A day needs at least one shift."));
        held.Version++;
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
