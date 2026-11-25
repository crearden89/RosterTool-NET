// Port of src/lib/roster/drift.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.RosterOps;

namespace Roster.Data.Services.Approval;

// Comparing the held roster with what was published is the engine's
// RosterOps.Drift; this reads both, as the system.
public sealed class RosterDriftService(IRosterDbContextFactory contexts) : IRosterDrift
{
    public async Task<RosterDrift> DetectRosterDriftAsync(Guid storeId, DateOnly date, CancellationToken ct = default)
    {
        // The Floor Display is a public, no-login page, so it cannot read through
        // the store scope: the system reads for it.
        await using var admin = contexts.CreateSystem("detectRosterDrift: the Floor Display compares published and held rosters");
        var plan = await admin.DayPlans.AsNoTracking().Where(d => d.StoreId == storeId && d.Date == date)
            .Select(d => new { d.RosterStatus, d.RosterGeneratedAt }).FirstOrDefaultAsync(ct);
        var periodStart = Period.PeriodStartFor(date);
        var snapRow = await admin.PlanSnapshots.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.PeriodStart == periodStart && s.Kind == SnapshotKinds.Roster)
            .Select(s => new { s.Payload, s.GeneratedAt }).FirstOrDefaultAsync(ct);
        var live = await admin.GeneratedShifts.AsNoTracking().Where(s => s.StoreId == storeId && s.Date == date)
            .Select(s => new { s.EmployeeId, s.StartTime, s.EndTime }).ToListAsync(ct);

        var approved = plan?.RosterStatus == DayStatuses.Approved;
        var approvedAt = plan?.RosterGeneratedAt;
        var regeneratedAt = snapRow?.GeneratedAt;

        // Only an APPROVED day can drift - an unapproved one has nothing published
        // to disagree with, and the Floor Display already shows "no approved roster".
        if (!approved || snapRow is null) return new RosterDrift { Approved = approved, ApprovedAt = approvedAt, RegeneratedAt = regeneratedAt };

        // A snapshot solved before the approval IS the approval's source - no drift.
        if (approvedAt is not null && regeneratedAt is not null && regeneratedAt <= approvedAt)
            return new RosterDrift { Approved = approved, ApprovedAt = approvedAt, RegeneratedAt = regeneratedAt };

        var held = HeldRosters.ParseRoster(new Snapshots.HeldSnapshot(storeId, periodStart, SnapshotKinds.Roster, snapRow.Payload, snapRow.GeneratedAt, null, 1));
        var drift = Drift.DetectRosterDrift(
            plan?.RosterStatus, approvedAt, true, regeneratedAt,
            held.ByDate.TryGet(date, out var day) ? day.Shifts : null,
            live.Select(s => new PublishedShift(s.EmployeeId.ToString("D"), Minutes(s.StartTime), Minutes(s.EndTime))).ToList());
        return new RosterDrift { Drifted = drift.Drifted, Approved = drift.Approved, ApprovedAt = drift.ApprovedAt, RegeneratedAt = drift.RegeneratedAt };
    }

    private static double Minutes(TimeOnly t) => Grid.TimeToMin(t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

}
