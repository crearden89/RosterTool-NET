namespace Roster.Web.Auth;

/// <summary>
/// The access tiers (spec Section 9), as the TypeScript app has them in
/// <c>src/lib/constants.ts</c>. Who may write what is in <see cref="AccessRules"/>.
/// </summary>
public enum Tier
{
    /// <summary>Writes everywhere; the only tier that sees the Super User pages.</summary>
    SuperUser,

    /// <summary>A store's own manager (per-person SM/ASM logins): writes their own store.</summary>
    Store,

    /// <summary>PA: reads the whole network, writes nothing.</summary>
    Pa,

    /// <summary>DSO (the TypeScript app's "director" tier): reads the whole network, writes nothing.</summary>
    Director,

    /// <summary>EMSO: writes every store in their area (the Area Manager patch).</summary>
    Emso,
}

public static class Tiers
{
    /// <summary>Every tier, in the order the TypeScript app lists them.</summary>
    public static IReadOnlyList<Tier> All { get; } = [Tier.SuperUser, Tier.Store, Tier.Pa, Tier.Director, Tier.Emso];

    /// <summary>The label under the person's name in the menu (TIER_LABELS).</summary>
    public static string Label(this Tier tier) => tier switch
    {
        Tier.SuperUser => "Super User",
        Tier.Store => "Store",
        Tier.Pa => "PA (network read-only)",
        Tier.Director => "DSO (network read-only)",
        Tier.Emso => "EMSO (area)",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    /// <summary>The stored value, as the TypeScript app stores it ("super_user", "store", "pa", "director", "emso").</summary>
    public static string Key(this Tier tier) => tier switch
    {
        Tier.SuperUser => "super_user",
        Tier.Store => "store",
        Tier.Pa => "pa",
        Tier.Director => "director",
        Tier.Emso => "emso",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    /// <summary>
    /// Reads a tier from its stored value ("super_user") or its name here
    /// ("SuperUser"), ignoring case. "DSO" is accepted for Director.
    /// </summary>
    public static bool TryParse(string? value, out Tier tier)
    {
        tier = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        foreach (var t in All)
        {
            if (string.Equals(v, t.Key(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, t.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                tier = t;
                return true;
            }
        }
        if (string.Equals(v, "dso", StringComparison.OrdinalIgnoreCase))
        {
            tier = Tier.Director;
            return true;
        }
        return false;
    }

    /// <summary>
    /// PA and DSO never write (isReadOnlyTier). EMSO is deliberately not here: it
    /// writes area-wide.
    /// </summary>
    public static bool IsReadOnly(this Tier tier) => tier is Tier.Pa or Tier.Director;
}
