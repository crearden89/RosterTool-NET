// Port of src/app/(app)/admin/pay-rates/page.tsx (its reads) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Pay;
using Roster.Data.Services.RefData;
using Roster.Engine.Pay;
using Roster.Web.Features.Admin.Shared;
using PaySettings = Roster.Engine.Pay.PaySettings;
using PaySettingsRow = Roster.Data.Entities.PaySettings;

namespace Roster.Web.Features.Admin.PayRates;

/// <summary>A position with its own pay-rate column (has_rate).</summary>
public sealed record RatePosition(string Code, string Label, string BaseRole, double? WeeklyHours, double? YearBand);

public sealed record PayRatesView(
    IReadOnlyList<PayRateRow> Rates,
    IReadOnlyList<RatePosition> Positions,
    PaySettings Settings,
    IReadOnlyDictionary<string, double> TaxRates);

public interface IPayRatesService
{
    /// <summary>
    /// The whole grid (all 2880 rows), the positions that own a column, the pay
    /// settings and the payroll tax rates. RSM owns no column - it is paid the Store
    /// Assistant rate plus the sole-manager allowance.
    /// </summary>
    Task<PayRatesView> LoadAsync(CancellationToken ct = default);

    /// <summary>saveRates: upserts the cells sent, on (day_type, hour, role).</summary>
    Task<AdminResult> SaveRatesAsync(IReadOnlyList<RateCellChange> cells, CancellationToken ct = default);

    /// <summary>savePaySettings: the single settings row, then each state's tax rate.</summary>
    Task<AdminResult> SavePaySettingsAsync(PaySettings settings, IReadOnlyDictionary<string, double> taxRates, CancellationToken ct = default);
}

internal sealed class PayRatesService(IServiceProvider services) : IPayRatesService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();
    private IPayData pay => services.GetRequiredService<IPayData>();
    private IRefData refData => services.GetRequiredService<IRefData>();

    public async Task<PayRatesView> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var rates = await pay.GetPayRatesAsync(ct);
        var positions = await refData.GetPositionsAsync(ct);
        var settings = await pay.GetPaySettingsAsync(ct);
        var tax = await pay.GetPayrollTaxRatesAsync(ct);
        return new PayRatesView(
            rates,
            positions.Where(p => p.HasRate).Select(p => new RatePosition(p.Code, p.Label, p.BaseRole, p.WeeklyHours, p.YearBand)).ToList(),
            settings,
            tax);
    }

    public async Task<AdminResult> SaveRatesAsync(IReadOnlyList<RateCellChange> cells, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Pay Rates: save changed rates");
        var dayTypes = cells.Select(c => c.DayType).Distinct().ToList();
        var existing = await db.PayRates.Where(r => dayTypes.Contains(r.DayType)).ToListAsync(ct);
        var byKey = existing.ToDictionary(r => RateGrid.RateKey(r.DayType, r.Hour, r.Role));
        foreach (var c in cells)
        {
            var key = RateGrid.RateKey(c.DayType, c.Hour, c.Role);
            if (!byKey.TryGetValue(key, out var row))
            {
                row = new PayRate { DayType = c.DayType, Hour = (int)c.Hour, Role = c.Role };
                db.PayRates.Add(row);
                byKey[key] = row;
            }
            row.Rate = c.Rate;
        }
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> SavePaySettingsAsync(PaySettings settings, IReadOnlyDictionary<string, double> taxRates, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Pay Rates: save pay settings and payroll tax");
        var row = await db.PaySettings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new PaySettingsRow { Id = true };
            db.PaySettings.Add(row);
        }
        row.RsmAllowance = settings.RsmAllowance;
        row.SuperRate = settings.SuperRate;
        row.Overtime15Multiplier = settings.Overtime15Multiplier;
        row.Overtime19Multiplier = settings.Overtime19Multiplier;
        row.DoubleTimeMultiplier = settings.DoubleTimeMultiplier;
        row.SundayOtFromHour = settings.SundayOtFromHour;
        row.SundayOtToHour = settings.SundayOtToHour;
        var saved = await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
        if (!saved.Ok) return saved;

        if (taxRates.Count == 0) return AdminResult.Success;
        var states = await db.PayrollTaxRates.ToDictionaryAsync(r => r.State, ct);
        foreach (var (state, rate) in taxRates)
        {
            if (!states.TryGetValue(state, out var t))
            {
                t = new PayrollTaxRate { State = state };
                db.PayrollTaxRates.Add(t);
                states[state] = t;
            }
            t.Rate = rate;
        }
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }
}

public sealed class PayRatesModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IPayRatesService, PayRatesService>();
}
