using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Roster.Web.Auth;

/// <summary>
/// The <c>Directory</c> section of appsettings: a short list of stores and users
/// for the stand-in directory, until the data layer's directory is registered.
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

        /// <summary>The tier, when groups do not decide it ("SuperUser", "Store", "Pa", "Director", "Emso").</summary>
        public string? Tier { get; set; }

        /// <summary>The store a Store-tier user manages, by code.</summary>
        public string? Store { get; set; }

        /// <summary>The area an EMSO writes to, by name (as the stores give it).</summary>
        public string? Area { get; set; }
    }
}

/// <summary>
/// A stand-in <see cref="IUserDirectory"/> and <see cref="IStoreDirectory"/>
/// over configuration, so the shell runs before the data layer is wired in. It
/// is registered only when nothing else provides those services. Areas and
/// regions are named in configuration and turned into stable ids.
/// </summary>
public sealed class ConfigurationDirectory(IOptionsMonitor<DirectoryOptions> options) : IUserDirectory, IStoreDirectory, Kiosk.IKioskDirectory
{
    public Task<IReadOnlyList<StoreInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoreInfo>>(Stores().Select(s => s.Info).OrderBy(s => s.Code, StringComparer.Ordinal).ToList());

    public Task<AppUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default)
    {
        var entry = options.CurrentValue.Users.FirstOrDefault(u => Matches(u.Username, identity));
        if (entry is null) return Task.FromResult<AppUser?>(null);

        // The tier from configuration; the resolver replaces it when groups
        // decide. No tier from either, no access.
        Tier tier;
        if (Tiers.TryParse(entry.Tier, out var configured)) tier = configured;
        else if (identity.GroupTier is { } fromGroups) tier = fromGroups;
        else return Task.FromResult<AppUser?>(null);

        var store = entry.Store is null ? null : Stores().FirstOrDefault(s => string.Equals(s.Info.Code, entry.Store, StringComparison.OrdinalIgnoreCase));
        var areaId = entry.Area is not null ? StableId("area", entry.Area) : store?.Info.AreaId;
        return Task.FromResult<AppUser?>(new AppUser
        {
            Id = StableId("user", entry.Username),
            Username = entry.Username,
            DisplayName = entry.DisplayName ?? identity.DisplayName ?? "",
            Tier = tier,
            StoreId = store?.Info.Id,
            AreaId = areaId,
            RegionId = store?.Info.RegionId,
        });
    }

    public Task<Kiosk.KioskStore?> FindByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var match = Stores().FirstOrDefault(s => s.Token is not null && FixedTimeEquals(s.Token, token));
        return Task.FromResult(match is null ? null : new Kiosk.KioskStore(match.Info));
    }

    private IEnumerable<(StoreInfo Info, string? Token)> Stores() =>
        options.CurrentValue.Stores.Select(s => (new StoreInfo
        {
            Id = s.Id ?? StableId("store", s.Code),
            Code = s.Code,
            Name = s.Name,
            AreaId = s.Area is null ? null : StableId("area", s.Area),
            RegionId = s.Region is null ? null : StableId("region", s.Region),
            State = s.State,
        }, s.KioskToken));

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
