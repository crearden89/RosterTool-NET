// Port of src/lib/auth.ts (getSessionUser) and src/app/(app)/admin/users/actions.ts,
// admin/emso/actions.ts, for single sign-on instead of passwords.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Data.Users;

/// <summary><see cref="IUserDirectory"/> over app_users.</summary>
public sealed class UserDirectory(IRosterDbContextFactory contexts, IOptions<UserDirectoryOptions> options, TimeProvider clock) : IUserDirectory
{
    public async Task<DirectoryUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = IdentityKey.From(identity);
        if (key.IsEmpty) return null;

        // Sign-in decides who may enter, so it reads past the row filters (the TypeScript app
        // read the profile with the signed-in user's own session, which RLS let through).
        await using var db = contexts.CreateSystem("sign-in");
        var users = db.AppUsers.IgnoreQueryFilters();

        AppUser? user = null;
        if (key.ObjectId is { } oid) user = await users.FirstOrDefaultAsync(u => u.EntraObjectId == oid, cancellationToken);
        if (user is null && key.Upn is { } upn) user = await users.FirstOrDefaultAsync(u => u.UserPrincipalName == upn, cancellationToken);
        if (user is null && key.Windows is { } win) user = await users.FirstOrDefaultAsync(u => u.WindowsAccount == win, cancellationToken);
        if (user is null && key.Username is { } name) user = await users.FirstOrDefaultAsync(u => u.Username == name, cancellationToken);

        var first = options.Value.FirstSuperUser;
        var isFirstSuperUser = first is { IsConfigured: true } && key.Matches(first);

        if (user is null)
        {
            if (!isFirstSuperUser) return null;
            user = new AppUser
            {
                Id = Guid.NewGuid(),
                Username = key.Upn ?? key.Windows ?? key.Username!,
                Tier = Tier.SuperUser,
                DisplayName = first!.DisplayName ?? identity.DisplayName,
            };
            db.AppUsers.Add(user);
        }
        else if (isFirstSuperUser && user.Tier != Tier.SuperUser)
        {
            // The configured first Super User is a Super User, whatever the row said.
            user.Tier = Tier.SuperUser;
            user.StoreId = user.RegionId = user.DirectorGroupId = null;
        }

        // Fill in the identity columns this sign-in proves, when no one else holds them.
        if (user.EntraObjectId is null && key.ObjectId is { } o && !await users.AnyAsync(u => u.EntraObjectId == o && u.Id != user.Id, cancellationToken))
            user.EntraObjectId = o;
        if (user.UserPrincipalName is null && key.Upn is { } p && !await users.AnyAsync(u => u.UserPrincipalName == p && u.Id != user.Id, cancellationToken))
            user.UserPrincipalName = p;
        if (user.WindowsAccount is null && key.Windows is { } w && !await users.AnyAsync(u => u.WindowsAccount == w && u.Id != user.Id, cancellationToken))
            user.WindowsAccount = w;
        if (string.IsNullOrWhiteSpace(user.DisplayName) && !string.IsNullOrWhiteSpace(identity.DisplayName))
            user.DisplayName = identity.DisplayName.Trim();
        user.LastSignInAt = clock.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);
        return DirectoryUser.From(user);
    }

    public async Task<DirectoryUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts.CreateSystem("session user");
        var user = await db.AppUsers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : DirectoryUser.From(user);
    }

    /// <summary>The normalised keys a sign-in can be matched on (all lower-case).</summary>
    private sealed record IdentityKey(string? ObjectId, string? Upn, string? Windows, string? Username)
    {
        public bool IsEmpty => ObjectId is null && Upn is null && Windows is null && Username is null;

        public static IdentityKey From(SignInIdentity i)
        {
            var method = i.Method.Trim().ToLowerInvariant();
            var username = Norm(i.Username);
            return new IdentityKey(
                Norm(i.ObjectId),
                method == "entra" ? username : null,
                Norm(i.AccountName) ?? (method == "windows" && username is not null && username.Contains('\\') ? username : null),
                username);
        }

        public bool Matches(FirstSuperUserOptions first) =>
            (Norm(first.ObjectId) is { } o && o == ObjectId)
            || (Norm(first.UserPrincipalName) is { } u && (u == Upn || u == Username))
            || (Norm(first.WindowsAccount) is { } w && (w == Windows || w == Username));
    }

    internal static string? Norm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

/// <summary><see cref="IUserAdmin"/>: the Users and EMSO screens, as the signed-in super user.</summary>
public sealed class UserAdmin(IRosterDbContextFactory contexts, IStoreScope scope) : IUserAdmin
{
    private static readonly Tier[] Creatable = [Tier.Store, Tier.Pa, Tier.Director, Tier.Emso, Tier.SuperUser];

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = contexts.Create();
        var users = await db.AppUsers.AsNoTracking().ToListAsync(cancellationToken);
        return users.OrderBy(u => Tiers.All.ToList().IndexOf(u.Tier)).ThenBy(u => u.Username, StringComparer.Ordinal).ToList();
    }

    public async Task<UserAdminResult> CreateAsync(NewAppUser input, CancellationToken cancellationToken = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return UserAdminResult.Fail("Only a super user can add logins.");
        var username = UserDirectory.Norm(input.Username);
        if (username is null) return UserAdminResult.Fail("A username is required.");
        if (!Creatable.Contains(input.Tier)) return UserAdminResult.Fail("Unknown access tier.");
        if (input.Tier == Tier.Store && input.StoreId is null) return UserAdminResult.Fail("Pick the person's store.");
        if (input.Tier == Tier.Emso && input.AreaId is null) return UserAdminResult.Fail("Pick the EMSO's area.");

        await using var db = contexts.Create();
        if (await db.AppUsers.AnyAsync(u => u.Username == username, cancellationToken))
            return UserAdminResult.Fail($"A login called '{username}' already exists.");

        var upn = UserDirectory.Norm(input.UserPrincipalName) ?? (username.Contains('@') ? username : null);
        var windows = UserDirectory.Norm(input.WindowsAccount) ?? (username.Contains('\\') ? username : null);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            Tier = input.Tier,
            // tier_scope: only the scope column the tier uses is kept.
            StoreId = input.Tier == Tier.Store ? input.StoreId : null,
            AreaId = input.Tier == Tier.Emso ? input.AreaId : null,
            RegionId = input.Tier is Tier.Pa or Tier.Director ? input.RegionId : null,
            DirectorGroupId = input.Tier == Tier.Director ? input.DirectorGroupId : null,
            UserPrincipalName = upn,
            WindowsAccount = windows,
            EntraObjectId = UserDirectory.Norm(input.EntraObjectId),
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim(),
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return UserAdminResult.Success(user.Id);
    }

    public async Task<UserAdminResult> SetEmsoAreaAsync(Guid emsoUserId, Guid areaId, CancellationToken cancellationToken = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return UserAdminResult.Fail("Only a super user can change logins.");
        if (areaId == Guid.Empty) return UserAdminResult.Fail("Pick an area.");
        await using var db = contexts.Create();
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == emsoUserId && u.Tier == Tier.Emso, cancellationToken);
        if (user is null) return UserAdminResult.Success(); // .eq("tier","emso") matched nothing: no error, as before
        user.AreaId = areaId;
        await db.SaveChangesAsync(cancellationToken);
        return UserAdminResult.Success(user.Id);
    }

    public async Task<UserAdminResult> SetTierAsync(Guid userId, Tier tier, Guid? storeId, Guid? areaId, CancellationToken cancellationToken = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return UserAdminResult.Fail("Only a super user can change logins.");
        if (tier == Tier.Store && storeId is null) return UserAdminResult.Fail("Pick the person's store.");
        if (tier == Tier.Emso && areaId is null) return UserAdminResult.Fail("Pick the EMSO's area.");
        await using var db = contexts.Create();
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return UserAdminResult.Fail("That login no longer exists.");
        user.Tier = tier;
        user.StoreId = tier == Tier.Store ? storeId : null;
        user.AreaId = tier == Tier.Emso ? areaId : user.AreaId is { } a && tier != Tier.SuperUser ? a : null;
        if (tier is Tier.SuperUser or Tier.Emso) user.RegionId = user.DirectorGroupId = null;
        await db.SaveChangesAsync(cancellationToken);
        return UserAdminResult.Success(user.Id);
    }

    public async Task<UserAdminResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!scope.HasTier(Tier.SuperUser)) return UserAdminResult.Fail("Only a super user can delete logins.");
        if (scope.User?.Id == userId) return UserAdminResult.Fail("You can't delete your own login.");

        await using var db = contexts.Create();
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return UserAdminResult.Success();
        if (await db.PlanSnapshots.IgnoreQueryFilters().AnyAsync(s => s.GeneratedBy == userId, cancellationToken))
            return UserAdminResult.Fail("This login generated a held plan or roster, so it can't be deleted.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.DeleteUserReferencesAsync(userId, cancellationToken);
        db.AppUsers.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return UserAdminResult.Success(userId);
    }
}
