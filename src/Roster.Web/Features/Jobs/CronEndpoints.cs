// Port of the HTTP side of src/app/api/cron/emso-check/route.ts and
// src/app/api/cron/keep-alive/route.ts: GET, protected by a bearer secret.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Roster.Web.Features.Jobs;

// Protected by the cron secret: the scheduler sends it as a Bearer token. Each route is
// denied unless the secret is configured and matches, so it can't be abused to spam
// notifications, and an unauthenticated endpoint that hits the database would be a free
// denial-of-wallet.
public static class CronEndpoints
{
    /// <summary>GET /api/cron/emso-check and GET /api/cron/keep-alive.</summary>
    public static IEndpointRouteBuilder MapCronEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ScheduledJobs.EmsoCheck.Path, (HttpContext http, CancellationToken ct) =>
                HandleAsync<EmsoCheckJob>(http, ct))
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(ScheduledJobs.KeepAlive.Path, (HttpContext http, CancellationToken ct) =>
                HandleAsync<KeepAliveJob>(http, ct))
            .AllowAnonymous()
            .ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>Check the bearer secret, run the job, answer with its JSON.</summary>
    public static async Task<IResult> HandleAsync<TJob>(HttpContext http, CancellationToken cancellationToken)
        where TJob : ICronJob
    {
        var services = http.RequestServices;
        var secret = ResolveSecret(services.GetRequiredService<IOptionsSnapshot<JobsOptions>>().Value,
            services.GetRequiredService<IConfiguration>());
        if (!IsAuthorized(secret, http.Request.Headers.Authorization.ToString()))
            return Results.Text("Unauthorized", "text/plain", Encoding.UTF8, StatusCodes.Status401Unauthorized);

        var outcome = await services.GetRequiredService<TJob>().RunAsync(cancellationToken);
        return Results.Json(outcome.Body, statusCode: outcome.StatusCode);
    }

    /// <summary>The configured secret: Roster:Jobs:CronSecret, else the CRON_SECRET variable.</summary>
    public static string? ResolveSecret(JobsOptions options, IConfiguration configuration)
    {
        var secret = options.CronSecret;
        return string.IsNullOrEmpty(secret) ? configuration["CRON_SECRET"] : secret;
    }

    /// <summary>
    /// `!secret || auth !== "Bearer " + secret`, inverted: allowed only when a secret is
    /// configured and the header is exactly "Bearer &lt;secret&gt;". Compared in constant time.
    /// </summary>
    public static bool IsAuthorized(string? secret, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(secret) || authorizationHeader is null) return false;
        var expected = Encoding.UTF8.GetBytes($"Bearer {secret}");
        var actual = Encoding.UTF8.GetBytes(authorizationHeader);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
