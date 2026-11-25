// Port of src/app/(app)/admin/variance-report/page.tsx (its reads; the report itself is
// Roster.Engine.Variance.VarianceReports.Build) and actions.ts.

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Engine.Variance;
using Roster.Web.Features.Admin.Shared;
using DayReport = Roster.Engine.Variance.VarianceReport;

namespace Roster.Web.Features.Admin.VarianceReport;

/// <summary>The three thresholds, as variance_settings holds them.</summary>
public sealed record VarianceThresholds(double DataLightPct, double MetricPct, double HoursPct);

public sealed record VarianceReportView(DateOnly Date, DayReport Report, VarianceThresholds Thresholds);

public interface IVarianceReportService
{
    /// <summary>
    /// The day's report: the date asked for (?date=, yyyy-mm-dd), else yesterday in
    /// Australia, matching the script's daily habit.
    /// </summary>
    Task<VarianceReportView> LoadAsync(string? date, CancellationToken ct = default);

    /// <summary>saveVarianceSettings: each threshold held to 0..100 (data light) or 0..500.</summary>
    Task<AdminResult> SaveVarianceSettingsAsync(double dataLightPct, double metricPct, double hoursPct, CancellationToken ct = default);
}

internal sealed partial class VarianceReportService(IRosterDbContextFactory contexts, IStoreScope scope, TimeProvider clock) : IVarianceReportService
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    /// <summary>The page's date: a yyyy-mm-dd ?date=, else yesterday in Australia.</summary>
    public static DateOnly DateFor(string? requested, DateOnly todayAu) =>
        requested is not null && IsoDate().IsMatch(requested) && AdminParse.TryDate(requested, out var d) ? d : todayAu.AddDays(-1);

    public async Task<VarianceReportView> LoadAsync(string? date, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var day = DateFor(date, Dates.TodayAu(clock.GetUtcNow()));
        await using var db = contexts.CreateSystem("Daily Variance Report: every store's benchmark and actuals for the day");
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code).Select(s => new VarianceStore(s.Id, s.Code)).ToListAsync(ct);
        var bench = await db.BenchmarkDays.AsNoTracking().Where(b => b.Date == day)
            .Select(b => new BenchmarkDayFigures
            {
                StoreId = b.StoreId,
                SalesForecast = b.SalesForecast,
                Items = b.Items,
                CustomerCount = b.CustomerCount,
                IsMhs = b.IsMhs,
                HasEsl = b.HasEsl,
                HasBaler = b.HasBaler,
                LrHours100 = b.LrHours100,
                CleaningHours100 = b.CleaningHours100,
                SpecialsHours100 = b.SpecialsHours100,
                ResetHours100 = b.ResetHours100,
                AdminHours100 = b.AdminHours100,
                CheckoutHours100 = b.CheckoutHours100,
            }).ToListAsync(ct);
        var acts = await db.ActualsDays.AsNoTracking().Where(a => a.Date == day)
            .Select(a => new ActualsDayFigures
            {
                StoreId = a.StoreId,
                ActualSales = a.ActualSales,
                ActualItems = a.ActualItems,
                ActualCustomers = a.ActualCustomers,
                ActualHours100 = a.ActualHours100,
                UkgActualHours = a.UkgActualHours,
                IsMhs = a.IsMhs,
                HasEsl = a.HasEsl,
                HasBaler = a.HasBaler,
                LrHours = a.LrHours,
                CleaningHours = a.CleaningHours,
                SpecialsHours = a.SpecialsHours,
                ResetHours = a.ResetHours,
                AdminHours = a.AdminHours,
                CheckoutHours = a.CheckoutHours,
            }).ToListAsync(ct);
        var settings = await db.VarianceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id, ct);
        var thresholds = new VarianceThresholds(
            settings?.DataLightPct ?? VarianceReports.DefaultDataLightPct,
            settings?.MetricPct ?? VarianceReports.DefaultMetricPct,
            settings?.HoursPct ?? VarianceReports.DefaultHoursPct);
        var report = VarianceReports.Build(stores, bench, acts, thresholds.MetricPct, thresholds.HoursPct);
        return new VarianceReportView(day, report, thresholds);
    }

    /// <summary>Math.min(hi, Math.max(0, Number(v) || 0)).</summary>
    public static double Clamp(double v, double hi) => Js.Min(hi, Js.Max(0, Js.Truthy(v) ? v : 0));

    public async Task<AdminResult> SaveVarianceSettingsAsync(double dataLightPct, double metricPct, double hoursPct, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var light = Clamp(dataLightPct, 100);
        var metric = Clamp(metricPct, 500);
        var hours = Clamp(hoursPct, 500);
        var now = clock.GetUtcNow();
        await using var db = contexts.CreateSystem("Daily Variance Report: save the thresholds");
        return await AdminDb.WriteAsync(() => db.VarianceSettings.Where(s => s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.DataLightPct, light)
            .SetProperty(s => s.MetricPct, metric)
            .SetProperty(s => s.HoursPct, hours)
            .SetProperty(s => s.UpdatedAt, now), ct));
    }
}

public sealed class VarianceReportModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IVarianceReportService, VarianceReportService>();
}
