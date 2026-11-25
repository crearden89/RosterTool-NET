// Port of src/components/nav/navConfig.ts.

using Roster.Engine.Core;

namespace Roster.Web.Components.Layout;

public sealed record NavItem(string Label, string Href);

/// <param name="Tiers">The tier keys that see the section ("super_user"); null for every signed-in tier.</param>
public sealed record NavSection(string Section, IReadOnlyList<NavItem> Items, IReadOnlyList<string>? Tiers = null);

/// <summary>
/// The menu (spec Section 10), as the TypeScript app's
/// <c>src/components/nav/navConfig.ts</c> has it: the same sections, items,
/// order and addresses. The Super User section is additive. Stored tab
/// visibility rows key on the href, so the addresses never change.
/// </summary>
public static class NavConfig
{
    public static IReadOnlyList<NavSection> Nav { get; } =
    [
        new("Overview", [new("Dashboard", "/overview")]),
        // Requests (day-off requests and fixed rosters) moved into Team Members
        // (owner, 15/11/2026). /setup/requests stays as a redirect for old links.
        new("Setup", [new("Team Members", "/setup/staff")]),
        new("Plan & Execute",
        [
            new("Sales", "/plan/sales"),
            new("Non-Op", "/plan/non-op"),
            // A planning input, before Weekends: who is already booked off is what
            // the weekend-off switches get set around. The route stays
            // /admin/leave-liability; the page's own guard controls access.
            new("Leave Liability", "/admin/leave-liability"),
            // Here, not under Setup: the weekend-off switches are set for the
            // fortnight being built. The route stays /setup/weekends.
            new("Weekends", "/setup/weekends"),
            // Plan (the task view) and Roster (the staff view) are separate pages;
            // peers see each other's Plan, never each other's Roster.
            new("Plan", "/plan/generate"),
            // Plan -> Review -> Roster: the store checks and submits the plan, its
            // EMSO approves it, and only then can the roster be built.
            new("Review", "/plan/review"),
            new("Roster", "/plan/roster"),
        ]),
        new("Analysis",
        [
            new("Analysis", "/analysis"),
            // Store walk notes are raised and closed on the kiosk; this reads them back.
            new("Notes History", "/analysis/notes"),
        ]),
        new("Super User",
        [
            new("Uploads", "/admin/uploads"),
            new("Daily Variance Report", "/admin/variance-report"),
            new("Data Feed", "/admin/data-feed"),
            new("Till Allocation Preview", "/admin/till-preview"),
            new("Store Attributes", "/admin/store-attributes"),
            new("Special Trading Hours", "/admin/special-trading-hours"),
            new("Holiday / School Calendar", "/admin/calendars"),
            // Two grouped destinations with tabs inside; the old routes redirect.
            new("Rules", "/admin/rules/rostering"),
            new("Process", "/admin/process/planning"),
            new("Pay Rate Table", "/admin/pay-rates"),
            new("Non-Op Categories", "/admin/non-op-categories"),
            new("Load Types", "/admin/load-types"),
            new("Users", "/admin/users"),
            new("EMSO Managers", "/admin/emso"),
            new("Kiosk Links", "/admin/kiosk-links"),
            new("Views", "/admin/views"),
        ], [Tiers.SuperUser]),
    ];

    /// <summary>The tabs inside Rules.</summary>
    public static IReadOnlyList<NavItem> RulesTabs { get; } =
    [
        new("Rostering Rules", "/admin/rules/rostering"),
        new("Labour Group Rules", "/admin/rules/labour"),
        new("Break & Work Rules", "/admin/rules/breaks"),
    ];

    /// <summary>The tabs inside Process.</summary>
    public static IReadOnlyList<NavItem> ProcessTabs { get; } =
    [
        new("Planning Order", "/admin/process/planning"),
        new("Roster Order", "/admin/process/roster"),
    ];

    /// <summary>The Notifications item at the top of the menu (above the sections).</summary>
    public static NavItem Notifications { get; } = new("Notifications", "/notifications");

    /// <summary>
    /// The menu item (or Rules / Process tab, or Notifications) at an address, with
    /// its section; null when the address is not in the menu.
    /// </summary>
    public static (NavSection Section, NavItem Item)? Find(string path)
    {
        static bool Same(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        foreach (var s in Nav)
            foreach (var i in s.Items)
                if (Same(i.Href, path)) return (s, i);
        var superUser = Nav.First(s => s.Tiers is not null);
        foreach (var t in RulesTabs.Concat(ProcessTabs))
            if (Same(t.Href, path)) return (superUser, t);
        if (Same(Notifications.Href, path)) return (new NavSection("Notifications", [Notifications]), Notifications);
        return null;
    }

    /// <summary>The sections a tier sees (navForTier), by tier key.</summary>
    public static IReadOnlyList<NavSection> ForTier(string tier) =>
        Nav.Where(s => s.Tiers is null || s.Tiers.Contains(tier)).ToList();

    /// <summary>Every navigable tab, for the Views (visibility) matrix; the key is the href.</summary>
    public static IReadOnlyList<(string Key, string Label, string Section)> AllTabs { get; } =
        Nav.SelectMany(s => s.Items.Select(i => (i.Href, i.Label, s.Section))).ToList();

    /// <summary>
    /// Whether a menu item is the page you are on: its address, or a page under
    /// it ("/plan/sales" for "/plan/sales/adjust").
    /// </summary>
    public static bool IsActive(string path, string href) =>
        string.Equals(path, href, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(href + "/", StringComparison.OrdinalIgnoreCase);
}
