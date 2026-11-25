// Port of src/lib/payroll/settings.ts (the reads) and src/lib/payroll/dayType.ts
// (the holiday lookup around the engine's DayTypes.DayType).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Services.Cache;
using Roster.Data.Services.RefData;
using Roster.Engine.Pay;

namespace Roster.Data.Services.Pay;

public interface IPayData
{
    /// <summary>getPaySettings: the global pay multipliers (the shipped defaults when the row is missing).</summary>
    Task<PaySettings> GetPaySettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// getPayRates: the full pay-rate grid (every position × 24 hours × 4 day types),
    /// ordered by role, day type and hour.
    /// </summary>
    Task<IReadOnlyList<PayRateRow>> GetPayRatesAsync(CancellationToken ct = default);

    /// <summary>getPayrollTaxRates: every state's payroll tax rate (0 where none is saved), in AuStates order.</summary>
    Task<IReadOnlyDictionary<string, double>> GetPayrollTaxRatesAsync(CancellationToken ct = default);

    /// <summary>
    /// dayType: the pay-rate day type for a store/date - "public_holiday" (3-tier
    /// calendar) before the plain "weekday" / "saturday" / "sunday" split.
    /// </summary>
    Task<string> DayTypeAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}

public sealed class PayDataReader(IRosterDbContextFactory contexts, IRefData refData) : IPayData
{
    public Task<PaySettings> GetPaySettingsAsync(CancellationToken ct = default) =>
        RequestMemo.Cached("payroll.getPaySettings", async () =>
        {
            await using var db = contexts.Create();
            var s = await db.PaySettings.AsNoTracking().Where(x => x.Id).FirstOrDefaultAsync(ct);
            if (s is null) return PaySettings.Default;
            return new PaySettings
            {
                RsmAllowance = s.RsmAllowance,
                SuperRate = s.SuperRate,
                Overtime15Multiplier = s.Overtime15Multiplier,
                Overtime19Multiplier = s.Overtime19Multiplier,
                SundayOtFromHour = s.SundayOtFromHour,
                SundayOtToHour = s.SundayOtToHour,
                DoubleTimeMultiplier = s.DoubleTimeMultiplier,
            };
        });

    // The full pay-rate grid, fetched ONCE per request however many cost calcs run
    // (shift cost, ideal floor and Analysis each need it for up to 14 dates). It is
    // 30 positions x 24 hours x 4 day types = 2880 rows: read whole, in a stable
    // total order (the TypeScript paged past PostgREST's 1000-row ceiling for it).
    public Task<IReadOnlyList<PayRateRow>> GetPayRatesAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyList<PayRateRow>>("payroll.getPayRates", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.PayRates.AsNoTracking().OrderBy(r => r.Role).ThenBy(r => r.DayType).ThenBy(r => r.Hour)
                .Select(r => new { r.DayType, r.Hour, r.Role, r.Rate }).ToListAsync(ct);
            return rows.Select(r => new PayRateRow(r.DayType, r.Hour, r.Role, r.Rate)).ToList();
        });

    public Task<IReadOnlyDictionary<string, double>> GetPayrollTaxRatesAsync(CancellationToken ct = default) =>
        RequestMemo.Cached<IReadOnlyDictionary<string, double>>("payroll.getPayrollTaxRates", async () =>
        {
            await using var db = contexts.Create();
            var rows = await db.PayrollTaxRates.AsNoTracking().OrderBy(r => r.State).Select(r => new { r.State, r.Rate }).ToListAsync(ct);
            var rates = PayrollTax.ZeroRates();
            foreach (var r in rows) rates[r.State] = r.Rate;
            return rates;
        });

    // Resolved from the request-cached holiday calendar - no per-date query.
    public Task<string> DayTypeAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        RequestMemo.Cached("payroll.dayType", [storeId, date], async () =>
            DayTypes.DayType(date, await refData.IsPublicHolidayAsync(storeId, date, ct)));
}
