// Port of src/app/(app)/admin/calendars/page.tsx (its reads) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Admin.Shared;
using AuStates = Roster.Engine.Core.AuStates;

namespace Roster.Web.Features.Admin.Calendars;

public sealed record NationalHolidayRow(Guid Id, DateOnly Date, string Name);

public sealed record StateHolidayRow(Guid Id, string State, DateOnly Date, string Name);

public sealed record LocalHolidayRow(Guid Id, Guid StoreId, DateOnly Date, string Name);

public sealed record SchoolTermRow(Guid Id, string State, string TermLabel, DateOnly StartDate, DateOnly EndDate);

public sealed record TermExceptionRow(Guid Id, string State, DateOnly Date, string? Note);

public sealed record CalendarsView(
    IReadOnlyList<NationalHolidayRow> National,
    IReadOnlyList<StateHolidayRow> State,
    IReadOnlyList<LocalHolidayRow> Local,
    IReadOnlyList<SchoolTermRow> Terms,
    IReadOnlyList<TermExceptionRow> Exceptions,
    IReadOnlyList<StoreChoice> Stores);

/// <summary>The five calendar tables a row can be deleted from (deleteRow's table).</summary>
public enum CalendarTable
{
    PublicHolidaysNational,
    PublicHolidaysState,
    PublicHolidaysLocal,
    SchoolTerms,
    SchoolTermExceptions,
}

public interface ICalendarsService
{
    /// <summary>Every holiday, term and exception day, by date, and the stores for the local-holiday picker.</summary>
    Task<CalendarsView> LoadAsync(CancellationToken ct = default);

    Task<AdminResult> AddNationalHolidayAsync(string date, string name, CancellationToken ct = default);

    Task<AdminResult> AddStateHolidayAsync(string state, string date, string name, CancellationToken ct = default);

    Task<AdminResult> AddLocalHolidayAsync(Guid storeId, string date, string name, CancellationToken ct = default);

    Task<AdminResult> AddSchoolTermAsync(string state, string termLabel, string startDate, string endDate, CancellationToken ct = default);

    Task<AdminResult> AddSchoolTermExceptionAsync(string state, string date, string note, CancellationToken ct = default);

    Task<AdminResult> DeleteRowAsync(CalendarTable table, Guid id, CancellationToken ct = default);
}

internal sealed class CalendarsService(IRosterDbContextFactory contexts, IStoreScope scope) : ICalendarsService
{
    public async Task<CalendarsView> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        var national = await db.PublicHolidaysNational.AsNoTracking().OrderBy(h => h.Date)
            .Select(h => new NationalHolidayRow(h.Id, h.Date, h.Name)).ToListAsync(ct);
        var state = await db.PublicHolidaysState.AsNoTracking().OrderBy(h => h.Date)
            .Select(h => new StateHolidayRow(h.Id, h.State, h.Date, h.Name)).ToListAsync(ct);
        var local = await db.PublicHolidaysLocal.AsNoTracking().OrderBy(h => h.Date)
            .Select(h => new LocalHolidayRow(h.Id, h.StoreId, h.Date, h.Name)).ToListAsync(ct);
        var terms = await db.SchoolTerms.AsNoTracking().OrderBy(t => t.StartDate)
            .Select(t => new SchoolTermRow(t.Id, t.State, t.TermLabel, t.StartDate, t.EndDate)).ToListAsync(ct);
        var exceptions = await db.SchoolTermExceptions.AsNoTracking().OrderBy(e => e.Date)
            .Select(e => new TermExceptionRow(e.Id, e.State, e.Date, e.Note)).ToListAsync(ct);
        var stores = await AdminStores.ListAsync(db, ct);
        return new CalendarsView(national, state, local, terms, exceptions, stores);
    }

    // The state column is Postgres's au_state enum: anything else is refused.
    private static string? BadState(string state) =>
        AuStates.All.Contains(state) ? null : $"invalid input value for enum au_state: \"{state}\"";

    private async Task<AdminResult> InsertAsync(string why, Action<RosterDbContext> add, CancellationToken ct)
    {
        await using var db = contexts.CreateSystem($"Calendars: {why}");
        add(db);
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> AddNationalHolidayAsync(string date, string name, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (!AdminParse.TryDate(date, out var d)) return AdminResult.Fail(AdminParse.BadDate(date));
        return await InsertAsync("add a national holiday", db => db.PublicHolidaysNational.Add(new PublicHolidayNational { Date = d, Name = name }), ct);
    }

    public async Task<AdminResult> AddStateHolidayAsync(string state, string date, string name, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (BadState(state) is { } bad) return AdminResult.Fail(bad);
        if (!AdminParse.TryDate(date, out var d)) return AdminResult.Fail(AdminParse.BadDate(date));
        return await InsertAsync("add a state holiday", db => db.PublicHolidaysState.Add(new PublicHolidayState { State = state, Date = d, Name = name }), ct);
    }

    public async Task<AdminResult> AddLocalHolidayAsync(Guid storeId, string date, string name, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (!AdminParse.TryDate(date, out var d)) return AdminResult.Fail(AdminParse.BadDate(date));
        return await InsertAsync("add a local holiday", db => db.PublicHolidaysLocal.Add(new PublicHolidayLocal { StoreId = storeId, Date = d, Name = name }), ct);
    }

    public async Task<AdminResult> AddSchoolTermAsync(string state, string termLabel, string startDate, string endDate, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (BadState(state) is { } bad) return AdminResult.Fail(bad);
        if (!AdminParse.TryDate(startDate, out var start)) return AdminResult.Fail(AdminParse.BadDate(startDate));
        if (!AdminParse.TryDate(endDate, out var end)) return AdminResult.Fail(AdminParse.BadDate(endDate));
        return await InsertAsync("add a school term", db => db.SchoolTerms.Add(new SchoolTerm
        {
            State = state,
            TermLabel = termLabel,
            StartDate = start,
            EndDate = end,
        }), ct);
    }

    public async Task<AdminResult> AddSchoolTermExceptionAsync(string state, string date, string note, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (BadState(state) is { } bad) return AdminResult.Fail(bad);
        if (!AdminParse.TryDate(date, out var d)) return AdminResult.Fail(AdminParse.BadDate(date));
        return await InsertAsync("add a pupil-free day", db => db.SchoolTermExceptions.Add(new SchoolTermException { State = state, Date = d, Note = note }), ct);
    }

    public async Task<AdminResult> DeleteRowAsync(CalendarTable table, Guid id, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Calendars: delete a row");
        return await AdminDb.WriteAsync(() => table switch
        {
            CalendarTable.PublicHolidaysNational => db.PublicHolidaysNational.Where(r => r.Id == id).ExecuteDeleteAsync(ct),
            CalendarTable.PublicHolidaysState => db.PublicHolidaysState.Where(r => r.Id == id).ExecuteDeleteAsync(ct),
            CalendarTable.PublicHolidaysLocal => db.PublicHolidaysLocal.Where(r => r.Id == id).ExecuteDeleteAsync(ct),
            CalendarTable.SchoolTerms => db.SchoolTerms.Where(r => r.Id == id).ExecuteDeleteAsync(ct),
            CalendarTable.SchoolTermExceptions => db.SchoolTermExceptions.Where(r => r.Id == id).ExecuteDeleteAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        });
    }
}

public sealed class CalendarsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ICalendarsService, CalendarsService>();
}
