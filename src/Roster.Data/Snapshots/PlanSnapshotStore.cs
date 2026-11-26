// Port of src/lib/planSnapshots.ts (the storage half: readSnapshots, readPeerPlanSnapshot,
// writePlanSnapshot, storeSnapshot). The engine's payload types belong to the engine;
// here a payload is the JSON text exactly as the engine produced it, so nothing is lost
// or reshaped on the way through. The solving and re-checking halves (computePlanDays,
// withDayChecks, withRosterLabels) belong with the engine loaders.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Snapshots;

/// <summary>A held plan or roster: the engine's output as JSON, when and by whom, and its version.</summary>
public sealed record HeldSnapshot(
    Guid StoreId,
    DateOnly PeriodStart,
    string Kind,
    string Payload,
    DateTimeOffset GeneratedAt,
    Guid? GeneratedBy,
    int Version);

/// <summary>A fortnight's held plan and roster (either may be missing).</summary>
public sealed record HeldSnapshots(HeldSnapshot? Plan, HeldSnapshot? Roster);

/// <summary>The outcome of a compare-and-set write.</summary>
public sealed record SnapshotWriteResult(bool Ok, string? Error = null, int? Version = null);

/// <summary>
/// The Plan page never solves on render: generating is an explicit action that solves once
/// and holds the result here, and every later visit is a single read. Every write bumps the
/// version, so an edit that carries a stale version is rejected instead of silently
/// overwriting whatever landed in between.
/// </summary>
public interface IPlanSnapshotStore
{
    /// <summary>
    /// readSnapshots: both held snapshots of a store's fortnight, as the current identity, so
    /// nothing for a store outside its visible stores.
    /// </summary>
    Task<HeldSnapshots> ReadAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// readPeerPlanSnapshot: the one deliberate gate through which another store's held PLAN
    /// is read (peer-visible Plan page). Only the plan, never the roster, and without who
    /// generated it; read as the system once the caller is signed in. Callers must strip
    /// flags before showing anything.
    /// </summary>
    Task<HeldSnapshot?> ReadPeerPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// writePlanSnapshot: compare-and-set of the plan payload. Succeeds only while the held row
    /// still carries <paramref name="expectedVersion"/>; otherwise reports the conflict.
    /// </summary>
    Task<SnapshotWriteResult> WritePlanPayloadAsync(
        Guid storeId, DateOnly periodStart, string payloadJson, int expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// storeSnapshot: hold a freshly generated plan or roster, bumping its version. Returns the
    /// new version. <paramref name="kind"/> is <see cref="SnapshotKinds.Plan"/> or <see cref="SnapshotKinds.Roster"/>.
    /// </summary>
    Task<int> StoreAsync(
        Guid storeId, DateOnly periodStart, string kind, string payloadJson, DateTimeOffset generatedAt, Guid? userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IPlanSnapshotStore"/> over plan_snapshots, as the current identity; only the peer
/// gate reads as the system.
/// </summary>
public sealed class PlanSnapshotStore(IRosterDbContextFactory contexts) : IPlanSnapshotStore
{
    /// <summary>writePlanSnapshot's conflict message, verbatim.</summary>
    public const string ConflictMessage =
        "This plan was changed by someone else while you were editing. Reload the page to pick up their version, then re-apply your changes.";

    public async Task<HeldSnapshots> ReadAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        await using var db = contexts.Create();
        var rows = await db.PlanSnapshots.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.PeriodStart == periodStart)
            .ToListAsync(cancellationToken);
        return new HeldSnapshots(
            rows.Where(r => r.Kind == SnapshotKinds.Plan).Select(ToHeld).FirstOrDefault(),
            rows.Where(r => r.Kind == SnapshotKinds.Roster).Select(ToHeld).FirstOrDefault());
    }

    public async Task<HeldSnapshot?> ReadPeerPlanAsync(Guid storeId, DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        // The gate: someone signed in, or the server itself. Which other stores a person may
        // look at this way is the peer-visible page's own access check, made before this.
        Scope.AccessContext caller;
        await using (var mine = contexts.Create()) caller = mine.Access;
        if (!caller.IsSignedIn && caller.Kind != Scope.AccessKind.System) return null;
        // Past the gate the read is the system's: the store scope rightly hides another
        // store's held plans (the TypeScript app read them with the service-role client).
        await using var db = contexts.CreateSystem("readPeerPlanSnapshot: another store's held plan, past the peer gate");
        var row = await db.PlanSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoreId == storeId && s.PeriodStart == periodStart && s.Kind == SnapshotKinds.Plan, cancellationToken);
        return row is null ? null : ToHeld(row) with { GeneratedBy = null };
    }

    public async Task<SnapshotWriteResult> WritePlanPayloadAsync(
        Guid storeId, DateOnly periodStart, string payloadJson, int expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var db = contexts.Create();
        var row = await db.PlanSnapshots.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.StoreId == storeId && s.PeriodStart == periodStart && s.Kind == SnapshotKinds.Plan, cancellationToken);
        if (row is null || row.Version != expectedVersion) return new SnapshotWriteResult(false, ConflictMessage);
        row.Payload = payloadJson;
        row.Version = expectedVersion + 1;
        try
        {
            // version is a concurrency token: the UPDATE carries "where version = expected".
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SnapshotWriteResult(false, ConflictMessage);
        }
        return new SnapshotWriteResult(true, null, row.Version);
    }

    public async Task<int> StoreAsync(
        Guid storeId, DateOnly periodStart, string kind, string payloadJson, DateTimeOffset generatedAt, Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (SnapshotKinds.Plan or SnapshotKinds.Roster))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A snapshot is a plan or a roster.");

        // Two generates racing for the same row: the second sees the first's version and
        // writes the one after it.
        for (var attempt = 0; ; attempt++)
        {
            await using var db = contexts.Create();
            var row = await db.PlanSnapshots.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.StoreId == storeId && s.PeriodStart == periodStart && s.Kind == kind, cancellationToken);
            if (row is null)
            {
                row = new PlanSnapshot { StoreId = storeId, PeriodStart = periodStart, Kind = kind, Version = 0 };
                db.PlanSnapshots.Add(row);
            }
            row.Payload = payloadJson;
            row.GeneratedAt = generatedAt;
            row.GeneratedBy = userId;
            row.Version += 1;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return row.Version;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Lost a race (concurrency token or duplicate key): read the winner and go again.
            }
            catch (DbUpdateException e)
            {
                throw new InvalidOperationException($"Failed to store {kind} snapshot: {e.InnerException?.Message ?? e.Message}", e);
            }
        }
    }

    private static HeldSnapshot ToHeld(PlanSnapshot s) =>
        new(s.StoreId, s.PeriodStart, s.Kind, s.Payload, s.GeneratedAt, s.GeneratedBy, s.Version);
}
