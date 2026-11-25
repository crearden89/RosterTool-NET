// New: the whole web app in memory, in a chosen environment and with chosen settings,
// for the sign-in, policy and kiosk tests.

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Roster.Web.Tests.Infrastructure;

public sealed partial class AppFactory(string environment, IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
        if (services is not null) builder.ConfigureTestServices(services);
    }

    /// <summary>A client that keeps cookies and shows redirects instead of following them.</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Follows redirects inside the app; stops at one that leaves it, or at a page.</summary>
    public static async Task<HttpResponseMessage> FollowAsync(HttpClient client, string url, int max = 6)
    {
        var response = await client.GetAsync(url);
        for (var i = 0; i < max && response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther; i++)
        {
            var next = response.Headers.Location!;
            if (next.IsAbsoluteUri && next.Host != client.BaseAddress!.Host) return response;
            response = await client.GetAsync(next);
        }
        return response;
    }

    /// <summary>The antiforgery token in a page's form.</summary>
    public static string AntiforgeryToken(string html) =>
        TokenPattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : throw new InvalidOperationException("No antiforgery token in the page.");

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"|value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"")]
    private static partial Regex TokenPattern();
}
