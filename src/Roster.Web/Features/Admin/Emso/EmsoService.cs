// Port of src/app/(app)/admin/emso/page.tsx (its reads) and actions.ts. EMSOs sign in
// through the company's single sign-on, so a new EMSO login has no password.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Jobs;
using Roster.Data.Scope;
using Roster.Data.Users;
using Roster.Engine.Core;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.Emso;

public sealed record EmsoRow(Guid Id, string Username, Guid? AreaId);

public sealed record EmsoAreaRow(Guid Id, string Name, Guid? RegionId);

public sealed record EmsoRegionRow(Guid Id, string Name);

public sealed record EmsoStoreRow(Guid Id, string Code, string Name, Guid? AreaId);

public sealed record EmsoView(
    IReadOnlyList<EmsoRow> Emsos, IReadOnlyList<EmsoAreaRow> Areas, IReadOnlyList<EmsoRegionRow> Regions, IReadOnlyList<EmsoStoreRow> Stores);

public sealed record EmsoCheckRun(bool Ok, int Created = 0, int Updated = 0, int Failed = 0, string? Error = null)
{
    /// <summary>The line the page shows after a run.</summary>
    public string Message =>
        Ok
            ? $"Roster check run: {Created} new, {Updated} updated alert(s)." + (Failed > 0 ? $" {Failed} alert(s) FAILED to write." : "")
            : $"Error: {Error}";
}

public interface IEmsoService
{
    /// <summary>The EMSO logins (by username), the areas, the regions and the stores.</summary>
    Task<EmsoView> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// createEmso: an EMSO is scoped to ONE area (the Area Manager patch of ~6 stores) -
    /// they can generate, approve and edit for every store in it, so the area is required.
    /// </summary>
    Task<AdminResult> CreateEmsoAsync(string username, Guid? areaId, CancellationToken ct = default);

    /// <summary>setEmsoArea: move an EMSO to a different area.</summary>
    Task<AdminResult> SetEmsoAreaAsync(Guid emsoUserId, Guid? areaId, CancellationToken ct = default);

    /// <summary>
    /// runEmsoCheckNow: the EMSO unapproved-roster check on demand (spec §8) - normally
    /// a Wed 18:00 scheduled job, exposed here so a super user can trigger/verify it.
    /// </summary>
    Task<EmsoCheckRun> RunEmsoCheckNowAsync(CancellationToken ct = default);
}

internal sealed class EmsoService(IServiceProvider services) : IEmsoService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();
    private IUserAdmin users => services.GetRequiredService<IUserAdmin>();
    private IEmsoRosterCheck check => services.GetRequiredService<IEmsoRosterCheck>();

    public async Task<EmsoView> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("EMSO Managers: EMSO logins, areas and stores");
        var emsos = await db.AppUsers.AsNoTracking().Where(u => u.Tier == Tier.Emso).ToListAsync(ct);
        var areas = await db.Areas.AsNoTracking().OrderBy(a => a.Name).Select(a => new EmsoAreaRow(a.Id, a.Name, a.RegionId)).ToListAsync(ct);
        var regions = await db.Regions.AsNoTracking().OrderBy(r => r.Name).Select(r => new EmsoRegionRow(r.Id, r.Name)).ToListAsync(ct);
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new EmsoStoreRow(s.Id, s.Code, s.Name, s.AreaId)).ToListAsync(ct);
        return new EmsoView(
            emsos.OrderBy(u => u.Username, StringComparer.Ordinal).Select(u => new EmsoRow(u.Id, u.Username, u.AreaId)).ToList(),
            areas, regions, stores);
    }

    public async Task<AdminResult> CreateEmsoAsync(string username, Guid? areaId, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (Js.Trim(username).Length == 0) return AdminResult.Fail("A username is required.");
        if (areaId is null) return AdminResult.Fail("Pick the area this EMSO oversees.");
        var res = await users.CreateAsync(new NewAppUser { Username = username, Tier = Tier.Emso, AreaId = areaId }, ct);
        return res.Ok ? AdminResult.Success : AdminResult.Fail(res.Error ?? "Could not create the login.");
    }

    public async Task<AdminResult> SetEmsoAreaAsync(Guid emsoUserId, Guid? areaId, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (areaId is null) return AdminResult.Fail("Pick an area.");
        var res = await users.SetEmsoAreaAsync(emsoUserId, areaId.Value, ct);
        return res.Ok ? AdminResult.Success : AdminResult.Fail(res.Error ?? "Could not save the area.");
    }

    public async Task<EmsoCheckRun> RunEmsoCheckNowAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var r = await check.RunEmsoUnapprovedCheckAsync(ct);
        return new EmsoCheckRun(true, r.Created, r.Updated, r.Failed);
    }
}

public sealed class EmsoModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IEmsoService, EmsoService>();
}
