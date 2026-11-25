// Port of the user look-up in src/lib/auth.ts (getSessionUser), for single sign-on:
// the TypeScript app signed people in with a username and password against Supabase
// Auth; here the company's sign-in (Entra ID or Windows) says who they are and the
// directory finds their app_users row.
using Roster.Data.Entities;

namespace Roster.Data.Users;

/// <summary>What the company sign-in says about a person.</summary>
public sealed record SignInIdentity
{
    /// <summary>"entra", "windows" or "development".</summary>
    public required string Method { get; init; }

    /// <summary>
    /// The name to look them up by: the user principal name for Entra ID
    /// ("jordan.lee@example.com"), the account name without its domain for Windows ("jlee").
    /// </summary>
    public required string Username { get; init; }

    /// <summary>The Windows account with its domain ("CORP\jlee").</summary>
    public string? AccountName { get; init; }

    /// <summary>The Entra ID object id (the oid claim).</summary>
    public string? ObjectId { get; init; }

    public string? Email { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The tier the person's AD groups give them, when any group is mapped to one.</summary>
    public Tier? GroupTier { get; init; }
}

/// <summary>A person the app knows: their tier and the scope it applies to (one app_users row).</summary>
public sealed record DirectoryUser
{
    public required Guid Id { get; init; }

    /// <summary>The login name the directory knows them by.</summary>
    public required string Username { get; init; }

    /// <summary>The name to show; empty when the directory has none.</summary>
    public string DisplayName { get; init; } = "";

    public required Tier Tier { get; init; }

    /// <summary>The store a Store-tier user manages.</summary>
    public Guid? StoreId { get; init; }

    public Guid? RegionId { get; init; }

    /// <summary>The area an EMSO writes to.</summary>
    public Guid? AreaId { get; init; }

    public Guid? DirectorGroupId { get; init; }

    /// <summary>The scope the data layer filters by.</summary>
    public Scope.ScopedUser ToScopedUser() => new(Id, Username, Tier, StoreId, RegionId, AreaId, DirectorGroupId);

    public static DirectoryUser From(AppUser u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName ?? "",
        Tier = u.Tier,
        StoreId = u.StoreId,
        RegionId = u.RegionId,
        AreaId = u.AreaId,
        DirectorGroupId = u.DirectorGroupId,
    };
}

/// <summary>
/// The seam between single sign-on and the app's users. The web app calls it once per
/// sign-in and keeps the answer in the sign-in cookie; it applies the group tier itself
/// (SignInIdentity.GroupTier) when the groups decide the tier.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// The app user for someone who has just signed in, or null when they have no access.
    /// Matched on the Entra object id, then the user principal name, then the Windows
    /// account, then the username; a match fills in the identity columns it was missing
    /// and records the sign-in. The configured first Super User is created on their first
    /// sign-in if no row exists yet.
    /// </summary>
    Task<DirectoryUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>One user by id (the session's user), or null.</summary>
    Task<DirectoryUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Settings for the user directory (section "Roster:Users").</summary>
public sealed class UserDirectoryOptions
{
    public const string Section = "Roster:Users";

    /// <summary>
    /// The first Super User, by configuration: the person who sets everyone else up. Created
    /// (or promoted) on their first sign-in and by the seeder. Give at least one of the three.
    /// </summary>
    public FirstSuperUserOptions? FirstSuperUser { get; set; }
}

public sealed class FirstSuperUserOptions
{
    /// <summary>Entra ID object id.</summary>
    public string? ObjectId { get; set; }

    /// <summary>User principal name, e.g. "jordan.lee@example.com".</summary>
    public string? UserPrincipalName { get; set; }

    /// <summary>Windows account, DOMAIN\user.</summary>
    public string? WindowsAccount { get; set; }

    public string? DisplayName { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ObjectId) || !string.IsNullOrWhiteSpace(UserPrincipalName) || !string.IsNullOrWhiteSpace(WindowsAccount);
}

/// <summary>A new login, created on Admin → Users / Admin → EMSO (no password: they sign in through SSO).</summary>
public sealed record NewAppUser
{
    /// <summary>The name they sign in as: a user principal name or a Windows account name.</summary>
    public required string Username { get; init; }

    public required Tier Tier { get; init; }

    public Guid? StoreId { get; init; }

    public Guid? AreaId { get; init; }

    public Guid? RegionId { get; init; }

    public Guid? DirectorGroupId { get; init; }

    public string? UserPrincipalName { get; init; }

    public string? WindowsAccount { get; init; }

    public string? EntraObjectId { get; init; }

    public string? DisplayName { get; init; }
}

/// <summary>The outcome of an admin change: ok, or the reason shown to the super user.</summary>
public sealed record UserAdminResult(bool Ok, string? Error = null, Guid? UserId = null)
{
    public static UserAdminResult Success(Guid? id = null) => new(true, null, id);
    public static UserAdminResult Fail(string error) => new(false, error);
}

/// <summary>
/// The Users and EMSO admin screens' writes (src/app/(app)/admin/users/actions.ts and
/// admin/emso/actions.ts), without passwords. Super user only.
/// </summary>
public interface IUserAdmin
{
    /// <summary>Every user, ordered by tier then username.</summary>
    Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>createAppUser / createEmso: scope requirements follow the tier_scope constraint.</summary>
    Task<UserAdminResult> CreateAsync(NewAppUser user, CancellationToken cancellationToken = default);

    /// <summary>setEmsoArea: move an EMSO to a different area.</summary>
    Task<UserAdminResult> SetEmsoAreaAsync(Guid emsoUserId, Guid areaId, CancellationToken cancellationToken = default);

    /// <summary>Change a user's tier and scope (the TypeScript app did this by hand in SQL).</summary>
    Task<UserAdminResult> SetTierAsync(Guid userId, Tier tier, Guid? storeId, Guid? areaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// deleteAppUser: refuses your own login. The references Postgres set to null on delete
    /// (uploads, locks, audit rows, reviews) are set to null here in the same transaction;
    /// notifications go with the user. A user who generated a held plan cannot be deleted
    /// (Postgres declared no delete action for plan_snapshots.generated_by).
    /// </summary>
    Task<UserAdminResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
