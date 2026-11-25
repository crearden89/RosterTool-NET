// New: an in-memory user and store directory standing in for the data layer's
// (Roster.Data.Users.UserDirectory, Roster.Data.Scope.StoreDirectory, which need
// SQL Server) in the web tests.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Users;

namespace Roster.Web.Tests.Infrastructure;

public sealed class FakeDirectory : IUserDirectory, IStoreDirectory
{
    public static readonly Guid Area1 = Id("area", "Area 1");
    public static readonly Guid Area2 = Id("area", "Area 2");

    public List<(StoreInfo Store, string Token)> Stores { get; } =
    [
        (new StoreInfo { Id = Id("store", "G101"), Code = "G101", Name = "Northside", AreaId = Area1, State = "NSW" }, "secret-g101"),
        (new StoreInfo { Id = Id("store", "G102"), Code = "G102", Name = "Riverside", AreaId = Area1, State = "NSW" }, "secret-g102"),
        (new StoreInfo { Id = Id("store", "G205"), Code = "G205", Name = "Hillcrest", AreaId = Area2, State = "VIC" }, "secret-g205"),
    ];

    /// <summary>People the directory knows, by the name they sign in with.</summary>
    public List<DirectoryUser> Users { get; } = [];

    public FakeDirectory WithUser(string username, Tier tier, string? storeCode = null)
    {
        var store = storeCode is null ? null : Stores.First(s => s.Store.Code == storeCode).Store;
        Users.Add(new DirectoryUser
        {
            Id = Id("user", username), Username = username, Tier = tier,
            StoreId = tier == Tier.Store ? store?.Id : null,
            AreaId = tier == Tier.Emso ? store?.AreaId : null,
        });
        return this;
    }

    public Task<DirectoryUser?> FindAsync(SignInIdentity identity, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(u =>
            new[] { identity.Username, identity.AccountName, identity.Email, identity.ObjectId }
                .Any(v => string.Equals(v, u.Username, StringComparison.OrdinalIgnoreCase))));

    public Task<DirectoryUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

    public Task<IReadOnlyList<StoreInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoreInfo>>(Stores.Select(s => s.Store).OrderBy(s => s.Code, StringComparer.Ordinal).ToList());

    public Task<StoreInfo?> FindAsync(Guid storeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stores.Select(s => s.Store).FirstOrDefault(s => s.Id == storeId));

    public Task<StoreInfo?> FindByKioskTokenAsync(string token, CancellationToken cancellationToken = default) =>
        Task.FromResult<StoreInfo?>(Stores.FirstOrDefault(s => s.Token == token).Store);

    /// <summary>Puts this directory in place of the data layer's.</summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddScoped<IUserDirectory>(_ => this);
        services.AddScoped<IStoreDirectory>(_ => this);
    }

    public static Guid Id(string kind, string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{name}")).AsSpan(0, 16));
}
