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

public static class TierNames
{
    public const string SuperUser = "super_user", Store = "store", Pa = "pa", Director = "director", Emso = "emso";

    public static string ToDb(Tier tier) => tier switch
    {
        Tier.SuperUser => SuperUser,
        Tier.Store => Store,
        Tier.Pa => Pa,
        Tier.Director => Director,
        Tier.Emso => Emso,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    public static Tier FromDb(string value) => value switch
    {
        SuperUser => Tier.SuperUser,
        Store => Tier.Store,
        Pa => Tier.Pa,
        Director => Tier.Director,
        Emso => Tier.Emso,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown tier"),
    };

    public static bool TryParse(string? value, out Tier tier)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case SuperUser or "superuser" or "super user": tier = Tier.SuperUser; return true;
            case Store: tier = Tier.Store; return true;
            case Pa: tier = Tier.Pa; return true;
            case Director or "dso": tier = Tier.Director; return true;
            case Emso: tier = Tier.Emso; return true;
            default: tier = default; return false;
        }
    }
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
