// Port of src/app/(app)/plan/sales/page.tsx (the reads) and actions.ts (the writes).

namespace Roster.Web.Features.Sales;

/// <summary>The Sales page's reads and its two actions, as the signed-in user.</summary>
public interface ISalesService
{
    /// <summary>
    /// The page: the viewer's store (?store=, else the remembered one, else the first
    /// in scope; a store login always gets its own), the fortnight from
    /// <paramref name="start"/> (else the store's first benchmark date, else today),
    /// its forecasts, adjustments, 12-week averages and the other inputs in brief.
    /// </summary>
    Task<SalesPageData> LoadAsync(Guid? store, DateOnly? start, CancellationToken ct = default);

    /// <summary>saveOverride: save a day's adjustment and un-approve the day.</summary>
    Task<SalesResult> SaveOverrideAsync(SaveOverrideInput input, CancellationToken ct = default);

    /// <summary>resetOverride: back to the forecast; the day's adjustment, if any, is removed.</summary>
    Task<SalesResult> ResetOverrideAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}
