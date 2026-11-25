namespace Roster.Web.Auth;

/// <summary>
/// Who someone is in the app, once their company sign-in has said who they are
/// in the company: the seam between single sign-on and the app's users.
/// </summary>
/// <remarks>
/// The data layer (src/Roster.Data) provides the real one, over the users
/// table. Until it is registered, <see cref="ConfigurationDirectory"/> reads a
/// short list from configuration (<c>Directory:Users</c>). The web app calls
/// it once per sign-in, through <see cref="AppUserResolver"/>, and keeps the
/// answer in the sign-in cookie.
/// </remarks>
public interface IUserDirectory
{
    /// <summary>
    /// The app's user for someone who has just signed in, or null when they have
    /// no access to the app. <see cref="SignInIdentity.GroupTier"/> is already
    /// applied by the caller; a directory only finds the person and their scope.
    /// </summary>
    Task<AppUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>What the company sign-in says about a person.</summary>
public sealed record SignInIdentity
{
    /// <summary>"entra" or "windows".</summary>
    public required string Method { get; init; }

    /// <summary>
    /// The name to look them up by: the user principal name for Entra ID
    /// ("jordan.lee@example.com"), the account name without its domain for
    /// Windows ("jlee").
    /// </summary>
    public required string Username { get; init; }

    /// <summary>The Windows account with its domain ("CORP\jlee").</summary>
    public string? AccountName { get; init; }

    /// <summary>The Entra ID object id (the <c>oid</c> claim).</summary>
    public string? ObjectId { get; init; }

    public string? Email { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>
    /// The tier the person's AD groups give them (<c>Auth:TierGroups</c>), when
    /// any of their groups is mapped to one.
    /// </summary>
    public Tier? GroupTier { get; init; }
}
