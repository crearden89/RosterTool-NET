// New: the invented organisation the development seeder builds. Mirrors scripts/seed.mjs
// (director group "Group A", an "Example Store", the logins superuser, store1, pa1 and
// director1), widened to a few stores across states, with an area per EMSO. Every store
// number, name and person here is invented.
namespace Roster.Data.Seed;

/// <summary>An invented store.</summary>
public sealed record SeedStore(
    string Code,
    string Name,
    string Slug,
    string State,
    string RegionName,
    string AreaName,
    string DsoName);

/// <summary>An invented login: its tier key ("store") and the store / area / region / group it is scoped to.</summary>
public sealed record SeedUser(
    string Username,
    string DisplayName,
    string TierKey,
    string? StoreCode = null,
    string? AreaName = null,
    string? RegionName = null,
    string? DirectorGroupName = null);

/// <summary>The invented organisation: director group → region → area → store, and the logins.</summary>
public static class SeedCatalogue
{
    /// <summary>The store scripts/seed.mjs made ("Example Store"), under the code the sample files use.</summary>
    public const string ExampleStoreCode = "G001";

    public static readonly IReadOnlyList<string> DirectorGroups = ["Group A", "Group B"];

    /// <summary>Region name → director group. The 3-digit prefix keys the region-varying admin task days.</summary>
    public static readonly IReadOnlyList<(string Name, string DirectorGroup)> Regions =
    [
        ("501 - Northvale", "Group A"),
        ("504 - Eastbrook", "Group A"),
        ("507 - Southmere", "Group B"),
    ];

    /// <summary>Area (named for its Area Manager, as the org extract names them) → region.</summary>
    public static readonly IReadOnlyList<(string Name, string Region)> Areas =
    [
        ("Robin Hale", "501 - Northvale"),
        ("Casey Marlow", "504 - Eastbrook"),
        ("Drew Penrose", "507 - Southmere"),
    ];

    public static readonly IReadOnlyList<SeedStore> Stores =
    [
        new(ExampleStoreCode, "Example Store", "store-1", "NSW", "501 - Northvale", "Robin Hale", "Jules Arden"),
        new("G102", "Northvale Fair", "g102-northvale-fair", "NSW", "501 - Northvale", "Robin Hale", "Jules Arden"),
        new("G215", "Eastbrook Central", "g215-eastbrook-central", "VIC", "504 - Eastbrook", "Casey Marlow", "Jules Arden"),
        new("G218", "Lakeside Grove", "g218-lakeside-grove", "VIC", "504 - Eastbrook", "Casey Marlow", "Jules Arden"),
        new("G327", "Southmere Plaza", "g327-southmere-plaza", "QLD", "507 - Southmere", "Drew Penrose", "Kit Morrow"),
        new("G340", "Harbour Point", "g340-harbour-point", "WA", "507 - Southmere", "Drew Penrose", "Kit Morrow"),
    ];

    /// <summary>The logins. scripts/seed.mjs made the first four; the EMSO and second store logins are new.</summary>
    public static readonly IReadOnlyList<SeedUser> Users =
    [
        new("superuser", "Avery Quill", "super_user"),
        new("store1", "Morgan Fenwick", "store", StoreCode: ExampleStoreCode),
        new("pa1", "Rowan Ashby", "pa", RegionName: "501 - Northvale"),
        new("director1", "Jules Arden", "director", DirectorGroupName: "Group A"),
        new("emso1", "Robin Hale", "emso", AreaName: "Robin Hale"),
        new("emso2", "Casey Marlow", "emso", AreaName: "Casey Marlow"),
        new("store2", "Tamsin Okafor", "store", StoreCode: "G215"),
    ];
}
