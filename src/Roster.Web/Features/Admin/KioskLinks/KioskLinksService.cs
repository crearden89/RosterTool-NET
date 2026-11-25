// Port of src/app/(app)/admin/kiosk-links/page.tsx (its read) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Scope;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.KioskLinks;

/// <summary>A store and the token behind its Floor Display and Load Planner links.</summary>
public sealed record KioskLinkRow(Guid Id, string Code, string Name, string KioskToken)
{
    public string FloorPath => $"/floor/{KioskToken}";
    public string LoadPath => $"/load/{KioskToken}";
}

public interface IKioskLinksService
{
    /// <summary>Every store with its kiosk token, by code.</summary>
    Task<IReadOnlyList<KioskLinkRow>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// rotateKioskToken: the old Floor Display / Load Planner links stop working
    /// immediately, and the tablet gets re-pinned with the new one.
    /// </summary>
    Task<AdminResult> RotateKioskTokenAsync(Guid storeId, CancellationToken ct = default);
}

internal sealed class KioskLinksService(IRosterDbContextFactory contexts, IStoreScope scope) : IKioskLinksService
{
    public async Task<IReadOnlyList<KioskLinkRow>> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Kiosk Links: every store's kiosk token");
        return await db.Stores.AsNoTracking()
            .OrderBy(s => s.Code)
            .Select(s => new KioskLinkRow(s.Id, s.Code, s.Name, s.KioskToken))
            .ToListAsync(ct);
    }

    public async Task<AdminResult> RotateKioskTokenAsync(Guid storeId, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Kiosk Links: rotate a store's kiosk token");
        var token = AdminStores.RandomHex(16);
        return await AdminDb.WriteAsync(() =>
            db.Stores.Where(s => s.Id == storeId).ExecuteUpdateAsync(u => u.SetProperty(s => s.KioskToken, token), ct));
    }
}

public sealed class KioskLinksModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IKioskLinksService, KioskLinksService>();
}
