namespace Roster.Data.Entities;

// ─── Users and access tiers ───────────────────────────────────────────────────
// Postgres: 0001 app_users (then auth.users-backed), 0013/0016 tier_scope, 0016 area_id.
// In this edition there are no passwords: a row is matched to a single-sign-on identity
// (Entra object id, user principal name or Windows account) by Users/UserDirectory.

/// <summary>The five access tiers. Stored as the same lower-case strings Postgres used.</summary>
public enum Tier
{
    /// <summary>Everything, including every configuration screen.</summary>
    SuperUser,
    /// <summary>One store: plan, generate, approve, edit.</summary>
    Store,
    /// <summary>Read-only, whole network ("PA").</summary>
    Pa,
    /// <summary>Read-only, whole network ("DSO"; stored as "director").</summary>
    Director,
    /// <summary>Write across one area of about six stores.</summary>
    Emso,
}

/// <summary>The stored tier values and their labels (src/lib/constants.ts Tier, TIER_LABELS).</summary>
public static class Tiers
{
    public const string SuperUserKey = "super_user", StoreKey = "store", PaKey = "pa", DirectorKey = "director", EmsoKey = "emso";

    /// <summary>Every tier, in the order the TypeScript app lists them.</summary>
    public static IReadOnlyList<Tier> All { get; } = [Tier.SuperUser, Tier.Store, Tier.Pa, Tier.Director, Tier.Emso];

    /// <summary>The stored value: "super_user", "store", "pa", "director", "emso".</summary>
    public static string Key(this Tier tier) => tier switch
    {
        Tier.SuperUser => SuperUserKey,
        Tier.Store => StoreKey,
        Tier.Pa => PaKey,
        Tier.Director => DirectorKey,
        Tier.Emso => EmsoKey,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    /// <summary>The label under the person's name (TIER_LABELS).</summary>
    public static string Label(this Tier tier) => tier switch
    {
        Tier.SuperUser => "Super User",
        Tier.Store => "Store",
        Tier.Pa => "PA (network read-only)",
        Tier.Director => "DSO (network read-only)",
        Tier.Emso => "EMSO (area)",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    /// <summary>The tier for a stored value; throws on anything else.</summary>
    public static Tier FromKey(string key) =>
        TryParse(key, out var tier) ? tier : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown tier");

    /// <summary>
    /// Reads a tier from its stored value ("super_user") or its enum name ("SuperUser"),
    /// ignoring case. "DSO" is accepted for Director.
    /// </summary>
    public static bool TryParse(string? value, out Tier tier)
    {
        tier = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        foreach (var t in All)
        {
            if (string.Equals(v, t.Key(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, t.ToString(), StringComparison.OrdinalIgnoreCase))
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

    /// <summary>PA and DSO never write (isReadOnlyTier). EMSO is deliberately not one.</summary>
    public static bool IsReadOnly(this Tier tier) => tier is Tier.Pa or Tier.Director;
}

/// <summary>
/// A person who may sign in, with their tier and the one scope column the tier needs
/// (the tier_scope check constraint): a store for Store, an area for EMSO, none for a
/// Super User. PA and DSO read the whole network; their region / director group is
/// informational (reporting remit only).
/// </summary>
public class AppUser
{
    public Guid Id { get; set; }
    /// <summary>Display handle, unique. For SSO users this is normally the UPN.</summary>
    public string Username { get; set; } = "";
    public Tier Tier { get; set; }
    public Guid? StoreId { get; set; }
    public Guid? RegionId { get; set; }
    public Guid? DirectorGroupId { get; set; }
    public Guid? AreaId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Single sign-on identity (this edition). Each is unique when present.
    /// <summary>Microsoft Entra ID object id ("oid" claim).</summary>
    public string? EntraObjectId { get; set; }
    /// <summary>User principal name ("upn"/"preferred_username"), stored lower-case.</summary>
    public string? UserPrincipalName { get; set; }
    /// <summary>Windows account, DOMAIN\user, stored lower-case.</summary>
    public string? WindowsAccount { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }

    public Store? Store { get; set; }
    public Region? Region { get; set; }
    public DirectorGroup? DirectorGroup { get; set; }
    public Area? Area { get; set; }
}

/// <summary>In-app alerts (e.g. the weekly EMSO unapproved-roster check).</summary>
public class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The Admin → Views matrix: which tabs each hierarchy level (SM, PA, EMSO, DSO) sees.
/// Presentation only; it never authorises.
/// </summary>
public class TabVisibility
{
    public string Level { get; set; } = "";
    public string TabKey { get; set; } = "";
    public bool Visible { get; set; } = true;
}
