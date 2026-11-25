// Port of src/lib/roster/drift.ts.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Engine.Core;

namespace Roster.Data.Services.Approval;

public sealed class RosterDriftService(IRosterDbContextFactory contexts) : IRosterDrift
{
    // Identity of a rostered shift for comparison: who, from when, to when. Task
    // tiles are deliberately excluded - a different Registers/SCO split is a
    // display detail, not a change to anyone's working day.
    private static string KeyOf(string employeeId, double start, double end) =>
        $"{employeeId}|{Js.NumberToString(start)}|{Js.NumberToString(end)}";

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

        var day = HeldDay(snapRow.Payload, date);
        if (day is null) return new RosterDrift { Approved = approved, ApprovedAt = approvedAt, RegeneratedAt = regeneratedAt };

        var held = new HashSet<string>(StringComparer.Ordinal);
        if (day.Value.TryGetProperty("shifts", out var shifts) && shifts.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in shifts.EnumerateArray())
            {
                if (Truthy(s, "unfilled")) continue;
                var employeeId = s.TryGetProperty("employeeId", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (string.IsNullOrEmpty(employeeId)) continue;
                held.Add(KeyOf(employeeId, Number(s, "start") ?? -1, Number(s, "end") ?? -1));
            }
        }
        var published = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in live)
            published.Add(KeyOf(s.EmployeeId.ToString("D"), Minutes(s.StartTime), Minutes(s.EndTime)));

        var drifted = held.Count != published.Count || held.Any(k => !published.Contains(k));
        return new RosterDrift { Drifted = drifted, Approved = approved, ApprovedAt = approvedAt, RegeneratedAt = regeneratedAt };
    }

    private static double Minutes(TimeOnly t) => Grid.TimeToMin(t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>payload.byDate.find(([d]) => d === date)?.[1]: the held roster's day.</summary>
    private static JsonElement? HeldDay(string payload, DateOnly date)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("byDate", out var byDate)
            || byDate.ValueKind != JsonValueKind.Array) return null;
        var iso = date.ToIso();
        foreach (var pair in byDate.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
            if (pair[0].ValueKind == JsonValueKind.String && pair[0].GetString() == iso) return pair[1].Clone();
        }
        return null;
    }

    private static bool Truthy(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => Js.Truthy(v.GetDouble()),
            JsonValueKind.String => v.GetString()!.Length > 0,
            JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
