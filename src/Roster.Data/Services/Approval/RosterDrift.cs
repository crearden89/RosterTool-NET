// Port of the public surface of src/lib/roster/drift.ts.

namespace Roster.Data.Services.Approval;

// ─── Approved-vs-held drift ──────────────────────────────────────────────────
// The Roster page reads the HELD SNAPSHOT (what the solver last produced); the
// Floor Display reads GENERATED_SHIFTS (what was actually approved and
// published to the team). That separation is deliberate - a background
// re-solve must never silently swap people out from under a store working the
// floor - but it means pressing Regenerate after approving leaves the two out
// of step, with the wall display showing one roster and the manager's screen
// another.
//
// This detects that state so both surfaces can say so out loud.

public sealed record RosterDrift
{
    /// <summary>The day is approved AND the held roster now differs from what was published.</summary>
    public bool Drifted { get; init; }
    public bool Approved { get; init; }
    /// <summary>When the published roster was approved.</summary>
    public DateTimeOffset? ApprovedAt { get; init; }
    /// <summary>When the held roster was last solved.</summary>
    public DateTimeOffset? RegeneratedAt { get; init; }

    public static readonly RosterDrift None = new();
}

public interface IRosterDrift
{
    /// <summary>
    /// detectRosterDrift: read as the system (the Floor Display is a no-login
    /// page and cannot read through the store scope).
    /// </summary>
    Task<RosterDrift> DetectRosterDriftAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}
