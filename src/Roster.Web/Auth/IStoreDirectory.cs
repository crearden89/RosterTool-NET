namespace Roster.Web.Auth;

/// <summary>A store, as the shell and the store-scope check need it.</summary>
public sealed record StoreInfo
{
    public required Guid Id { get; init; }

    /// <summary>The store code ("G101").</summary>
    public required string Code { get; init; }

    public required string Name { get; init; }

    /// <summary>The area (EMSO patch) the store is in; an EMSO writes the stores of their area.</summary>
    public Guid? AreaId { get; init; }

    public Guid? RegionId { get; init; }

    /// <summary>The state, which sets the store's clock (kiosk "today" and "now").</summary>
    public string? State { get; init; }

    /// <summary>"G101 - Northside", as the store pickers write it.</summary>
    public string Label => $"{Code} - {Name}";
}

/// <summary>
/// The stores, for the store pickers and the store-scope check. The data layer
/// provides the real one; until then <see cref="ConfigurationDirectory"/> reads
/// <c>Directory:Stores</c>.
/// </summary>
public interface IStoreDirectory
{
    /// <summary>Every store, ordered by code.</summary>
    Task<IReadOnlyList<StoreInfo>> ListAsync(CancellationToken cancellationToken = default);
}
