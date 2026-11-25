// Port of src/app/(app)/setup/requests/redirectTarget.test.ts.

using Roster.Web.Features.Team;

namespace Roster.Web.Tests.Team;

// Old Requests links land on Team Members.
public sealed class RequestsRedirectTests
{
    [Fact]
    public void A_plain_link_opens_the_Requests_card() =>
        Assert.Equal("/setup/staff?section=requests", RequestsRedirect.Target());

    [Fact]
    public void Keeps_the_store_the_Dashboards_Needs_doing_link() =>
        Assert.Equal("/setup/staff?store=s1&section=requests", RequestsRedirect.Target(store: "s1"));

    [Fact]
    public void The_Fixed_tab_opens_the_Fixed_rosters_card() =>
        Assert.Equal("/setup/staff?store=s1&section=fixed", RequestsRedirect.Target(tab: "fixed", store: "s1"));

    [Fact]
    public void The_Fixed_tab_with_a_person_opens_that_persons_sheet() =>
        Assert.Equal("/setup/staff?store=s1&fixed=e1", RequestsRedirect.Target(tab: "fixed", employee: "e1", store: "s1"));

    [Fact]
    public void A_person_without_the_Fixed_tab_lands_on_Requests_as_the_old_page_did() =>
        Assert.Equal("/setup/staff?section=requests", RequestsRedirect.Target(employee: "e1"));

    [Fact]
    public void Encodes_what_it_passes_on() =>
        Assert.Equal("/setup/staff?fixed=a%26b%3Dc", RequestsRedirect.Target(tab: "fixed", employee: "a&b=c"));

    [Fact]
    public void Encodes_as_URLSearchParams_does() =>
        Assert.Equal("/setup/staff?fixed=a+b%21%7E*-._%C3%A9", RequestsRedirect.Target(tab: "fixed", employee: "a b!~*-._é"));
}
