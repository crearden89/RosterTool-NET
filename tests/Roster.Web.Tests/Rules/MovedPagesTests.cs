// New: the rules and process pages' old paths (src/app/(app)/admin/break-rules,
// labour-rules, rostering-rules, planning-order, roster-order) redirect permanently.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Roster.Web.Features.Rules;

namespace Roster.Web.Tests.Rules;

public class MovedPagesTests
{
    [Theory]
    [InlineData("/admin/break-rules", "/admin/rules/breaks")]
    [InlineData("/admin/labour-rules", "/admin/rules/labour")]
    [InlineData("/admin/rostering-rules", "/admin/rules/rostering")]
    public async Task An_old_rules_path_redirects_with_308(string from, string to)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        new RulesModule().Map(app);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(from);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(to, response.Headers.Location?.OriginalString);
    }
}
