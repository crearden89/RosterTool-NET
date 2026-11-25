// Port of src/lib/planSnapshots.ts - the plan's solving and re-checking half
// (readSnapshots' plan with withDayChecks, readPeerPlanSnapshot's parse,
// writePlanSnapshot, computePlanDays, storePlanSnapshot,
// computeAndStorePlanSnapshot). The storage is D's IPlanSnapshotStore: a held
// payload is the engine's JSON, kept as text.

using System.Text.Json;
using System.Text.RegularExpressions;
using Roster.Data.Entities;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Planning;
using Roster.Data.Snapshots;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.Held;

// ─── Held plan snapshots ─────────────────────────────────────────────────────
// The Plan/Generate page never solves on render: generating is an EXPLICIT
// action that solves once and persists the result here, and every later visit
// is a single read of the held snapshot. The plan a manager looked at
// yesterday is byte-for-byte the plan they see today - it only changes when
// someone presses Regenerate (or makes an edit that must reflect immediately,
// like the PH reallocation toggle).

public sealed record PlanSnapshotDay
{
    public DateOnly Date { get; init; }
    public bool Ok { get; init; }
    public Stage1Computation Result { get; init; } = new();
    public PlanShiftComputation? Plan { get; init; }
}

public sealed record PlanSnapshot
{
    public List<PlanSnapshotDay> Days { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; }
    // Who pressed Generate (app_users id), when the read carries it. An edit
    // keeps the plan's generator: only a generate replaces it.
    public Guid? GeneratedBy { get; init; }
    /// <summary>The optimistic-concurrency token (see WritePlanSnapshotAsync).</summary>
    public int Version { get; init; } = 1;
}

public interface IHeldPlans
{
    /// <summary>
    /// readSnapshots' plan: the store's held plan for the fortnight, as the caller, with
    /// any day held from before the day checker re-read by it; null when none is held.
    /// </summary>
    Task<PlanSnapshot?> ReadPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>
    /// readPeerPlanSnapshot: another store's held plan, read as the system - only the
    /// plan, without its generator. Callers must strip flags before showing anything.
    /// </summary>
    Task<PlanSnapshot?> ReadPeerPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>
    /// writePlanSnapshot: replace the held plan's days, only while it still carries
    /// <paramref name="expectedVersion"/> (an edit on a stale plan is refused).
    /// </summary>
    Task<SnapshotWriteResult> WritePlanSnapshotAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<PlanSnapshotDay> days, int expectedVersion, CancellationToken ct = default);

    /// <summary>computePlanDays: solve Stage 1 and the plan shifts for each date (one memo scope).</summary>
    Task<List<PlanSnapshotDay>> ComputePlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, CancellationToken ct = default);

    /// <summary>storePlanSnapshot: hold freshly computed days as the fortnight's plan (bumps the version).</summary>
    Task<PlanSnapshot> StorePlanSnapshotAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<PlanSnapshotDay> days, Guid? userId = null, CancellationToken ct = default);

    /// <summary>computeAndStorePlanSnapshot: solve the fortnight's plan and hold it.</summary>
    Task<PlanSnapshot> ComputeAndStorePlanSnapshotAsync(Guid storeId, DateOnly periodStart, Guid? userId = null, CancellationToken ct = default);
}

public sealed partial class HeldPlans(
    IPlanSnapshotStore store,
    IStage1Loader stage1,
    IPlanShiftsLoader planShifts,
    IDayCheckOptionsLoader checkOptions,
    IPlanPeriods periods,
    TimeProvider clock) : IHeldPlans
{
    private sealed record PlanPayload
    {
        public List<PlanSnapshotDay> Days { get; init; } = [];
    }

    internal static List<PlanSnapshotDay> ParseDays(string payload) =>
        JsonSerializer.Deserialize<PlanPayload>(payload, RosterJson.Options)?.Days ?? [];

    internal static string PayloadOf(IReadOnlyList<PlanSnapshotDay> days) =>
        JsonSerializer.Serialize(new PlanPayload { Days = days.ToList() }, RosterJson.Options);

    public async Task<PlanSnapshot?> ReadPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var held = await store.ReadAsync(storeId, periodStart, ct);
        if (held.Plan is not { } row) return null;
        return await WithDayChecksAsync(storeId, new PlanSnapshot
        {
            Days = ParseDays(row.Payload),
            GeneratedAt = row.GeneratedAt,
            GeneratedBy = row.GeneratedBy,
            Version = row.Version,
        }, ct);
    }

    // The painter's flags a plan held from before the day checker carries: the
    // checker reads the same things from the shifts, with the right causes.
    private static readonly Regex[] LegacyPainterFlags =
    [
        LegacyQuarterHours(),
        new("^No manager rostered"),
        new(@"^Fewer than \d+ people rostered"),
        new("^Junior rostered with fewer than"),
        new("on nobody's shift"),
        new("has nobody on it"),
    ];

    [GeneratedRegex("quarter-hours? with no work .* that could not be cleared within the rules$")]
    private static partial Regex LegacyQuarterHours();

    /// <summary>
    /// A plan held from before the day checker (build phase 4) is re-read with it,
    /// so its status, the Plan page and the Review all hold it to today's checks.
    /// Days already checked are left exactly as held.
    /// </summary>
    private async Task<PlanSnapshot> WithDayChecksAsync(Guid storeId, PlanSnapshot plan, CancellationToken ct)
    {
        if (plan.Days.All(d => d.Plan is not { HasData: true } || d.Plan.Checks is not null)) return plan;
        var days = new List<PlanSnapshotDay>();
        foreach (var d in plan.Days)
        {
            if (d.Plan is not { HasData: true } p || p.Checks is not null || !d.Result.HasData)
            {
                days.Add(d);
                continue;
            }
            var check = DayChecker.CheckPlanDay(d.Result, p.Shifts.Select(s => s.ToChecked()).ToList(),
                await checkOptions.DayCheckOptionsAsync(storeId, d.Date, ct));
            var notes = PlanShifts.PlannerNotes(p.Flags.Where(f => !LegacyPainterFlags.Any(x => x.IsMatch(f))).ToList(), p.Shifts);
            var planDay = p with { Flags = [.. notes, .. check.Flags], Notes = notes, Checks = check.Flags, Hours = check.Hours };
            days.Add(d with { Plan = planDay, Ok = d.Result.Closed || DayChecker.DayIsOk([.. d.Result.Flags, .. planDay.Flags]) });
        }
        return plan with { Days = days };
    }

    // The one deliberate gate through which another store's held PLAN is read
    // (peer-visible Plan page): never the roster, never who generated it.
    public async Task<PlanSnapshot?> ReadPeerPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var row = await store.ReadPeerPlanAsync(storeId, periodStart, ct);
        return row is null ? null : new PlanSnapshot { Days = ParseDays(row.Payload), GeneratedAt = row.GeneratedAt, Version = row.Version };
    }

    // Persist a plan edit with optimistic concurrency. The update only succeeds
    // while the row still carries `expectedVersion`; if someone else saved in
    // between, the write is rejected instead of silently overwriting their change.
    public Task<SnapshotWriteResult> WritePlanSnapshotAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<PlanSnapshotDay> days, int expectedVersion, CancellationToken ct = default) =>
        store.WritePlanPayloadAsync(storeId, periodStart, PayloadOf(days), expectedVersion, ct);

    // Solve one fortnight's plan - Stage 1 then the shaped Task-view shifts - for
    // every date. One memo scope for the whole build: the store's reference data
    // is read once, not once per date.
    public Task<List<PlanSnapshotDay>> ComputePlanDaysAsync(Guid storeId, IReadOnlyList<DateOnly> dates, CancellationToken ct = default) =>
        RequestMemo.Run(async () =>
        {
            var days = await Task.WhenAll(dates.Select(async date =>
            {
                var result = await stage1.ComputeStage1Async(storeId, date, ct);
                var plan = result.HasData ? await planShifts.ComputePlanShiftsAsync(storeId, date, ct) : null;
                var ok = result.Closed || (result.HasData && plan is { HasData: true } && DayChecker.DayIsOk([.. result.Flags, .. plan.Flags]));
                return new PlanSnapshotDay { Date = date, Ok = ok, Result = result, Plan = plan };
            }));
            return days.ToList();
        });

    public async Task<PlanSnapshot> StorePlanSnapshotAsync(
        Guid storeId, DateOnly periodStart, IReadOnlyList<PlanSnapshotDay> days, Guid? userId = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var version = await store.StoreAsync(storeId, periodStart, SnapshotKinds.Plan, PayloadOf(days), now, userId, ct);
        return new PlanSnapshot { Days = days.ToList(), GeneratedAt = now, Version = version };
    }

    // Compute every date of the fortnight and persist the result as the held plan.
    // Called by the explicit Generate action only - never from a page render.
    public async Task<PlanSnapshot> ComputeAndStorePlanSnapshotAsync(Guid storeId, DateOnly periodStart, Guid? userId = null, CancellationToken ct = default)
    {
        var dates = periods.GetPlanPeriodDates(periodStart);
        return await StorePlanSnapshotAsync(storeId, periodStart, await ComputePlanDaysAsync(storeId, dates, ct), userId, ct);
    }
}
