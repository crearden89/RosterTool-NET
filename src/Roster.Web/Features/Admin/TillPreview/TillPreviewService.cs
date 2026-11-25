// Port of src/app/(app)/admin/till-preview/page.tsx (its reads): the store and date to
// show, and the till engine run over that day (D2's loader around E3's TillCompute).

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Scope;
using Roster.Data.Services.Planning;
using Roster.Engine.Core;
using Roster.Engine.Till;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.TillPreview;

/// <param name="SelectedDate">"yyyy-mm-dd", or "" when there is no store.</param>
/// <param name="Result">The engine's run for the store and date; null when there is no store.</param>
public sealed record TillPreviewView(IReadOnlyList<StoreChoice> Stores, Guid? SelectedStore, string SelectedDate, TillComputation? Result);

public interface ITillPreviewService
{
    /// <summary>
    /// The store (asked for, else last chosen, else the first) and the date (asked for,
    /// else the store's earliest benchmark date - the source of the day's checkout-hours
    /// budget - else today), with the till engine run over them.
    /// </summary>
    Task<TillPreviewView> LoadAsync(Guid? requestedStore, Guid? rememberedStore, string? date, CancellationToken ct = default);
}

internal sealed class TillPreviewService(IRosterDbContextFactory contexts, IStoreScope scope, ITillLoader tills, TimeProvider clock) : ITillPreviewService
{
    public async Task<TillPreviewView> LoadAsync(Guid? requestedStore, Guid? rememberedStore, string? date, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        var stores = await AdminStores.ListAsync(db, ct);
        var selected = StoreScope.ResolveStoreId(requestedStore, stores.Select(s => s.Id).ToList(), rememberedStore);
        var selectedDate = date ?? "";
        if (selected is { } id && selectedDate == "")
        {
            var first = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == id)
                .OrderBy(b => b.Date).Select(b => (DateOnly?)b.Date).FirstOrDefaultAsync(ct);
            selectedDate = AdminParse.Iso(first ?? Dates.TodayAu(clock.GetUtcNow()));
        }
        TillComputation? result = null;
        if (selected is { } store && AdminParse.TryDate(selectedDate, out var day))
            result = await tills.ComputeTillForDateAsync(store, day, ct);
        return new TillPreviewView(stores, selected, selectedDate, result);
    }
}

public sealed class TillPreviewModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ITillPreviewService, TillPreviewService>();
}
