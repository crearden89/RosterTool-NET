// Port of src/app/(app)/admin/users/page.tsx (its reads) and actions.ts. People sign in
// through the company's single sign-on here, so a login has no password: the
// TypeScript app's password field and its Reset password action have no counterpart.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Users;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.Users;

public sealed record UserRow(Guid Id, string Username, Tier Tier, Guid? StoreId, Guid? AreaId);

public sealed record UserStoreRow(Guid Id, string Code, string Name, Guid? AreaId);

public sealed record AreaRow(Guid Id, string Name);

/// <param name="MyId">The signed-in super user (their row says YOU and cannot be deleted).</param>
public sealed record UsersView(IReadOnlyList<UserRow> Users, IReadOnlyList<UserStoreRow> Stores, IReadOnlyList<AreaRow> Areas, Guid MyId);

public interface IUsersService
{
    /// <summary>Every login (by tier, then username), the stores and the areas.</summary>
    Task<UsersView> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// createAppUser: a per-person login. Scope requirements follow the tier_scope
    /// constraint: store needs a store, emso needs an area, pa/dso/super need nothing.
    /// </summary>
    Task<AdminResult> CreateAppUserAsync(string username, Tier tier, Guid? storeId, Guid? areaId, CancellationToken ct = default);

    /// <summary>deleteAppUser: never your own login.</summary>
    Task<AdminResult> DeleteAppUserAsync(Guid userId, CancellationToken ct = default);
}

public static class UserScope
{
    /// <summary>Creation order mirrors how often each tier gets made.</summary>
    public static readonly IReadOnlyList<Tier> CreationOrder = [Tier.Store, Tier.Emso, Tier.Pa, Tier.Director, Tier.SuperUser];

    /// <summary>The Scope column: the store, the area, everything, or the whole network.</summary>
    public static string Label(UserRow u, IReadOnlyList<UserStoreRow> stores, IReadOnlyList<AreaRow> areas)
    {
        if (u.Tier == Tier.Store)
        {
            var s = stores.FirstOrDefault(x => x.Id == u.StoreId);
            return s is null ? "—" : $"{s.Code} - {s.Name}";
        }
        if (u.Tier == Tier.Emso) return areas.FirstOrDefault(a => a.Id == u.AreaId)?.Name ?? "—";
        if (u.Tier == Tier.SuperUser) return "Everything";
        return "Whole network";
    }
}

internal sealed class UsersService(IServiceProvider services) : IUsersService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();
    private IUserAdmin users => services.GetRequiredService<IUserAdmin>();

    public async Task<UsersView> LoadAsync(CancellationToken ct = default)
    {
        var me = scope.RequireSuperUser();
        var list = await users.ListAsync(ct);
        await using var db = contexts.CreateSystem("Users: stores and areas for the login form");
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Code)
            .Select(s => new UserStoreRow(s.Id, s.Code, s.Name, s.AreaId)).ToListAsync(ct);
        var areas = await db.Areas.AsNoTracking().OrderBy(a => a.Name).Select(a => new AreaRow(a.Id, a.Name)).ToListAsync(ct);
        return new UsersView(list.Select(u => new UserRow(u.Id, u.Username, u.Tier, u.StoreId, u.AreaId)).ToList(), stores, areas, me.Id);
    }

    public async Task<AdminResult> CreateAppUserAsync(string username, Tier tier, Guid? storeId, Guid? areaId, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var res = await users.CreateAsync(new NewAppUser
        {
            Username = username,
            Tier = tier,
            StoreId = tier == Tier.Store ? storeId : null,
            AreaId = tier == Tier.Emso ? areaId : null,
        }, ct);
        return res.Ok ? AdminResult.Success : AdminResult.Fail(res.Error ?? "Could not create the login.");
    }

    public async Task<AdminResult> DeleteAppUserAsync(Guid userId, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var res = await users.DeleteAsync(userId, ct);
        return res.Ok ? AdminResult.Success : AdminResult.Fail(res.Error ?? "Could not delete the login.");
    }
}

public sealed class UsersModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IUsersService, UsersService>();
}
