// New: the seam between single sign-on and the app's users (the TypeScript app
// matched a Supabase login to its app_users row in src/lib/auth.ts).
//
// A stand-in until the data layer's user lookup (Roster.Data, owner D) is on main:
// then sign-in calls that instead and this file goes. Internal, so no page depends on it.

namespace Roster.Web.Auth;

/// <summary>What the company sign-in says about a person.</summary>
internal sealed record SignInIdentity
{
    /// <summary>"entra" or "windows".</summary>
    public required string Method { get; init; }

    /// <summary>
    /// The name to find them by: the user principal name for Entra ID
    /// ("jordan.lee@example.com"), the account name without its domain for
    /// Windows ("jlee").
    /// </summary>
    public required string Username { get; init; }

    /// <summary>The Windows account with its domain ("CORP\jlee").</summary>
    public string? AccountName { get; init; }

    /// <summary>The Entra ID object id (the oid claim).</summary>
    public string? ObjectId { get; init; }

    public string? Email { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>The tier key the person's AD groups give (<c>Auth:TierGroups</c>), when any of them is mapped.</summary>
    public string? GroupTier { get; init; }
}

/// <summary>Finds the app's user for someone the company sign-in has identified.</summary>
internal interface IUserDirectory
{
    /// <summary>The app user (tier and scope), or null when the person has no access.</summary>
    Task<SignedInUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>A store as the development sign-in offers it.</summary>
internal sealed record DirectoryStore(Guid Id, string Code, string Name, Guid? AreaId, Guid? RegionId, string? State);

/// <summary>The stores, for the development sign-in and the kiosk link check (stand-in, as above).</summary>
internal interface IStoreDirectory
{
    /// <summary>Every store, ordered by code.</summary>
    Task<IReadOnlyList<DirectoryStore>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The store whose kiosk link carries this token, or null.</summary>
    Task<DirectoryStore?> FindByKioskTokenAsync(string token, CancellationToken cancellationToken = default);
}
