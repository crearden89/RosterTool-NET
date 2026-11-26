// Port of src/app/(app)/admin/store-attributes/page.tsx (its reads), actions.ts, and
// the stores CSV the Add stores card reads (parseStoresCsv in StoreAdmin.tsx).

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Web.Features.Admin.Shared;
using AttributesEntity = Roster.Data.Entities.StoreAttributes;
using AuStates = Roster.Engine.Core.AuStates;

namespace Roster.Web.Features.Admin.StoreAttributes;

/// <summary>One store's checkout model and details, as the sheet edits them (Attr).</summary>
public sealed record AttributeValues
{
    public bool HasSco { get; init; }
    public bool IsNewScoModel { get; init; }
    public double McoRegisters { get; init; }
    public double ScoLanes { get; init; }
    public double ScoEfficiency { get; init; } = 0.45;
    public double FirstStep { get; init; } = 1.2;
    public double ScoShare { get; init; } = 0.66;
    public double OeTargetPct { get; init; } = 90;
    /// <summary>"yyyy-mm-dd", or null.</summary>
    public string? OpeningDate { get; init; }
    public Guid? PairStoreId { get; init; }
    public bool NightFill { get; init; }

    /// <summary>The sheet's DEFAULTS: what a store with no attributes row shows.</summary>
    public static readonly AttributeValues Defaults = new();
}

/// <summary>One day's trading hours as the sheet edits them: "HH:MM" times.</summary>
public sealed record TradingHour(int DayOfWeek, string? OpenTime, string? CloseTime, bool Closed);

public sealed record RegionChoice(Guid Id, string Name);

/// <param name="Attributes">The saved attributes over the defaults (the sheet's starting values).</param>
/// <param name="Hours">Monday (0) to Sunday (6); a day with no row opens 07:00-21:00.</param>
public sealed record StoreAttributesView(
    IReadOnlyList<StoreChoice> Stores,
    IReadOnlyList<RegionChoice> Regions,
    Guid? SelectedId,
    AttributeValues Attributes,
    IReadOnlyList<TradingHour> Hours);

/// <summary>A store to create (NewStore): its number or code, name, state and optional opening date.</summary>
public sealed record NewStore(string Code, string Name, string State, string? OpeningDate = null);

public sealed record BulkAddResult(bool Ok, int Created, string? Error = null);

public interface IStoreAttributesService
{
    Task<StoreAttributesView> LoadAsync(Guid? requestedStore, Guid? rememberedStore, CancellationToken ct = default);

    /// <summary>saveStoreAttributes: the store's attributes and its seven days, together.</summary>
    Task<AdminResult> SaveStoreAttributesAsync(Guid storeId, AttributeValues input, IReadOnlyList<TradingHour> tradingHours, CancellationToken ct = default);

    Task<AdminResult> AddStoreAsync(Guid regionId, NewStore store, CancellationToken ct = default);

    /// <summary>Creates the stores in turn, stopping at the first refused (how many were created by then).</summary>
    Task<BulkAddResult> BulkAddStoresAsync(Guid regionId, IReadOnlyList<NewStore> stores, CancellationToken ct = default);
}

/// <summary>The result of reading a stores CSV: the rows, or the line that stopped it.</summary>
public sealed record StoresCsv(IReadOnlyList<NewStore> Rows, string? Error = null);

public static partial class StoreCsv
{
    [GeneratedRegex("name", RegexOptions.IgnoreCase)]
    private static partial Regex NameWord();

    [GeneratedRegex("(code|store|number|id)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeWord();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreak();

    // Parse a simple stores CSV: header row then `code,name,state,opening_date`.
    // opening_date optional (yyyy-mm-dd). Tolerant of extra whitespace.
    public static StoresCsv Parse(string text)
    {
        var lines = LineBreak().Split(text).Select(Js.Trim).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return new StoresCsv([]);
        var start = NameWord().IsMatch(lines[0]) && CodeWord().IsMatch(lines[0]) ? 1 : 0;
        var rows = new List<NewStore>();
        for (var i = start; i < lines.Count; i++)
        {
            var cells = lines[i].Split(',').Select(Js.Trim).ToArray();
            if (cells.Length < 3) return new StoresCsv([], $"Line {i + 1}: expected at least code, name, state");
            rows.Add(new NewStore(cells[0], cells[1], cells[2].ToUpperInvariant(), cells.Length > 3 && cells[3].Length > 0 ? cells[3] : null));
        }
        return new StoresCsv(rows);
    }
}

internal sealed partial class StoreAttributesService(IServiceProvider services, TimeProvider clock)
    : IStoreAttributesService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();

    public async Task<StoreAttributesView> LoadAsync(Guid? requestedStore, Guid? rememberedStore, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        var stores = await AdminStores.ListAsync(db, ct);
        var regions = await db.Regions.AsNoTracking().OrderBy(r => r.Name).Select(r => new RegionChoice(r.Id, r.Name)).ToListAsync(ct);
        var selectedId = StoreScope.ResolveStoreId(requestedStore, stores.Select(s => s.Id).ToList(), rememberedStore);

        var attributes = AttributeValues.Defaults;
        var rows = new List<StoreTradingHours>();
        if (selectedId is { } id)
        {
            var a = await db.StoreAttributes.AsNoTracking().FirstOrDefaultAsync(x => x.StoreId == id, ct);
            if (a is not null)
                attributes = new AttributeValues
                {
                    HasSco = a.HasSco,
                    IsNewScoModel = a.IsNewScoModel,
                    McoRegisters = a.McoRegisters,
                    ScoLanes = a.ScoLanes,
                    ScoEfficiency = a.ScoEfficiency,
                    FirstStep = a.FirstStep,
                    ScoShare = a.ScoShare,
                    OeTargetPct = a.OeTargetPct,
                    OpeningDate = a.OpeningDate is { } d ? AdminParse.Iso(d) : null,
                    PairStoreId = a.PairStoreId,
                    NightFill = a.NightFill,
                };
            rows = await db.StoreTradingHours.AsNoTracking().Where(t => t.StoreId == id).OrderBy(t => t.DayOfWeek).ToListAsync(ct);
        }
        var hours = Enumerable.Range(0, 7).Select(i =>
        {
            var existing = rows.FirstOrDefault(t => t.DayOfWeek == i);
            return new TradingHour(
                i,
                existing?.OpenTime is { } o ? AdminParse.Hm(o) : "07:00",
                existing?.CloseTime is { } c ? AdminParse.Hm(c) : "21:00",
                existing?.Closed ?? false);
        }).ToList();
        return new StoreAttributesView(stores, regions, selectedId, attributes, hours);
    }

    public async Task<AdminResult> SaveStoreAttributesAsync(
        Guid storeId, AttributeValues input, IReadOnlyList<TradingHour> tradingHours, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        // Clamp sco_share to the 0–0.90 range (spec Section 5) before persisting.
        var scoShare = Js.Min(0.9, Js.Max(0, input.ScoShare));
        if (AdminInts.AsInt(input.McoRegisters) is not { } mco) return AdminResult.Fail(AdminInts.BadInt(input.McoRegisters));
        if (AdminInts.AsInt(input.ScoLanes) is not { } lanes) return AdminResult.Fail(AdminInts.BadInt(input.ScoLanes));
        DateOnly? opening = null;
        if (!string.IsNullOrEmpty(input.OpeningDate))
        {
            if (!AdminParse.TryDate(input.OpeningDate, out var d)) return AdminResult.Fail(AdminParse.BadDate(input.OpeningDate));
            opening = d;
        }

        await using var db = contexts.CreateSystem("Store Attributes: save a store's attributes and trading hours");
        var row = await db.StoreAttributes.FirstOrDefaultAsync(a => a.StoreId == storeId, ct);
        if (row is null)
        {
            row = new AttributesEntity { StoreId = storeId };
            db.StoreAttributes.Add(row);
        }
        row.HasSco = input.HasSco;
        row.IsNewScoModel = input.IsNewScoModel;
        row.McoRegisters = mco;
        row.ScoLanes = lanes;
        row.ScoEfficiency = input.ScoEfficiency;
        row.FirstStep = input.FirstStep;
        row.ScoShare = scoShare;
        row.OeTargetPct = input.OeTargetPct;
        row.OpeningDate = opening;
        row.PairStoreId = input.PairStoreId;
        row.NightFill = input.NightFill;
        row.UpdatedAt = clock.GetUtcNow();
        var attrResult = await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
        if (!attrResult.Ok) return attrResult;

        var existing = await db.StoreTradingHours.Where(t => t.StoreId == storeId).ToDictionaryAsync(t => t.DayOfWeek, ct);
        foreach (var t in tradingHours)
        {
            TimeOnly? open = null, close = null;
            if (!t.Closed)
            {
                if (t.OpenTime is not null)
                {
                    if (!AdminParse.TryTime(t.OpenTime, out var o)) return AdminResult.Fail(AdminParse.BadTime(t.OpenTime));
                    open = o;
                }
                if (t.CloseTime is not null)
                {
                    if (!AdminParse.TryTime(t.CloseTime, out var c)) return AdminResult.Fail(AdminParse.BadTime(t.CloseTime));
                    close = c;
                }
            }
            if (!existing.TryGetValue(t.DayOfWeek, out var h))
            {
                h = new StoreTradingHours { StoreId = storeId, DayOfWeek = t.DayOfWeek };
                db.StoreTradingHours.Add(h);
                existing[t.DayOfWeek] = h;
            }
            h.OpenTime = open;
            h.CloseTime = close;
            h.Closed = t.Closed;
        }
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex("^-|-$")]
    private static partial Regex SlugEdges();

    // Create a store + a default store_attributes row + default 7-day trading
    // hours (07:00-21:00, open). Slug is derived from the code. All three are
    // written together: a store row without its attributes or trading hours is
    // half-created - every planning screen assumes both exist - so a refusal
    // leaves a clean retry instead of a wreck.
    private async Task<AdminResult> CreateOneStoreAsync(Guid regionId, NewStore s, CancellationToken ct)
    {
        // Store IDs are canonicalised to the GXXX format (e.g. 25 → "G025").
        var code = StoreIds.FormatStoreId(s.Code);
        var name = Js.Trim(s.Name);
        if (code.Length == 0 || name.Length == 0) return AdminResult.Fail("Store ID and name are required");
        if (!AuStates.All.Contains(s.State)) return AdminResult.Fail($"Invalid state '{s.State}'");
        var slug = SlugEdges().Replace(NonSlug().Replace(code.ToLowerInvariant(), "-"), "");
        DateOnly? opening = null;
        if (!string.IsNullOrEmpty(s.OpeningDate))
        {
            if (!AdminParse.TryDate(s.OpeningDate, out var d)) return AdminResult.Fail($"{code}: {AdminParse.BadDate(s.OpeningDate)}");
            opening = d;
        }

        await using var db = contexts.CreateSystem("Store Attributes: create a store");
        var store = new Store
        {
            Code = code,
            Name = name,
            Slug = slug,
            RegionId = regionId,
            State = s.State,
            KioskToken = AdminStores.RandomHex(16),
        };
        db.Stores.Add(store);
        db.StoreAttributes.Add(new AttributesEntity { StoreId = store.Id, OpeningDate = opening });
        foreach (var d in Enumerable.Range(0, 7))
            db.StoreTradingHours.Add(new StoreTradingHours
            {
                StoreId = store.Id,
                DayOfWeek = d,
                OpenTime = new TimeOnly(7, 0),
                CloseTime = new TimeOnly(21, 0),
                Closed = false,
            });
        var result = await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
        return result.Ok ? result : AdminResult.Fail($"{code}: {result.Error}");
    }

    public async Task<AdminResult> AddStoreAsync(Guid regionId, NewStore store, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        return await CreateOneStoreAsync(regionId, store, ct);
    }

    public async Task<BulkAddResult> BulkAddStoresAsync(Guid regionId, IReadOnlyList<NewStore> stores, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var created = 0;
        foreach (var s in stores)
        {
            var res = await CreateOneStoreAsync(regionId, s, ct);
            if (!res.Ok) return new BulkAddResult(false, created, res.Error);
            created++;
        }
        return new BulkAddResult(true, created);
    }
}

public sealed class StoreAttributesModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IStoreAttributesService, StoreAttributesService>();
}
