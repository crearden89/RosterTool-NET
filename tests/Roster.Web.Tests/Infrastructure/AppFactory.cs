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
    /// <summary>The directory the app signs people in against (in place of the database's).</summary>
    public FakeDirectory Directory { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(s =>
        {
            Directory.Register(s);
            services?.Invoke(s);
        });
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
    public static string AntiforgeryToken(string html)
    {
        var input = TokenInput().Match(html);
        if (!input.Success) throw new InvalidOperationException("No antiforgery token in the page.");
        return WebUtility.HtmlDecode(ValueAttribute().Match(input.Value).Groups[1].Value);
    }

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*>")]
    private static partial Regex TokenInput();

    [GeneratedRegex("value=\"([^\"]*)\"")]
    private static partial Regex ValueAttribute();
}
