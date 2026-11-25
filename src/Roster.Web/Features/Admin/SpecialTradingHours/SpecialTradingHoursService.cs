// Port of src/app/(app)/admin/special-trading-hours/page.tsx (its reads) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Scope;
using Roster.Web.Features.Admin.Shared;
using SpecialHoursEntity = Roster.Data.Entities.SpecialTradingHours;

namespace Roster.Web.Features.Admin.SpecialTradingHours;

public sealed record SpecialHoursRow(
    Guid Id, DateOnly StartDate, DateOnly EndDate, bool Closed, TimeOnly? OpenTime, TimeOnly? CloseTime, string? Note);

/// <param name="StoreId">The store the page is on; null when there are no stores.</param>
public sealed record SpecialHoursView(IReadOnlyList<StoreChoice> Stores, Guid? StoreId, IReadOnlyList<SpecialHoursRow> Rows);

/// <summary>A date range as the form sends it: ISO dates, "HH:MM" times (null when closed).</summary>
public sealed record SpecialHoursInput(
    Guid? StoreId, string StartDate, string EndDate, bool Closed, string? OpenTime, string? CloseTime, string Note);

public interface ISpecialTradingHoursService
{
    /// <summary>
    /// The store list and the chosen store's ranges, latest first. The store is the
    /// one asked for (?store=) when it is in the list, else the one last chosen,
    /// else the first.
    /// </summary>
    Task<SpecialHoursView> LoadAsync(Guid? requestedStore, Guid? rememberedStore, CancellationToken ct = default);

    Task<AdminResult> AddSpecialHoursAsync(SpecialHoursInput input, CancellationToken ct = default);

    Task<AdminResult> DeleteSpecialHoursAsync(Guid id, CancellationToken ct = default);
}

internal sealed class SpecialTradingHoursService(IRosterDbContextFactory contexts, IStoreScope scope) : ISpecialTradingHoursService
{
    public async Task<SpecialHoursView> LoadAsync(Guid? requestedStore, Guid? rememberedStore, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        var stores = await AdminStores.ListAsync(db, ct);
        var storeId = StoreScope.ResolveStoreId(requestedStore, stores.Select(s => s.Id).ToList(), rememberedStore);
        IReadOnlyList<SpecialHoursRow> rows = storeId is { } id
            ? await db.SpecialTradingHours.AsNoTracking()
                .Where(r => r.StoreId == id)
                .OrderByDescending(r => r.StartDate)
                .Select(r => new SpecialHoursRow(r.Id, r.StartDate, r.EndDate, r.Closed, r.OpenTime, r.CloseTime, r.Note))
                .ToListAsync(ct)
            : [];
        return new SpecialHoursView(stores, storeId, rows);
    }

    /// <summary>addSpecialHours's checks, in its order; null when the range may be saved.</summary>
    public static string? Validate(SpecialHoursInput input)
    {
        if (input.StoreId is null || string.IsNullOrEmpty(input.StartDate) || string.IsNullOrEmpty(input.EndDate))
            return "Store, start date and end date are required.";
        if (string.CompareOrdinal(input.EndDate, input.StartDate) < 0)
            return "End date must be on or after the start date.";
        if (!input.Closed)
        {
            if (string.IsNullOrEmpty(input.OpenTime) || string.IsNullOrEmpty(input.CloseTime))
                return "Enter open and close times, or mark the range closed.";
            if (string.CompareOrdinal(input.OpenTime, input.CloseTime) >= 0)
                return "Open time must be before close time.";
        }
        return null;
    }

    public async Task<AdminResult> AddSpecialHoursAsync(SpecialHoursInput input, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (Validate(input) is { } error) return AdminResult.Fail(error);
        if (!AdminParse.TryDate(input.StartDate, out var start)) return AdminResult.Fail(AdminParse.BadDate(input.StartDate));
        if (!AdminParse.TryDate(input.EndDate, out var end)) return AdminResult.Fail(AdminParse.BadDate(input.EndDate));
        TimeOnly? open = null, close = null;
        if (!input.Closed)
        {
            if (!AdminParse.TryTime(input.OpenTime, out var o)) return AdminResult.Fail(AdminParse.BadTime(input.OpenTime));
            if (!AdminParse.TryTime(input.CloseTime, out var c)) return AdminResult.Fail(AdminParse.BadTime(input.CloseTime));
            open = o;
            close = c;
        }
        var note = input.Note.Trim();
        await using var db = contexts.CreateSystem("Special Trading Hours: add a date range");
        db.SpecialTradingHours.Add(new SpecialHoursEntity
        {
            StoreId = input.StoreId!.Value,
            StartDate = start,
            EndDate = end,
            Closed = input.Closed,
            OpenTime = open,
            CloseTime = close,
            Note = note.Length == 0 ? null : note,
        });
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> DeleteSpecialHoursAsync(Guid id, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Special Trading Hours: remove a date range");
        return await AdminDb.WriteAsync(() => db.SpecialTradingHours.Where(r => r.Id == id).ExecuteDeleteAsync(ct));
    }
}

public sealed class SpecialTradingHoursModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ISpecialTradingHoursService, SpecialTradingHoursService>();
}
