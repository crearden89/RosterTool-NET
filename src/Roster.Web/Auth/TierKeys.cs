// New: reading the tier a sign-in carries. The tiers, their stored keys and labels
// are the data layer's (Roster.Data.Entities.Tier and Tiers, from src/lib/constants.ts).

using Roster.Data.Entities;

namespace Roster.Web.Auth;

internal static class TierKeys
{
    /// <summary>Every tier key, in the order the TypeScript app lists the tiers.</summary>
    public static IReadOnlyList<string> All { get; } = Tiers.All.Select(t => t.Key()).ToList();

    /// <summary>
    /// The stored key for a tier written either way: "super_user" or "SuperUser",
    /// "director", "Director" or "DSO". Case does not matter.
    /// </summary>
    public static bool TryNormalize(string? value, out string key)
    {
        var compact = value?.Trim().Replace(" ", "").Replace("_", "");
        if (string.Equals(compact, "superuser", StringComparison.OrdinalIgnoreCase)) compact = "super_user";
        key = Tiers.TryParse(compact, out var tier) ? tier.Key() : "";
        return key.Length > 0;
    }

    /// <summary>The tier for a stored key.</summary>
    public static Tier ToTier(string key) => Tiers.FromKey(key);

    /// <summary>The label under the person's name in the menu (TIER_LABELS).</summary>
    public static string Label(string key) => Tiers.TryParse(key, out var tier) ? tier.Label() : key;

    /// <summary>PA and DSO never write (isReadOnlyTier); EMSO writes area-wide.</summary>
    public static bool IsReadOnly(string key) => Tiers.TryParse(key, out var tier) && Roster.Data.Scope.AccessMatrix.IsReadOnlyTier(tier);
}
