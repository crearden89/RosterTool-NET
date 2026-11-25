// Port of src/lib/constants.ts.
// Shared constants for the roster system. Brand palette + labour-group legend
// (spec Section 2) are locked and reused identically across every screen - keep
// this the single source of truth.

namespace Roster.Engine.Core;

public static class Brand
{
    // Official brand hex values (spec Section 2).
    public const string Navy = "#00005F";
    public const string LightBlue = "#5AC5F0";
    public const string Red = "#D70000";
    public const string Orange = "#FF7801";
    public const string Yellow = "#FFC800";
    public const string White = "#FFFFFF";
}

// ─── Labour-group colour legend (spec Section 2) ─────────────────────────────
// Registers + SCOs are shown combined as "Checkout" in summary contexts using a
// split-colour swatch (half navy / half light-blue). The eight-group legend is
// the canonical set; Checkout is the merged summary of Registers + SCOs.
public sealed record LabourGroup(string Key, string Label, string Color, string Abbrev);

public static class LabourGroups
{
    // Colour scheme aligned to the client's current tool: Registers light green,
    // SCOs dark green, Load Running navy, Reset light blue, Admin purple,
    // Specials yellow, Cleaning orange, Non-Op grey (Breaks render red on the
    // grid - see the roster grid's tile map).
    public static readonly IReadOnlyList<LabourGroup> All =
    [
        new("registers", "Registers", "#8CC63F", "REG"),
        new("scos", "SCOs", "#1E7A34", "SCO"),
        new("load_running", "Load Running", Brand.Navy, "LR"),
        new("reset", "Reset", Brand.LightBlue, "RST"),
        new("admin", "Admin", "#7D3F98", "ADM"),
        new("specials", "Specials", Brand.Yellow, "SPC"),
        new("cleaning", "Cleaning", Brand.Orange, "CLN"),
        new("non_op", "Non-Op", "#8A8F98", "NOP"),
    ];

    public static readonly IReadOnlyDictionary<string, LabourGroup> ByKey = All.ToDictionary(g => g.Key);

    // The "Checkout" merged group (Registers + SCOs) as shown in summary tables.
    public const string CheckoutKey = "checkout";
    public const string CheckoutLabel = "Checkout";
    public static readonly string CheckoutColorLeft = ByKey["registers"].Color; // Registers half of the split swatch
    public static readonly string CheckoutColorRight = ByKey["scos"].Color; // SCOs half
    public const string CheckoutAbbrev = "CO";
}

// ─── Org / roles ─────────────────────────────────────────────────────────────
public static class AuStates
{
    public static readonly IReadOnlyList<string> All = ["NSW", "VIC", "QLD", "SA", "WA", "TAS", "NT", "ACT"];
}

/// <summary>Role types (spec Section 3). Manager-tier = SM/ASM/DSM/RSM.</summary>
public static class Roles
{
    public const string SM = "SM";
    public const string ASM = "ASM";
    public const string DSM = "DSM";
    public const string RSM = "RSM";
    public const string SA = "SA";
    public const string JSA = "JSA";

    public static readonly IReadOnlyList<string> All = [SM, ASM, DSM, RSM, SA, JSA];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [SM] = "Store Manager",
        [ASM] = "Assistant Store Manager",
        [DSM] = "Duty Store Manager",
        [RSM] = "Relieving Shift Manager",
        [SA] = "Store Assistant",
        [JSA] = "Junior Store Assistant",
    };

    public static readonly IReadOnlyList<string> Managers = [SM, ASM, DSM, RSM];

    public static bool IsManager(string role) => role is SM or ASM or DSM or RSM;

    // Tenure-year bands. SM runs 1/2/3/4/7; ASM runs 1/2.
    public static readonly IReadOnlyList<int> SmYearBands = [1, 2, 3, 4, 7];
    public static readonly IReadOnlyList<int> AsmYearBands = [1, 2];

    // Roles whose pay is banded by tenure year. The employee's band lives on
    // employees.sm_year_band (shared column, used for both SM and ASM).
    public static readonly IReadOnlyList<string> YearBandedRoles = [SM, ASM];

    public static IReadOnlyList<int> YearBandsForRole(string role) =>
        role == SM ? SmYearBands : role == ASM ? AsmYearBands : [];
}

// ─── Positions ───────────────────────────────────────────────────────────────
// A POSITION is what a team member actually is on the payroll - the finest
// grain that carries its own hourly rate. Each resolves to a base ROLE (which
// is what the engines reason about: manager tier, break entitlement, JSA
// school rules), plus - for the manager positions - a contract size and a
// tenure year.
//
// The position CODE is also the pay_rates key, so the rate grid and the team
// list can never drift apart. Codes are shaped `<ROLE>[-<weekly hours>]-<year>`
// (SM-45-3, ASM-32-1) or a plain variant suffix (SA-FF, JSA-16).
//
// The catalogue lives in the `positions` table so a super user can extend it
// without a deploy; this list is the shipped seed and the fallback shape.
public sealed record PositionDef(
    string Code,
    string Label,
    string BaseRole,
    double? WeeklyHours, // contract size implied (null = set per person)
    int? YearBand,
    // False for RSM, which has no rate of its own - it is paid the Store
    // Assistant rate plus the per-hour sole-manager allowance.
    bool HasRate);

public static class Positions
{
    public static readonly IReadOnlyList<int> AsmHourBands = [30, 32, 35, 40, 45];
    public static readonly IReadOnlyList<int> SmHourBands = [40, 45, 50];

    public static readonly IReadOnlyList<PositionDef> All = Build();

    private static List<PositionDef> Build()
    {
        var list = new List<PositionDef>
        {
            new("JSA-16", "JSA 16 year old", Roles.JSA, null, null, true),
            new("JSA-17", "JSA 17 year old", Roles.JSA, null, null, true),
            new("SA-FF", "Store Assistant Fully Flexible", Roles.SA, null, null, true),
            new("SA-LR", "Store Assistant Limited Roster", Roles.SA, null, null, true),
            new("DSM", "DSM", Roles.DSM, null, null, true),
            new("RSM", "Relieving Shift Manager", Roles.RSM, null, null, false),
        };
        foreach (var h in AsmHourBands)
            foreach (var y in Roles.AsmYearBands)
                list.Add(new($"ASM-{h}-{y}", $"ASM {h} hour year {y}", Roles.ASM, h, y, true));
        foreach (var h in SmHourBands)
            foreach (var y in Roles.SmYearBands)
                list.Add(new($"SM-{h}-{y}", $"SM {h} hour year {y}", Roles.SM, h, y, true));
        return list;
    }

    // The Store Assistant rate an RSM is paid (plus the sole-manager allowance).
    public const string RsmRatePosition = "SA-FF";

    // Positions that own a column in the pay-rate grid.
    public static readonly IReadOnlyList<PositionDef> RatePositions = All.Where(p => p.HasRate).ToList();

    public static readonly IReadOnlyDictionary<string, PositionDef> ByCode = All.ToDictionary(p => p.Code);

    // Contract hours are stored per FORTNIGHT; a position's size is quoted weekly.
    public static double? FortnightHoursFor(double? weeklyHours) => weeklyHours is null ? null : weeklyHours * 2;

    // The position a plain role defaults to - used when costing something that is
    // not a real person (the Ideal Floor prices its shifts at these).
    private static readonly IReadOnlyDictionary<string, string> DefaultPosition = new Dictionary<string, string>
    {
        [Roles.SM] = "SM-45-1",
        [Roles.ASM] = "ASM-40-1",
        [Roles.DSM] = "DSM",
        [Roles.RSM] = "RSM",
        [Roles.SA] = "SA-FF",
        [Roles.JSA] = "JSA-16",
    };

    public static string DefaultPositionFor(string role) => DefaultPosition.GetValueOrDefault(role, role);

    // LEGACY pay key from role + tenure band, for employees whose position hasn't
    // been set. Resolves onto position codes at the role's default contract size.
    public static string PayRoleKeyFor(string role, double? band)
    {
        if (role == Roles.SM) return $"SM-45-{Js.NumberToString(band ?? 1)}";
        if (role == Roles.ASM) return $"ASM-40-{Js.NumberToString(band ?? 1)}";
        if (role == Roles.SA) return "SA-FF";
        if (role == Roles.JSA) return "JSA-16";
        return role;
    }

    // The pay_rates key for a costable shift. The employee's position is the
    // authority; role+band is the fallback for rows predating positions. RSM never
    // has its own rate - it always prices at the Store Assistant rate.
    public static string PayKeyFor(string? positionCode, string role, double? band = null)
    {
        if (role == Roles.RSM) return RsmRatePosition;
        return string.IsNullOrEmpty(positionCode) ? PayRoleKeyFor(role, band) : positionCode;
    }
}

public static class StoreIds
{
    // Store ID canonical format: "G" + zero-padded 3-digit store number (e.g. 25 →
    // "G025"). Accepts a raw number, "025", or an existing "G025" and normalises.
    public static string FormatStoreId(string input)
    {
        var s = input.Trim().ToUpperInvariant();
        if (s.Length == 0) return "";
        // Drop a decimal tail first - spreadsheet exports sometimes emit an integer
        // store number column as "25.0", which would otherwise pad into "G250".
        var intPart = s.Split('.')[0];
        var digits = new string(intPart.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return s; // nothing numeric to format - leave as typed
        return $"G{Js.PadStart(digits, 3, '0')}";
    }

    public static string FormatStoreId(double input) => FormatStoreId(Js.NumberToString(input));
}

// Operational hierarchy levels used by the Views (visibility) matrix. These are
// distinct from the auth access tiers below - they describe org roles (Store
// Manager, Personal Assistant, EMSO, DSO) whose tab visibility a super user
// configures.
public static class HierarchyLevels
{
    public static readonly IReadOnlyList<string> All = ["SM", "PA", "EMSO", "DSO"];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["SM"] = "Store Manager",
        ["PA"] = "Personal Assistant",
        ["EMSO"] = "EMSO",
        ["DSO"] = "DSO",
    };
}

// ─── Access tiers (spec Section 9) ───────────────────────────────────────────
public static class Tiers
{
    public const string SuperUser = "super_user";
    public const string Store = "store";
    public const string Pa = "pa";
    public const string Director = "director";
    public const string Emso = "emso";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [SuperUser] = "Super User",
        [Store] = "Store",
        [Pa] = "PA (network read-only)",
        [Director] = "DSO (network read-only)",
        [Emso] = "EMSO (area)",
    };
}

public static class Auth
{
    // Synthetic email domain - usernames map to <username>@DOMAIN for sign-in.
    public const string AuthEmailDomain = "roster.internal";

    public static string UsernameToEmail(string username) => $"{username.Trim().ToLowerInvariant()}@{AuthEmailDomain}";
}

// ─── Upload types ────────────────────────────────────────────────────────────
public static class UploadTypes
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["benchmark"] = "Benchmark",
        ["actuals"] = "Actuals",
        ["leave_liability"] = "Leave Liability",
        ["leave_requests"] = "Leave Requests",
        ["contacts"] = "UKG Contacts",
        ["pos_quarter_hour"] = "POS Quarter-Hour",
    };
}

public static class PlanningConstants
{
    // The 90% planning transform (spec 4.1): file hours are at 100%; divide by 0.9.
    public const double PlanningFactor = 0.9;
}
