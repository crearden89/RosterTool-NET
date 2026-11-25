// Port of src/lib/emso/rosterCheck.ts.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Engine.Core;

namespace Roster.Data.Jobs;

// The EMSO unapproved-roster check (spec §8): for the period stores should
// currently have finished (the planning period), any store in an EMSO's area
// that has benchmark demand but no fully-approved roster generates an in-app
// notification for that EMSO, shown on their next login. Idempotent per EMSO +
// period: an existing unread alert for the same period is refreshed, not
// duplicated. Meant to be run at Wed 18:00 of the planning week (via a scheduled
// job) but is also runnable on demand by a super user (Admin → EMSO, "Run now").

/// <summary>What one run of the check wrote: alerts created, refreshed, and writes that failed.</summary>
public sealed record EmsoCheckResult(int Created, int Updated, int Failed);

/// <summary>runEmsoUnapprovedCheck. Runs as the server (the TypeScript app used the service role).</summary>
public interface IEmsoRosterCheck
{
    /// <summary>
    /// Raise or refresh one "emso_unapproved" notification per EMSO whose area has a store
    /// with benchmark demand but no approved roster on some day of the planning period
    /// (the period after the one containing today in Sydney). The caller checks who may run
    /// it (the admin action requires a super user; the scheduled job is the server).
    /// </summary>
    Task<EmsoCheckResult> RunEmsoUnapprovedCheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>The notification the check writes.</summary>
public static class EmsoAlert
{
    /// <summary>notifications.kind of the alert.</summary>
    public const string Kind = "emso_unapproved";

    /// <summary>Where the alert links to.</summary>
    public const string Link = "/overview";

    /// <summary>"3 store(s) without an approved roster for 07/12 – 20/12: G101, G102, G205".</summary>
    public static string Body(IReadOnlyList<string> behindStoreCodes, string periodLabel) =>
        $"{behindStoreCodes.Count} store(s) without an approved roster for {periodLabel}: {string.Join(", ", behindStoreCodes)}";
}

internal sealed class EmsoRosterCheck(IRosterDbContextFactory contexts, TimeProvider clock) : IEmsoRosterCheck
{
    public async Task<EmsoCheckResult> RunEmsoUnapprovedCheckAsync(CancellationToken cancellationToken = default)
    {
        var target = Period.PlanningPeriodStart(Dates.TodayAu(clock.GetUtcNow()));
        var dates = Period.PeriodDatesFrom(target);
        var label = Period.PeriodLabel(target);
        var (first, last) = (dates[0], dates[^1]); // the 14 consecutive dates of the period

        await using var db = contexts.CreateSystem("emso-check: the EMSO unapproved-roster check");
        // The TypeScript app paginated the two per-store-per-date reads past PostgREST's
        // 1000-row ceiling (600+ stores x 14 dates); a query here has no ceiling.
        var emsos = await db.AppUsers.AsNoTracking()
            .Where(u => u.Tier == Tier.Emso)
            .Select(u => new { u.Id, u.AreaId })
            .ToListAsync(cancellationToken);
        var stores = await db.Stores.AsNoTracking()
            .Select(s => new { s.Id, s.Code, s.AreaId })
            .ToListAsync(cancellationToken);
        var bench = await db.BenchmarkDays.AsNoTracking()
            .Where(b => b.Date >= first && b.Date <= last)
            .Select(b => new { b.StoreId, b.Date })
            .ToListAsync(cancellationToken);
        var dp = await db.DayPlans.AsNoTracking()
            .Where(d => d.Date >= first && d.Date <= last)
            .Select(d => new { d.StoreId, d.Date, d.RosterStatus })
            .ToListAsync(cancellationToken);

        var codeById = new Dictionary<Guid, string>();
        foreach (var s in stores) codeById[s.Id] = s.Code;
        var dataByStore = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var b in bench)
        {
            if (!dataByStore.TryGetValue(b.StoreId, out var set)) dataByStore[b.StoreId] = set = [];
            set.Add(b.Date);
        }
        var approvedByStore = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var d in dp)
        {
            if (d.RosterStatus != DayStatuses.Approved) continue;
            if (!approvedByStore.TryGetValue(d.StoreId, out var set)) approvedByStore[d.StoreId] = set = [];
            set.Add(d.Date);
        }
        bool IsUnapproved(Guid storeId)
        {
            if (!dataByStore.TryGetValue(storeId, out var dd) || dd.Count == 0) return false; // nothing to approve
            var ap = approvedByStore.GetValueOrDefault(storeId) ?? [];
            return dd.Any(date => !ap.Contains(date));
        }

        var storesByArea = new Dictionary<Guid, List<Guid>>();
        foreach (var s in stores)
        {
            if (s.AreaId is not { } area) continue;
            if (!storesByArea.TryGetValue(area, out var list)) storesByArea[area] = list = [];
            list.Add(s.Id);
        }

        var created = 0;
        var updated = 0;
        var failed = 0;
        foreach (var e in emsos)
        {
            var inArea = e.AreaId is { } area ? storesByArea.GetValueOrDefault(area) ?? [] : [];
            var behind = Js.SortDefault(inArea.Where(IsUnapproved).Select(id => codeById.GetValueOrDefault(id) ?? id.ToString()).ToList());
            if (behind.Count == 0) continue;

            var body = EmsoAlert.Body(behind, label);
            // Idempotent: refresh an existing unread alert for this period, else insert.
            // As supabase-js maybeSingle(): two or more matches count as none (it errors), so
            // a fresh alert is inserted.
            var matches = await db.Notifications
                .Where(n => n.UserId == e.Id && n.Kind == EmsoAlert.Kind && n.ReadAt == null && n.Body.Contains(label))
                .Take(2)
                .ToListAsync(cancellationToken);
            var existing = matches.Count == 1 ? matches[0] : null;
            // Count only writes that actually landed - a failed insert reported as
            // "created" would hide exactly the alert the check exists to guarantee.
            if (existing is not null)
            {
                existing.Body = body;
                if (await TrySaveAsync(db, cancellationToken)) updated++;
                else failed++;
            }
            else
            {
                db.Notifications.Add(new Notification { UserId = e.Id, Kind = EmsoAlert.Kind, Body = body, Link = EmsoAlert.Link });
                if (await TrySaveAsync(db, cancellationToken)) created++;
                else failed++;
            }
        }
        return new EmsoCheckResult(created, updated, failed);
    }

    /// <summary>One write; a failure is counted by the caller, and the next write starts clean.</summary>
    private static async Task<bool> TrySaveAsync(RosterDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
