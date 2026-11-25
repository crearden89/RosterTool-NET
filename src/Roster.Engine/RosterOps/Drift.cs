// Port of the pure part of src/lib/roster/drift.ts. The reads (day_plans, the
// held roster snapshot and generated_shifts) are the data layer's.
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

using Roster.Engine.Core;

namespace Roster.Engine.RosterOps;

public sealed record RosterDrift(
    // The day is approved AND the held plan now differs from what was published.
    bool Drifted,
    bool Approved,
    DateTimeOffset? ApprovedAt, // when the published roster was approved
    DateTimeOffset? RegeneratedAt) // when the held plan was last solved
{
    public static readonly RosterDrift None = new(false, false, null, null);
}

/// <summary>A published shift (generated_shifts): who, and its times in minutes from midnight.</summary>
public sealed record PublishedShift(string? EmployeeId, double Start, double End);

public static class Drift
{
    // Identity of a rostered shift for comparison: who, from when, to when. Task
    // tiles are deliberately excluded - a different Registers/SCO split is a
    // display detail, not a change to anyone's working day.
    private static string KeyOf(string employeeId, double start, double end) =>
        $"{employeeId}|{Js.NumberToString(start)}|{Js.NumberToString(end)}";

    /// <summary>
    /// The drift for a store/date. <paramref name="rosterStatus"/> and <paramref name="approvedAt"/> are the
    /// day_plans row's roster_status and roster_generated_at; <paramref name="hasSnapshot"/> and
    /// <paramref name="regeneratedAt"/> say whether the fortnight's held roster exists and when it
    /// was solved; <paramref name="heldShifts"/> are that roster's shifts for the date (null when
    /// the date is not in it); <paramref name="live"/> the published shifts.
    /// </summary>
    public static RosterDrift DetectRosterDrift(
        string? rosterStatus,
        DateTimeOffset? approvedAt,
        bool hasSnapshot,
        DateTimeOffset? regeneratedAt,
        IReadOnlyList<Shift>? heldShifts,
        IReadOnlyList<PublishedShift> live)
    {
        var approved = rosterStatus == "approved";

        // Only an APPROVED day can drift - an unapproved one has nothing published
        // to disagree with, and the Floor Display already shows "no approved roster".
        if (!approved || !hasSnapshot) return new RosterDrift(false, approved, approvedAt, regeneratedAt);

        // A snapshot solved before the approval IS the approval's source - no drift.
        if (approvedAt is not null && regeneratedAt is not null && regeneratedAt <= approvedAt)
            return new RosterDrift(false, approved, approvedAt, regeneratedAt);

        if (heldShifts is null) return new RosterDrift(false, approved, approvedAt, regeneratedAt);

        var held = new JsSet<string>(heldShifts
            .Where(s => !s.Unfilled && !string.IsNullOrEmpty(s.EmployeeId))
            .Select(s => KeyOf(s.EmployeeId, s.Start, s.End)));
        var published = new JsSet<string>(live
            .Where(s => !string.IsNullOrEmpty(s.EmployeeId))
            .Select(s => KeyOf(s.EmployeeId!, s.Start, s.End)));

        var drifted = held.Size != published.Size || held.Any(k => !published.Has(k));
        return new RosterDrift(drifted, approved, approvedAt, regeneratedAt);
    }
}
