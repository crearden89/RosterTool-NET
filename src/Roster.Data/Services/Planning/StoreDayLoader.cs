// Port of src/lib/storeDay.ts (the reads; the resolution is Core StoreDays.ResolveStoreDay).

using Roster.Data.Services.Cache;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;

namespace Roster.Data.Services.Planning;

// The single source of truth for a store's real staffed window on a given date.
// Resolution is in memory over the request-cached trading week + special-hours
// rows - a fortnight render calls this ~28x and used to issue 1-2 queries per call.
public sealed class StoreDayLoader(IRefData refData) : IStoreDayLoader
{
    public Task<StoreDay> ResolveStoreDayAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("storeDay.resolveStoreDay", [storeId, date], async () =>
        {
            var week = await refData.GetTradingWeekAsync(storeId, ct);
            var specials = await refData.GetSpecialHoursAsync(storeId, ct);
            var overrides = await refData.GetRuleOverridesAsync(ct);
            return StoreDays.ResolveStoreDay(week, specials, overrides, date);
        });
}
