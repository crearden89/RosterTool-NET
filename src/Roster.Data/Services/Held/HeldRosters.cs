// Port of src/lib/planSnapshots.ts - the roster's half: readSnapshots' roster
// (withRosterLabels) and computeAndStoreRosterSnapshot. The storage is D's
// IPlanSnapshotStore.

using System.Text.Json;
using Roster.Data.Entities;
using Roster.Data.Services.Cache;
using Roster.Data.Services.Rostering;
using Roster.Data.Services.SubTasks;
using Roster.Data.Snapshots;
using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.SubTasks;

namespace Roster.Data.Services.Held;

/// <summary>The held roster: built FROM the held plan once its review is approved.</summary>
public sealed record RosterSnapshot
{
    /// <summary>Each date's roster, in date order (held as [date, day] pairs, as the TypeScript did).</summary>
    public JsMap<DateOnly, RosterComputation> ByDate { get; init; } = new();
    public List<string> FortnightFlags { get; init; } = [];
    // Where each person landed against contract (the engine's contract outcomes,
    // the numbers behind the contract flags). Null on rosters held before them.
    public List<ContractOutcome>? Outcomes { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public int Version { get; init; } = 1;
}

/// <summary>A fortnight's held plan and roster, as readSnapshots returns them.</summary>
public sealed record HeldSnapshotPair(PlanSnapshot? Plan, RosterSnapshot? Roster);

public interface IHeldRosters
{
    /// <summary>
    /// readSnapshots: the fortnight's held plan (re-checked where held before the day checker)
    /// and roster (labelled where held before sub-tasks), as the caller.
    /// </summary>
    Task<HeldSnapshotPair> ReadSnapshotsAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>readSnapshots' roster alone; null when none is held.</summary>
    Task<RosterSnapshot?> ReadRosterAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default);

    /// <summary>
    /// computeAndStoreRosterSnapshot: build the roster from the HELD plan and hold it. Null when
    /// no plan is held. Generate on the Roster page calls it, once its plan is approved.
    /// </summary>
    Task<RosterSnapshot?> ComputeAndStoreRosterSnapshotAsync(Guid storeId, DateOnly periodStart, Guid? userId = null, CancellationToken ct = default);
}

public sealed class HeldRosters(
    IPlanSnapshotStore store,
    IHeldPlans plans,
    IRosterLoader rosters,
    ISubtaskLabels labels,
    TimeProvider clock) : IHeldRosters
{
    private sealed record RosterPayload
    {
        public JsMap<DateOnly, RosterComputation> ByDate { get; init; } = new();
        public List<string> FortnightFlags { get; init; } = [];
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<ContractOutcome>? Outcomes { get; init; }
    }

    internal static RosterSnapshot ParseRoster(HeldSnapshot row)
    {
        var p = JsonSerializer.Deserialize<RosterPayload>(row.Payload, RosterJson.Options) ?? new RosterPayload();
        return new RosterSnapshot { ByDate = p.ByDate, FortnightFlags = p.FortnightFlags, Outcomes = p.Outcomes, GeneratedAt = row.GeneratedAt, Version = row.Version };
    }

    public async Task<HeldSnapshotPair> ReadSnapshotsAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default)
    {
        var held = await store.ReadAsync(storeId, periodStart, ct);
        var plan = held.Plan is null ? null : await plans.ReadPlanAsync(storeId, periodStart, ct);
        RosterSnapshot? roster = null;
        if (held.Roster is { } row)
        {
            roster = ParseRoster(row);
            // A label must never stop the roster showing: unlabelled if it fails.
            try
            {
                roster = await WithRosterLabelsAsync(storeId, roster, plan, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
            }
        }
        return new HeldSnapshotPair(plan, roster);
    }

    public async Task<RosterSnapshot?> ReadRosterAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) =>
        (await ReadSnapshotsAsync(storeId, periodStart, ct)).Roster;

    /// <summary>
    /// A roster held from before sub-tasks is labelled when it is read, the way a new one is
    /// labelled when it is built: the held plan's labels on the tiles that come unchanged from
    /// it, the rules for the rest (Reset from the held plan's Stage 1 record). Days already
    /// labelled are left exactly as held; nothing is written back.
    /// </summary>
    private async Task<RosterSnapshot> WithRosterLabelsAsync(Guid storeId, RosterSnapshot roster, PlanSnapshot? plan, CancellationToken ct)
    {
        var heldOf = new Dictionary<DateOnly, PlanSnapshotDay>();
        foreach (var d in plan?.Days ?? []) heldOf[d.Date] = d;
        var byDate = new JsMap<DateOnly, RosterComputation>();
        foreach (var (date, day) in roster.ByDate)
        {
            if (!day.HasData || day.Shifts.Count == 0)
            {
                byDate.Set(date, day);
                continue;
            }
            heldOf.TryGetValue(date, out var held);
            var shifts = await labels.WithDayLabelsAsync(storeId, date, held is null ? null : ResetSource.From(held.Result),
                held?.Plan?.Shifts ?? [], day.Shifts, ct);
            byDate.Set(date, ReferenceEquals(shifts, day.Shifts) ? day : day with { Shifts = shifts.ToList() });
        }
        return roster with { ByDate = byDate };
    }

    // Build the roster from the held plan (the one its review approved) and hold
    // it. One memo scope for the build: the reference data is read once.
    public async Task<RosterSnapshot?> ComputeAndStoreRosterSnapshotAsync(Guid storeId, DateOnly periodStart, Guid? userId = null, CancellationToken ct = default)
    {
        var built = await RequestMemo.Run(async () =>
        {
            var plan = await plans.ReadPlanAsync(storeId, periodStart, ct);
            if (plan is null) return null;
            var heldDays = plan.Days.Select(d => new HeldPlanDay(d.Date, d.Result, d.Plan)).ToList();
            var fortnight = await rosters.ComputeRosterFortnightAsync(storeId, periodStart, heldDays, ct);
            // Every Reset, Load Running, Cleaning and Specials tile of the roster gets
            // its sub-task - labels only, so nothing the roster checks changes. Every
            // tile the roster keeps from the plan has the plan's label, the rules' or
            // a manager's; the pass that labels the plan labels the rest, on the
            // roster's own painted tiles (SubTasks/Day LabelRosterDay).
            var heldOf = plan.Days.ToDictionary(d => d.Date);
            var byDate = new JsMap<DateOnly, RosterComputation>();
            foreach (var (date, day) in fortnight.ByDate)
            {
                if (!day.HasData || day.Shifts.Count == 0)
                {
                    byDate.Set(date, day);
                    continue;
                }
                heldOf.TryGetValue(date, out var held);
                var shifts = await labels.LabelRosterDayAsync(storeId, date, held is null ? null : ResetSource.From(held.Result),
                    held?.Plan?.Shifts ?? [], day.Shifts, ct);
                byDate.Set(date, day with { Shifts = shifts });
            }
            return new { fortnight, byDate };
        });
        if (built is null) return null;
        var now = clock.GetUtcNow();
        var payload = JsonSerializer.Serialize(new RosterPayload
        {
            ByDate = built.byDate,
            FortnightFlags = built.fortnight.FortnightFlags,
            Outcomes = built.fortnight.Outcomes,
        }, RosterJson.Options);
        var version = await store.StoreAsync(storeId, periodStart, SnapshotKinds.Roster, payload, now, userId, ct);
        return new RosterSnapshot
        {
            ByDate = built.byDate,
            FortnightFlags = built.fortnight.FortnightFlags,
            Outcomes = built.fortnight.Outcomes,
            GeneratedAt = now,
            Version = version,
        };
    }
}
