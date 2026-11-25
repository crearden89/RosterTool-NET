// New: GET /api/cron/emso-check and /api/cron/keep-alive answer as the TypeScript routes
// did: 401 "Unauthorized" without the right bearer secret, else the job's JSON.
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Data.Jobs;
using Roster.Web.Features.Jobs;
using Roster.Web.Tests.Infrastructure;

namespace Roster.Web.Tests.Jobs;

public sealed class CronEndpointTests
{
    private const string Secret = "test-cron-secret";

    private sealed class FakeCheck : IEmsoRosterCheck
    {
        public int Runs;
        public Task<EmsoCheckResult> RunEmsoUnapprovedCheckAsync(CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult(new EmsoCheckResult(2, 1, 0));
        }
    }

    private sealed class FakeHeartbeat(bool lands = true) : ICronHeartbeat
    {
        public readonly List<(string Job, string? Json)> Beats = [];
        public Task<bool> RecordCronRunAsync(string job, object? result = null, CancellationToken cancellationToken = default)
        {
            Beats.Add((job, CronResultJson.Serialize(result)));
            return Task.FromResult(lands);
        }
    }

    private sealed class FakeKeepAlive(KeepAliveResult result) : IKeepAlive
    {
        public Task<KeepAliveResult> TouchAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private static AppFactory App(string? secret, FakeCheck check, FakeHeartbeat beat, KeepAliveResult? keepAlive = null, string secretKey = "Roster:Jobs:CronSecret")
    {
        var settings = new Dictionary<string, string?> { ["Roster:Jobs:RunScheduler"] = "false" };
        if (secret is not null) settings[secretKey] = secret;
        return new AppFactory("Development", settings, s =>
        {
            s.RemoveAll<IEmsoRosterCheck>();
            s.RemoveAll<ICronHeartbeat>();
            s.RemoveAll<IKeepAlive>();
            s.AddSingleton<IEmsoRosterCheck>(check);
            s.AddSingleton<ICronHeartbeat>(beat);
            s.AddSingleton<IKeepAlive>(new FakeKeepAlive(keepAlive ?? KeepAliveResult.Success(6)));
        });
    }

    private static HttpRequestMessage Get(string path, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    [Theory]
    [InlineData(null, "Bearer test-cron-secret")] // no secret configured: refuse everything
    [InlineData("", "Bearer ")]
    [InlineData(Secret, null)]
    [InlineData(Secret, "Bearer wrong")]
    [InlineData(Secret, "bearer test-cron-secret")]
    [InlineData(Secret, "Bearer test-cron-secret ")]
    [InlineData(Secret, "test-cron-secret")]
    public void Only_the_exact_bearer_secret_is_accepted(string? secret, string? header) =>
        Assert.False(CronEndpoints.IsAuthorized(secret, header));

    [Fact]
    public void The_exact_bearer_secret_is_accepted() =>
        Assert.True(CronEndpoints.IsAuthorized(Secret, "Bearer test-cron-secret"));

    [Theory]
    [InlineData("/api/cron/emso-check")]
    [InlineData("/api/cron/keep-alive")]
    public async Task Without_the_secret_the_routes_answer_401_and_run_nothing(string path)
    {
        var check = new FakeCheck();
        var beat = new FakeHeartbeat();
        await using var app = App(Secret, check, beat);
        var client = app.Browser();

        var response = await client.SendAsync(Get(path, "Bearer nope"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Unauthorized", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, check.Runs);
        Assert.Empty(beat.Beats);
    }

    [Fact]
    public async Task With_no_secret_configured_even_a_bearer_is_refused()
    {
        var check = new FakeCheck();
        await using var app = App(null, check, new FakeHeartbeat());
        var response = await app.Browser().SendAsync(Get("/api/cron/emso-check", "Bearer "));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, check.Runs);
    }

    [Fact]
    public async Task The_emso_check_runs_records_its_heartbeat_and_answers_with_its_counts()
    {
        var check = new FakeCheck();
        var beat = new FakeHeartbeat();
        await using var app = App(Secret, check, beat);

        var response = await app.Browser().SendAsync(Get("/api/cron/emso-check", $"Bearer {Secret}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"ok":true,"created":2,"updated":1,"failed":0,"heartbeat":true}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, check.Runs);
        Assert.Equal([("emso-check", """{"created":2,"updated":1,"failed":0}""")], beat.Beats);
    }

    [Fact]
    public async Task The_typescript_apps_cron_secret_variable_is_read_too()
    {
        var check = new FakeCheck();
        await using var app = App(Secret, check, new FakeHeartbeat(lands: false), secretKey: "CRON_SECRET");

        var response = await app.Browser().SendAsync(Get("/api/cron/emso-check", $"Bearer {Secret}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true,"created":2,"updated":1,"failed":0,"heartbeat":false}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_keep_alive_counts_the_stores_and_records_its_heartbeat()
    {
        var beat = new FakeHeartbeat();
        await using var app = App(Secret, new FakeCheck(), beat, KeepAliveResult.Success(6));

        var response = await app.Browser().SendAsync(Get("/api/cron/keep-alive", $"Bearer {Secret}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true,"stores":6,"heartbeat":true}""", await response.Content.ReadAsStringAsync());
        Assert.Equal([("keep-alive", """{"stores":6}""")], beat.Beats);
    }

    [Fact]
    public async Task A_keep_alive_that_cannot_read_answers_500_with_the_error_and_no_heartbeat()
    {
        var beat = new FakeHeartbeat();
        await using var app = App(Secret, new FakeCheck(), beat, KeepAliveResult.Failure("connection refused"));

        var response = await app.Browser().SendAsync(Get("/api/cron/keep-alive", $"Bearer {Secret}"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"ok":false,"error":"connection refused"}""", await response.Content.ReadAsStringAsync());
        Assert.Empty(beat.Beats);
    }

    [Fact]
    public async Task The_scheduler_runs_a_job_in_its_own_scope_and_never_throws()
    {
        var check = new FakeCheck();
        var beat = new FakeHeartbeat();
        await using var app = App(Secret, check, beat);
        var service = new CronJobHostedService<EmsoCheckJob>(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JobsOptions>>(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CronJobHostedService<EmsoCheckJob>>.Instance);

        var outcome = await service.RunOnceAsync(ScheduledJobs.EmsoCheck, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome.Ok);
        Assert.Equal(1, check.Runs);
        Assert.Single(beat.Beats);
    }

    [Fact]
    public void The_scheduler_is_registered_unless_turned_off()
    {
        static int Count(IReadOnlyDictionary<string, string?> settings)
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            new JobsModule().Register(services, configuration);
            return services.Count(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && d.ImplementationType is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(CronJobHostedService<>));
        }

        Assert.Equal(2, Count(new Dictionary<string, string?>()));
        Assert.Equal(0, Count(new Dictionary<string, string?> { ["Roster:Jobs:RunScheduler"] = "false" }));
    }

    [Fact]
    public void The_seed_switch_is_read_from_the_command_line_or_the_settings()
    {
        Assert.Equal(DevSeedRequest.None, DevSeedStartup.RequestFrom(["Roster.Web.dll"], new DevSeedSettings()));
        Assert.Equal(DevSeedRequest.Seed, DevSeedStartup.RequestFrom(["Roster.Web.dll", "--seed"], new DevSeedSettings()));
        Assert.Equal(DevSeedRequest.Seed, DevSeedStartup.RequestFrom(["Roster.Web.dll"], new DevSeedSettings { OnStartup = true }));
        Assert.Equal(DevSeedRequest.Reseed, DevSeedStartup.RequestFrom(["--seed", "--reseed"], new DevSeedSettings()));
        Assert.Equal(DevSeedRequest.None, DevSeedStartup.RequestFrom(["--seeds"], new DevSeedSettings()));
    }
}
