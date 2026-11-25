// New: reading the tier a sign-in carries. The tiers and their labels are the
// engine's constants (Roster.Engine.Core.Tiers, from src/lib/constants.ts).

using Roster.Engine.Core;

namespace Roster.Web.Auth;

internal static class TierKeys
{
    /// <summary>Every tier, in the order the TypeScript app lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Tiers.SuperUser, Tiers.Store, Tiers.Pa, Tiers.Director, Tiers.Emso];

    /// <summary>
    /// The stored key for a tier written either way: "super_user" or "SuperUser",
    /// "director", "Director" or "DSO". Case does not matter.
    /// </summary>
    public static bool TryNormalize(string? value, out string key)
    {
        key = "";
        var v = value?.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        key = v switch
        {
            "superuser" => Tiers.SuperUser,
            "store" => Tiers.Store,
            "pa" => Tiers.Pa,
            "director" or "dso" => Tiers.Director,
            "emso" => Tiers.Emso,
            _ => "",
        };
        return key.Length > 0;
    }

    /// <summary>The label under the person's name in the menu (TIER_LABELS).</summary>
    public static string Label(string key) => Tiers.Labels.TryGetValue(key, out var label) ? label : key;

    /// <summary>PA and DSO never write (isReadOnlyTier); EMSO writes area-wide.</summary>
    public static bool IsReadOnly(string key) => key is Tiers.Pa or Tiers.Director;
}
