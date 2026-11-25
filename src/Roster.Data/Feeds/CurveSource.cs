// Port of src/lib/plan/curveSource.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Feeds;

/// <summary>The plan page's trade-curve card (peerTradeCurveRows).</summary>
public interface ICurveSource
{
    /// <summary>
    /// The store's weekly curve as flat rows: every weekday's rows, weekdays in the order
    /// <see cref="PosAllocation.AssembleCurveWeek"/> holds them.
    /// </summary>
    Task<IReadOnlyList<QuarterHourRow>> PeerTradeCurveRowsAsync(Guid storeId, CancellationToken ct = default);
}

// The plan page's trade-curve card, service-role edition. The engines read
// the derived curve through refdata's store-scoped getTradeCurveWeek; the
// card cannot, because Plan is peer-visible and the store scope rightly hides another
// store's rows from a store-tier user. Same assembly, different context - the
// pure AssembleCurveWeek is the single definition of what the curve IS.
public sealed class CurveSource(IRosterDbContextFactory contexts, IFeedStore feeds) : ICurveSource
{
    public async Task<IReadOnlyList<QuarterHourRow>> PeerTradeCurveRowsAsync(Guid storeId, CancellationToken ct = default)
    {
        List<PosQuarterHourRow> posRows;
        List<QuarterHourRow> legacy;
        await using (var db = contexts.CreateSystem("plan page trade-curve card (peer-visible, lib/plan/curveSource)"))
        {
            var pos = await db.PosIntervalStats.AsNoTracking().Where(p => p.StoreId == storeId)
                .OrderBy(p => p.Date).ThenBy(p => p.BlockTime)
                .Select(p => new { p.Date, p.BlockTime, p.McoCustomers, p.ScoCustomers, p.McoItems, p.ScoItems })
                .ToListAsync(ct);
            posRows = pos.Select(r => new PosQuarterHourRow(
                0, storeId.ToString(), r.Date, Hms(r.BlockTime), r.McoCustomers, r.ScoCustomers, r.McoItems, r.ScoItems)).ToList();
            var rows = await db.QuarterHourAllocations.AsNoTracking().Where(q => q.StoreId == storeId)
                .OrderBy(q => q.BlockTime).ThenBy(q => q.DayOfWeek)
                .Select(q => new { q.DayOfWeek, q.BlockTime, q.Allocation })
                .ToListAsync(ct);
            legacy = rows.Select(r => new QuarterHourRow { DayOfWeek = r.DayOfWeek, BlockTime = Hms(r.BlockTime), Allocation = r.Allocation }).ToList();
        }
        var holidayMap = await feeds.HolidaysForStoresAsync([storeId], ct);
        var week = PosAllocation.AssembleCurveWeek(
            posRows, legacy, holidayMap.TryGetValue(storeId, out var set) ? set : new HashSet<DateOnly>());
        return week.Values.SelectMany(v => v).ToList();
    }

    /// <summary>A TIME column as Postgres prints it: "HH:MM:SS".</summary>
    internal static string Hms(TimeOnly t) => t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
