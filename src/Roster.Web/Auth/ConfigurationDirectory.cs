// New: a stand-in user and store directory read from configuration, so the shell
// and its sign-in run before the data layer's (Roster.Data, owner D) is wired in.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Roster.Web.Auth;

/// <summary>
/// The <c>Directory</c> section of appsettings: a short list of stores and users
/// for the stand-in directory. Areas and regions are named here and turned into
/// stable ids.
/// </summary>
public sealed class DirectoryOptions
{
    public const string Section = "Directory";

    public List<StoreEntry> Stores { get; set; } = [];

    public List<UserEntry> Users { get; set; } = [];

    public sealed class StoreEntry
    {
        public Guid? Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Area { get; set; }
        public string? Region { get; set; }
        public string? State { get; set; }

        /// <summary>The store's kiosk link token (Floor Display and Load Planner).</summary>
        public string? KioskToken { get; set; }
    }

    public sealed class UserEntry
    {
        /// <summary>Matched, ignoring case, against the sign-in's username, Windows account, email or object id.</summary>
        public string Username { get; set; } = "";
        public string? DisplayName { get; set; }

        /// <summary>The tier when groups do not decide it ("SuperUser", "Store", "Pa", "Director" or "DSO", "Emso").</summary>
        public string? Tier { get; set; }

        /// <summary>The store a Store-tier user manages, by code.</summary>
        public string? Store { get; set; }

        /// <summary>The area an EMSO writes to, by name (as the stores give it).</summary>
        public string? Area { get; set; }
    }
}

internal sealed class ConfigurationDirectory(IOptionsMonitor<DirectoryOptions> options) : IUserDirectory, IStoreDirectory
{
    public Task<IReadOnlyList<DirectoryStore>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DirectoryStore>>(Stores().Select(s => s.Store).OrderBy(s => s.Code, StringComparer.Ordinal).ToList());

    public Task<DirectoryStore?> FindByKioskTokenAsync(string token, CancellationToken cancellationToken = default) =>
        Task.FromResult<DirectoryStore?>(Stores().FirstOrDefault(s => s.Token is not null && FixedTimeEquals(s.Token, token)).Store);

    public Task<SignedInUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default)
    {
        var entry = options.CurrentValue.Users.FirstOrDefault(u => Matches(u.Username, identity));
        if (entry is null) return Task.FromResult<SignedInUser?>(null);

        // The tier from configuration; the resolver replaces it when groups decide.
        // No tier from either: no access.
        string tier;
        if (TierKeys.TryNormalize(entry.Tier, out var configured)) tier = configured;
        else if (identity.GroupTier is { } fromGroups) tier = fromGroups;
        else return Task.FromResult<SignedInUser?>(null);

        var store = entry.Store is null ? null : Stores().Select(s => s.Store).FirstOrDefault(s => string.Equals(s.Code, entry.Store, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<SignedInUser?>(new SignedInUser
        {
            Id = StableId("user", entry.Username),
            Username = entry.Username,
            DisplayName = entry.DisplayName ?? identity.DisplayName ?? "",
            Tier = tier,
            StoreId = store?.Id,
            AreaId = entry.Area is not null ? StableId("area", entry.Area) : store?.AreaId,
            RegionId = store?.RegionId,
        });
    }

    private IEnumerable<(DirectoryStore Store, string? Token)> Stores() =>
        options.CurrentValue.Stores.Select(s => (new DirectoryStore(
            s.Id ?? StableId("store", s.Code), s.Code, s.Name,
            s.Area is null ? null : StableId("area", s.Area),
            s.Region is null ? null : StableId("region", s.Region),
            s.State), s.KioskToken));

    private static bool Matches(string username, SignInIdentity identity) =>
        !string.IsNullOrWhiteSpace(username) &&
        new[] { identity.Username, identity.AccountName, identity.Email, identity.ObjectId }
            .Any(v => string.Equals(v, username, StringComparison.OrdinalIgnoreCase));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>A stable id for a named thing, so the same name always has the same id.</summary>
    internal static Guid StableId(string kind, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{name.Trim().ToUpperInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
