// Port of the behaviour of src/components/nav/Sidebar.tsx, navConfig.ts and
// src/app/(app)/layout.tsx: sections by tier, the active item, the read-only line.

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Roster.Web.Components.Layout;
using Roster.Web.Components.Ui;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Layout;

public class ShellTests : UiTestContext
{
    [Fact]
    public void The_menu_has_every_page_of_the_typescript_app_in_order()
    {
        var items = NavConfig.Nav.SelectMany(s => s.Items.Select(i => $"{s.Section}|{i.Label}|{i.Href}")).ToList();
        Assert.Equal(27, items.Count);
        Assert.Equal("Overview|Dashboard|/overview", items[0]);
        Assert.Equal("Plan & Execute|Leave Liability|/admin/leave-liability", items[4]);
        Assert.Equal("Plan & Execute|Roster|/plan/roster", items[8]);
        Assert.Equal("Super User|Rules|/admin/rules/rostering", items[18]);
        Assert.Equal("Super User|Views|/admin/views", items[26]);
        Assert.Equal(3, NavConfig.RulesTabs.Count);
        Assert.Equal(2, NavConfig.ProcessTabs.Count);
    }

    [Theory]
    [InlineData("super_user", 5)]
    [InlineData("store", 4)]
    [InlineData("emso", 4)]
    [InlineData("pa", 4)]
    [InlineData("director", 4)]
    public void Only_a_super_user_sees_the_super_user_section(string tier, int sections) =>
        Assert.Equal(sections, NavConfig.ForTier(tier).Count);

    [Theory]
    [InlineData("/plan/sales", "/plan/sales", true)]
    [InlineData("/plan/sales/adjust", "/plan/sales", true)]
    [InlineData("/plan/salesx", "/plan/sales", false)]
    [InlineData("/admin/rules/labour", "/admin/rules/rostering", false)]
    public void An_item_is_active_on_its_page_and_the_pages_under_it(string path, string href, bool active) =>
        Assert.Equal(active, NavConfig.IsActive(path, href));

    [Fact]
    public void Sidebar_marks_the_page_you_are_on_and_hides_the_super_user_section_from_a_store()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("plan/sales");
        var cut = Render<Sidebar>(p => p.Add(s => s.Name, "Jordan Lee").Add(s => s.Tier, "store").Add(s => s.UnreadCount, 3));

        var current = cut.FindAll("a[aria-current=page]");
        Assert.Single(current);
        Assert.Equal("Sales", current[0].TextContent.Trim());
        Assert.Contains("bg-[var(--ui-lavender)] font-semibold", current[0].ClassName);
        Assert.DoesNotContain(cut.FindAll("nav p"), p => p.TextContent == "Super User");
        Assert.Equal("Jordan Lee", cut.Find("aside p.truncate").TextContent);
        Assert.Contains(cut.FindAll("aside p"), p => p.TextContent == "Store");
        // The unread count: on the menu's Notifications item and in the phone bar.
        Assert.Equal("3 unread notifications", cut.Find("header a").GetAttribute("aria-label"));
        Assert.Contains(cut.FindAll("nav a span"), s => s.TextContent == "3");
    }

    [Fact]
    public void Sidebar_shows_a_super_user_every_section()
    {
        var cut = Render<Sidebar>(p => p.Add(s => s.Name, "Sam Admin").Add(s => s.Tier, "super_user"));
        var sections = cut.FindAll("nav p").Select(p => p.TextContent).ToList();
        Assert.Equal(["Overview", "Setup", "Plan & Execute", "Analysis", "Super User"], sections);
        Assert.Empty(cut.FindAll("header a"));
    }

    [Fact]
    public void The_phone_menu_opens_and_closes()
    {
        var cut = Render<Sidebar>(p => p.Add(s => s.Name, "Jordan Lee").Add(s => s.Tier, "store"));
        Assert.Contains("-translate-x-full", cut.Find("aside").ClassName);
        cut.Find("button[aria-label='Open navigation']").Click();
        Assert.Contains("translate-x-0", cut.Find("aside").ClassName);
        Assert.Equal("true", cut.Find("button[aria-label='Open navigation']").GetAttribute("aria-expanded"));
        cut.Find("button[aria-label='Close navigation']").Click();
        Assert.Contains("-translate-x-full", cut.Find("aside").ClassName);
    }

    [Theory]
    [InlineData("pa", false)]
    [InlineData("director", false)]
    [InlineData("store", true)]
    [InlineData("emso", true)]
    public void Read_only_tiers_see_no_write_controls(string tier, bool shown)
    {
        SignIn(tier);
        var cut = Render<WriteGate>(p => p.AddChildContent("<button>Save</button>"));
        Assert.Equal(shown, cut.FindAll("button").Count == 1);
    }

    [Fact]
    public void A_write_tier_without_write_access_to_the_store_sees_no_write_controls()
    {
        SignIn("store");
        var cut = Render<WriteGate>(p => p.Add(g => g.CanWrite, false).Add(g => g.ReadOnly, "<p>View only</p>").AddChildContent("<button>Save</button>"));
        Assert.Empty(cut.FindAll("button"));
        Assert.Equal("View only", cut.Find("p").TextContent);
    }
}

public class StoreSelectionTests
{
    [Fact]
    public void The_store_chosen_last_is_remembered_for_the_data_layer()
    {
        var selection = new StoreSelection();
        var remembered = (Roster.Data.Scope.IRememberedStore)selection;
        Assert.Null(remembered.StoreId);
        selection.Load("not a store");
        Assert.Null(remembered.StoreId);
        var store = Guid.NewGuid();
        selection.Load(store.ToString());
        Assert.Equal(store, remembered.StoreId);
        // A cookie read later never undoes a choice made on this connection.
        var chosen = Guid.NewGuid();
        selection.Remember(chosen);
        selection.Load(Guid.NewGuid().ToString());
        Assert.Equal(chosen, remembered.StoreId);
    }
}
